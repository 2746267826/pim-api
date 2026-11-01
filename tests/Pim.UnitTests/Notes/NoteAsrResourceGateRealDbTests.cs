using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

[Trait("DataSource", "RealDb")]
public sealed class NoteAsrResourceGateRealDbTests
{
    private static readonly string StorageRoot = Path.Combine(Path.GetTempPath(), "pim-asr516-resource-gate-tests");

    [SkippableFact]
    public async Task Resource_gate_decisions_are_durable_idempotent_owner_bound_and_capped()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_asr516_resource_gate");
        await database.MigrateAsync();

        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var inputSha = new string('a', 64);
        var now = DateTimeOffset.UtcNow;
        var tasks = new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot }));

        database.Db.Users.AddRange(User(owner), User(otherOwner));
        database.Db.NoteAsrNodes.Add(Node(owner, nodeId, now));
        database.Db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
        {
            Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = noteId, VersionId = versionId,
            InputSha256 = inputSha, Route = NoteAsrRoute.Local, State = "Queued", Language = "zh",
            ExpectedSegments = 3, LocalModelId = "local-model", LocalModelRevision = "r1",
            ConfigRevision = Guid.NewGuid(), ConsentVersion = "local-consent"
        });
        await database.Db.SaveChangesAsync();
        await WriteTaskAsync(owner, taskId, attemptId, versionId, noteId, inputSha);

        var service = Service(database.Db, owner, tasks, maxAutomaticFailures: 2);
        var firstRequest = Claim(nodeId, "auto-1", automatic: true, passed: false,
            ["resource-vram-insufficient"]);
        var first = await service.ClaimAsync(firstRequest, CancellationToken.None);
        Assert.Equal("resource_blocked", first.Status);
        Assert.Equal(1, first.ConsecutiveAutomaticResourceFailures);
        Assert.Null(first.LeaseId);

        var replay = await service.ClaimAsync(firstRequest, CancellationToken.None);
        Assert.Equal("resource_blocked", replay.Status);
        Assert.Equal(1, replay.ConsecutiveAutomaticResourceFailures);

        var capped = await service.ClaimAsync(Claim(nodeId, "auto-2", automatic: true, passed: false,
            ["resource-vram-insufficient"]), CancellationToken.None);
        Assert.Equal("manual_required", capped.Status);
        Assert.True(capped.ManualTriggerRequired);
        Assert.Equal(2, capped.ConsecutiveAutomaticResourceFailures);

        var nodes = await service.ListNodesAsync(CancellationToken.None);
        var listed = Assert.Single(nodes);
        Assert.Equal(2, listed.ConsecutiveAutomaticResourceFailures);
        Assert.True(listed.AutomaticProcessingManualRequired);
        Assert.Equal(new[] { "resource-vram-insufficient" }, listed.ResourceGateFailureReasons);

        var otherService = Service(database.Db, otherOwner, tasks, maxAutomaticFailures: 2);
        var crossOwner = await Assert.ThrowsAsync<DomainException>(() => otherService.ClaimAsync(
            Claim(nodeId, "foreign", automatic: true, passed: false, ["resource-vram-insufficient"]),
            CancellationToken.None));
        Assert.Equal(40401, crossOwner.ErrorCode);
        Assert.Empty(await otherService.ListNodesAsync(CancellationToken.None));

        var manualBlocked = await service.ClaimAsync(Claim(nodeId, "manual-blocked", automatic: false,
            passed: false, ["resource-power-on-battery"]), CancellationToken.None);
        Assert.Equal("resource_blocked", manualBlocked.Status);
        Assert.False(manualBlocked.ManualTriggerRequired);
        Assert.Equal(2, manualBlocked.ConsecutiveAutomaticResourceFailures);

        var manualReady = await service.ClaimAsync(Claim(nodeId, "manual-ready", automatic: false,
            passed: true, []), CancellationToken.None);
        Assert.Equal("claimed", manualReady.Status);
        Assert.NotNull(manualReady.Lease);

        for (var index = 0; index < 3; index++)
        {
            await service.AcknowledgeAsync(attemptId, new NoteAsrAckRequest(
                manualReady.Lease!.LeaseId, manualReady.Lease.LeaseToken, manualReady.Lease.FencingToken,
                "segment", "Succeeded", index, $"synthetic-{index}", null, null,
                $"synthetic transcript {index}"), CancellationToken.None);
        }
        await service.AcknowledgeAsync(attemptId, new NoteAsrAckRequest(
            manualReady.Lease!.LeaseId, manualReady.Lease.LeaseToken, manualReady.Lease.FencingToken,
            "complete", "Succeeded", 0, "synthetic-terminal", null, null), CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        var storedNode = await database.Db.NoteAsrNodes.AsNoTracking().SingleAsync(x => x.Id == nodeId);
        Assert.Equal(0, storedNode.ConsecutiveAutomaticResourceFailures);
        Assert.Equal("recovered", storedNode.ResourceGateStatus);
        Assert.Equal("[]", storedNode.ResourceGateFailureReasonsJson);
        Assert.Equal(3, await database.Db.NoteAsrResourceDecisions.CountAsync(x => x.OwnerId == owner));
        Assert.Equal(0, await database.Db.NoteAsrResourceDecisions.CountAsync(x => x.OwnerId == otherOwner));
        CleanupTaskFile(owner);
    }

    [SkippableFact]
    public async Task Unconfigured_cap_does_not_invent_a_default_and_legacy_claims_remain_compatible()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_asr516_resource_uncapped");
        await database.MigrateAsync();

        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var inputSha = new string('b', 64);
        var now = DateTimeOffset.UtcNow;
        var tasks = new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot }));
        database.Db.Users.Add(User(owner));
        database.Db.NoteAsrNodes.Add(Node(owner, nodeId, now));
        database.Db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
        {
            Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = noteId, VersionId = versionId,
            InputSha256 = inputSha, Route = NoteAsrRoute.Local, State = "Queued", Language = "zh",
            ExpectedSegments = 3, LocalModelId = "local-model", LocalModelRevision = "r1",
            ConfigRevision = Guid.NewGuid(), ConsentVersion = "local-consent"
        });
        await database.Db.SaveChangesAsync();
        await WriteTaskAsync(owner, taskId, attemptId, versionId, noteId, inputSha);

        var service = Service(database.Db, owner, tasks, maxAutomaticFailures: null);
        var failure = await service.ClaimAsync(Claim(nodeId, "auto-uncapped", automatic: true,
            passed: false, ["resource-memory-insufficient"]), CancellationToken.None);
        Assert.Equal("resource_blocked", failure.Status);
        Assert.False(failure.ManualTriggerRequired);
        Assert.Equal(1, failure.ConsecutiveAutomaticResourceFailures);

        var legacy = await service.ClaimAsync(new ClaimNoteAsrTaskRequest(nodeId, "legacy-client"),
            CancellationToken.None);
        Assert.Equal("claimed", legacy.Status);
        Assert.NotNull(legacy.Lease);
        CleanupTaskFile(owner);
    }

    private static ClaimNoteAsrTaskRequest Claim(Guid nodeId, string key, bool automatic, bool passed,
        IReadOnlyList<string> reasons) => new(nodeId, key, 120,
        new NoteAsrResourceGateDecision(automatic, passed, reasons, DateTimeOffset.UtcNow));

    private static NoteAsrRoutingService Service(PimDbContext db, Guid owner, NoteAsrTaskStore tasks,
        int? maxAutomaticFailures)
    {
        var user = new FixedUser(owner);
        var materialOptions = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        return new NoteAsrRoutingService(db, user, new NoteMaterialUploadStore(materialOptions),
            new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), tasks,
            new NoteAsrEvidenceService(db, tasks, user), new PassthroughSecrets(),
            Options.Create(new NoteAsrRoutingOptions { MaxAutomaticResourceFailures = maxAutomaticFailures }));
    }

    private static UserEntity User(Guid id) => new()
    {
        Id = id, Username = "synthetic-" + id.ToString("N")[..10],
        Email = id.ToString("N")[..10] + "@example.invalid", PasswordHash = "synthetic", IsActive = true
    };

    private static NoteAsrNodeEntity Node(Guid owner, Guid id, DateTimeOffset now) => new()
    {
        Id = id, OwnerId = owner, NodeId = "synthetic-node", Enabled = true,
        ModelId = "local-model", ModelRevision = "r1", ModelVerified = true,
        CapabilitiesJson = "[]", LastHeartbeatAt = now
    };

    private static async Task WriteTaskAsync(Guid owner, Guid taskId, Guid attemptId, Guid versionId,
        Guid noteId, string inputSha)
    {
        var state = new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, versionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('c', 64), NoteId = noteId, InputSha256 = inputSha,
            ManifestSha256 = new string('d', 64), AttemptId = attemptId,
            Steps = Enumerable.Range(0, 3).Select(index => new NoteAsrStep
            {
                Index = index, RecordingId = Guid.NewGuid(), RecordingSegment = 1, ChunkIndex = index,
                FileIndex = index, Path = $"recordings/s0001-c{index:D6}.wav", Sha256 = new string('e', 64),
                SourceOffset = 0, RecordingOffset = index, TimelineOffset = index, Samples = 1_000,
                Attempts = [new()]
            }).ToList()
        };
        var directory = Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, taskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void CleanupTaskFile(Guid owner)
    {
        var directory = Path.GetFullPath(Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D")));
        var root = Path.GetFullPath(Path.Combine(StorageRoot, "asr-tasks"));
        if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Role => "user";
    }

    private sealed class PassthroughSecrets : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
