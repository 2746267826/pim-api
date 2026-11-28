using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

/// <summary>
/// Focused real-PostgreSQL proof that public Batch submission fails closed for a non-canonical
/// initial task or a stale route-config consent version. Each test creates a disposable migrated
/// database from PIM_TEST_CONN; no provider or production database is used.
/// </summary>
public sealed class NoteAsrCanonicalBatchTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_initial_submit_rejects_non_canonical_duplicate_without_job_or_provider(
        bool hasDuplicateAttempt)
    {
        await using var seeded = await SeedAsync(hasDuplicateAttempt);
        var request = BatchRequest(seeded, seeded.DuplicateTaskId,
            seeded.DuplicateState.Revision, seeded.ConsentVersion);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            seeded.Service.StartFromTaskAsync(request, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        await seeded.AssertNoSubmissionAsync();
    }

    [SkippableFact]
    public async Task Batch_initial_submit_rejects_stale_route_consent_without_job_or_provider()
    {
        await using var seeded = await SeedAsync();
        await using (var db = seeded.NewDb())
        {
            var config = await db.NoteAsrRouteConfigs.SingleAsync(x => x.OwnerId == seeded.Owner);
            config.ConsentVersion = "consent-v2";
            await db.SaveChangesAsync();
        }
        var request = BatchRequest(seeded, seeded.CanonicalTaskId,
            seeded.CanonicalState.Revision, seeded.ConsentVersion);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            seeded.Service.StartFromTaskAsync(request, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        await seeded.AssertNoSubmissionAsync();
    }

    [SkippableFact]
    public async Task Batch_jsonl_submit_rejects_stale_route_consent_without_job_or_provider()
    {
        await using var seeded = await SeedAsync();
        await using (var db = seeded.NewDb())
        {
            var config = await db.NoteAsrRouteConfigs.SingleAsync(x => x.OwnerId == seeded.Owner);
            config.ConsentVersion = "consent-v2";
            await db.SaveChangesAsync();
        }
        await using var jsonl = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(seeded.Owner,
            seeded.VersionId, seeded.Steps, NoteAsrPlan.SamplesPerCall, "zh", seeded.Model,
            seeded.PromptText, seeded.Materials,
            128 * 1024 * 1024,
            Enumerable.Range(0, seeded.Steps.Count).Select(NoteAsrBatchJsonlReader.CustomId).ToArray(),
            Enumerable.Range(0, seeded.Steps.Count).ToArray(), 8192, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            seeded.Service.StartAsync(new StartNoteAsrBatchRequest(seeded.CanonicalTaskId,
                    seeded.CanonicalAttemptId, seeded.ProfileId, seeded.ConsentVersion, true),
                jsonl.Content, jsonl.Length, "pim-canonical.jsonl",
                CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        await seeded.AssertNoSubmissionAsync();
    }

    [SkippableFact]
    public async Task Batch_route_consent_drift_at_freeze_creates_no_job_or_provider_call()
    {
        await using var seeded = await SeedAsync();
        seeded.Service.BeforeFreezeForTests = ct => DriftConsentAsync(seeded, ct);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            StartCanonicalBatchAsync(seeded));

        Assert.Equal(40901, error.ErrorCode);
        await seeded.AssertNoSubmissionAsync();
    }

    [SkippableFact]
    public async Task Batch_route_consent_drift_before_upload_never_uploads_or_creates()
    {
        await using var seeded = await SeedAsync(syntheticUpload: true);
        seeded.Service.BeforeUploadForTests = ct => DriftConsentAsync(seeded, ct);

        var job = await StartCanonicalBatchAsync(seeded);

        Assert.Equal("Failed", job.Status);
        Assert.Equal("batch_route_consent_drift", job.LastErrorCode);
        Assert.Null(job.BatchId);
        Assert.Null(job.InputFileId);
        Assert.Equal(0, seeded.Factory.UploadCalls);
        Assert.Equal(0, seeded.Factory.CreateCalls);
        await seeded.AssertRevokedAsync(expectUpload: false);
    }

    [SkippableFact]
    public async Task Batch_route_consent_drift_before_create_never_creates_paid_batch()
    {
        await using var seeded = await SeedAsync(syntheticUpload: true);
        seeded.Service.BeforeCreateForTests = ct => DriftConsentAsync(seeded, ct);

        var job = await StartCanonicalBatchAsync(seeded);

        Assert.Equal("Failed", job.Status);
        Assert.Equal("batch_route_consent_drift", job.LastErrorCode);
        Assert.Null(job.BatchId);
        Assert.Equal("file-in-1", job.InputFileId);
        Assert.Equal(1, seeded.Factory.UploadCalls);
        Assert.Equal(0, seeded.Factory.CreateCalls);
        await seeded.AssertRevokedAsync(expectUpload: true);
    }

    [SkippableFact]
    public async Task Batch_initial_submit_rejects_task_attempt_binding_mismatch_without_job_or_provider()
    {
        await using var seeded = await SeedAsync();
        seeded.CanonicalState.AttemptId = Guid.NewGuid();
        await seeded.WriteCanonicalStateAsync();
        var request = BatchRequest(seeded, seeded.CanonicalTaskId,
            seeded.CanonicalState.Revision, seeded.ConsentVersion);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            seeded.Service.StartFromTaskAsync(request, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        await seeded.AssertNoSubmissionAsync();
    }

    private static StartNoteAsrBatchFromTaskRequest BatchRequest(Seeded seeded, Guid taskId,
        Guid sourceRevision, string consentVersion) =>
        new(taskId, taskId, sourceRevision, seeded.Owner,
            new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, seeded.ConfigRevision,
                consentVersion, NoteAsrExecutionMode.Batch, Confirmed: true,
                BatchScopeConfirmed: true),
            new NoteAsrBatchTarget(seeded.ProfileId, seeded.ProfileRevision, consentVersion,
                Confirmed: true, BatchScopeConfirmed: true),
            new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "canonical batch proof"));

    private static async Task<NoteAsrBatchJobDto> StartCanonicalBatchAsync(Seeded seeded,
        CancellationToken ct = default)
    {
        await using var jsonl = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(seeded.Owner,
            seeded.VersionId, seeded.Steps, NoteAsrPlan.SamplesPerCall, "zh", seeded.Model,
            seeded.PromptText, seeded.Materials, 128 * 1024 * 1024,
            Enumerable.Range(0, seeded.Steps.Count).Select(NoteAsrBatchJsonlReader.CustomId).ToArray(),
            Enumerable.Range(0, seeded.Steps.Count).ToArray(), 8192, ct);
        return await seeded.Service.StartAsync(new StartNoteAsrBatchRequest(seeded.CanonicalTaskId,
                seeded.CanonicalAttemptId, seeded.ProfileId, seeded.ConsentVersion, true),
            jsonl.Content, jsonl.Length, "pim-canonical.jsonl", ct);
    }

    private static async Task DriftConsentAsync(Seeded seeded, CancellationToken ct)
    {
        await using var db = seeded.NewDb();
        var config = await db.NoteAsrRouteConfigs.SingleAsync(x => x.OwnerId == seeded.Owner, ct);
        config.ConsentVersion = "consent-v2";
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Seeded> SeedAsync(bool includeDuplicateAttempt = true,
        bool syntheticUpload = false)
    {
        var temp = await TempMigrationDatabase.CreateAsync("test_note_asr_canonical_batch");
        await temp.MigrateAsync();
        var owner = Guid.NewGuid();
        var storageRoot = Path.Combine(Path.GetTempPath(), "pim-canonical-batch",
            Guid.NewGuid().ToString("N"));
        var materialOptions = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = storageRoot
        });
        var materials = new NoteMaterialUploadStore(materialOptions);
        var tasks = new NoteAsrTaskStore(materialOptions);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        var version = await materials.GetAsync(owner, material.Request.VersionId,
            CancellationToken.None);
        var steps = NoteAsrPlan.Build(version);
        var inputSha = NoteMaterialManifestValidator.Hash(version.InputJson);
        var manifestSha = NoteMaterialManifestValidator.Hash(version.ManifestJson);
        var configRevision = Guid.NewGuid();
        var consentVersion = "consent-v1";
        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfileEntity
        {
            Id = Guid.NewGuid(),
            Revision = Guid.NewGuid(),
            Name = "Canonical batch " + owner.ToString("N")[..8],
            NormalizedName = "canonical-batch-" + owner.ToString("N")[..8],
            BaseUrl = "https://api.example.invalid/v1/",
            Model = MimoMultimodalPreset.ProModel,
            Enabled = true,
            Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId,
            AsrBatchBaseUrl = "https://batch-api-cn.xiaomimimo.com/v1/",
            ApiKeySecret = "synthetic-key",
            MaxOutputTokens = 8192,
            CreatedAt = now,
            UpdatedAt = now
        };
        var canonicalTaskId = Guid.NewGuid();
        var duplicateTaskId = Guid.NewGuid();
        var canonicalAttemptId = Guid.NewGuid();
        var duplicateAttemptId = Guid.NewGuid();
        var promptTemplate = AiAsrPromptTemplate.Normalize();
        var prompt = promptTemplate.Snapshot("zh");
        var route = new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, configRevision,
            consentVersion, true, NoteAsrExecutionMode.Batch, BatchScopeConfirmed: true);
        var consent = new AiAsrDestinationConsent(profile.Id, profile.Revision,
            "https://api.example.invalid/v1/chat/completions", MimoMultimodalPreset.PresetId,
            "1", true);
        var canonical = NewState(owner, canonicalTaskId, material.NoteId,
            material.Request.VersionId, inputSha, manifestSha, steps, configRevision,
            consentVersion, consent, route, attemptId: canonicalAttemptId, now.AddMinutes(-2));
        var duplicate = NewState(owner, duplicateTaskId, material.NoteId,
            material.Request.VersionId, inputSha, manifestSha, steps, configRevision,
            consentVersion, consent, route, attemptId: duplicateAttemptId, now.AddMinutes(-1));

        CountingFactory factory = syntheticUpload ? new UploadOnlyFactory() : new ThrowingFactory();
        var user = new FixedUser(owner);
        await using (var seedDb = NewDb(temp.ConnectionString, owner))
        {
            seedDb.Users.Add(new UserEntity
            {
                Id = owner,
                Username = "canonical-" + owner.ToString("N")[..8],
                Email = owner.ToString("N")[..8] + "@example.invalid",
                PasswordHash = "synthetic",
                IsActive = true
            });
            seedDb.AiModelProfiles.Add(profile);
            seedDb.AiModelRuntime.Add(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId,
                Revision = Guid.NewGuid(),
                Enabled = true,
                TranscriptionModelId = profile.Id
            });
            seedDb.NoteAsrRouteConfigs.Add(new NoteAsrRouteConfigEntity
            {
                OwnerId = owner,
                Revision = configRevision,
                DefaultRoute = NoteAsrRoute.CloudMultimodal,
                CloudModelId = profile.Id,
                ConsentVersion = consentVersion,
                UpdatedAt = now
            });
            seedDb.NoteAsrAttempts.Add(Attempt(owner, canonicalAttemptId, canonicalTaskId,
                material.NoteId, material.Request.VersionId, inputSha, steps.Count,
                profile, configRevision, consentVersion, promptTemplate, prompt,
                now.AddMinutes(-2)));
            if (includeDuplicateAttempt)
            {
                seedDb.NoteAsrAttempts.Add(Attempt(owner, duplicateAttemptId, duplicateTaskId,
                    material.NoteId, material.Request.VersionId, inputSha, steps.Count,
                    profile, configRevision, consentVersion, promptTemplate, prompt,
                    now.AddMinutes(-1)));
            }
            await seedDb.SaveChangesAsync();
        }
        await WriteStateAsync(storageRoot, canonical);
        await WriteStateAsync(storageRoot, duplicate);
        await WriteChainAsync(storageRoot, owner, material.Request.VersionId, canonicalTaskId);

        var batchOptions = Options.Create(new NoteAsrBatchOptions
        {
            AllowUnverifiedAudioBatch = true,
            MaxJsonlBytes = checked((int)MimoBatchDefaults.MaxJsonlBytes)
        });
        var serviceDb = NewDb(temp.ConnectionString, owner);
        var service = new NoteAsrBatchService(serviceDb, user, tasks, materials,
            new NoteAsrEvidenceService(serviceDb, tasks, user),
            new EfMimoBatchHandleSink(serviceDb), factory, new PassthroughSecrets(),
            batchOptions);
        return new Seeded(temp, storageRoot, owner, profile.Id, profile.Revision,
            material.Request.VersionId, steps, prompt.Text, materials, duplicateTaskId,
            duplicateAttemptId, canonicalTaskId, canonicalAttemptId, canonical, duplicate,
            includeDuplicateAttempt, configRevision, consentVersion, profile.Model, factory,
            service, serviceDb);
    }

    private static NoteAsrAttemptEntity Attempt(Guid owner, Guid attemptId, Guid taskId,
        Guid noteId, Guid versionId, string inputSha, int expectedSegments,
        AiModelProfileEntity profile, Guid configRevision, string consentVersion,
        AiAsrPromptTemplate promptTemplate, AiAsrPromptSnapshot prompt,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = attemptId,
            OwnerId = owner,
            TaskId = taskId,
            NoteId = noteId,
            VersionId = versionId,
            InputSha256 = inputSha,
            Route = NoteAsrRoute.CloudMultimodal,
            State = "Queued",
            ConfigRevision = configRevision,
            ConsentVersion = consentVersion,
            ExecutionMode = NoteAsrExecutionMode.Batch,
            ModelId = profile.Id,
            ModelRevision = profile.Revision,
            PresetId = MimoMultimodalPreset.PresetId,
            PresetVersion = "1",
            Model = profile.Model,
            Endpoint = "https://api.example.invalid/v1/chat/completions",
            PromptTemplate = promptTemplate.Text,
            PromptTemplateSha256 = promptTemplate.Sha256,
            PromptTemplateRevision = promptTemplate.Revision,
            PromptRendered = prompt.Text,
            PromptSha256 = prompt.Sha256,
            PromptRevision = prompt.Revision,
            FencingToken = 0,
            ExpectedSegments = expectedSegments,
            Language = "zh",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static NoteAsrState NewState(Guid owner, Guid taskId, Guid noteId, Guid versionId,
        string inputSha, string manifestSha,
        List<NoteAsrStep> steps, Guid configRevision, string consentVersion,
        AiAsrDestinationConsent consent, NoteAsrRouteConsent route, Guid? attemptId,
        DateTimeOffset createdAt)
    {
        var state = new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, versionId, consent, "zh", route),
            Fingerprint = new string('b', 64),
            NoteId = noteId,
            InputSha256 = inputSha,
            ManifestSha256 = manifestSha,
            Route = NoteAsrRoute.CloudMultimodal,
            RouteConfigRevision = configRevision,
            RouteConsentVersion = consentVersion,
            ExecutionMode = NoteAsrExecutionMode.Batch,
            AttemptId = attemptId,
            SamplesPerCall = NoteAsrPlan.SamplesPerCall,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        state.Steps = steps.Select(step => new NoteAsrStep
        {
            Index = step.Index,
            RecordingId = step.RecordingId,
            RecordingSegment = step.RecordingSegment,
            ChunkIndex = step.ChunkIndex,
            FileIndex = step.FileIndex,
            Path = step.Path,
            Sha256 = step.Sha256,
            SourceOffset = step.SourceOffset,
            RecordingOffset = step.RecordingOffset,
            TimelineOffset = step.TimelineOffset,
            Samples = step.Samples,
            Attempts = [new()]
        }).ToList();
        return state;
    }

    private static async Task WriteStateAsync(string storageRoot, NoteAsrState state)
    {
        var folder = Path.Combine(storageRoot, "asr-tasks", state.Owner.ToString("D"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, state.Request.TaskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(state, Json));
    }

    private static async Task WriteChainAsync(string storageRoot, Guid owner, Guid versionId,
        Guid taskId)
    {
        var folder = Path.Combine(storageRoot, "asr-tasks", owner.ToString("D"), "chains");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, versionId.ToString("D") + ".json"),
            JsonSerializer.Serialize(new { taskId }, Json));
    }

    private static PimDbContext NewDb(string connectionString, Guid owner)
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString).Options;
        return new PimDbContext(options, new FixedUser(owner));
    }

    private sealed class Seeded : IAsyncDisposable
    {
        private readonly TempMigrationDatabase _temp;
        private readonly string _storageRoot;
        private readonly PimDbContext _serviceDb;
        public Guid Owner { get; }
        public Guid ProfileId { get; }
        public Guid ProfileRevision { get; }
        public Guid VersionId { get; }
        public List<NoteAsrStep> Steps { get; }
        public string PromptText { get; }
        public NoteMaterialUploadStore Materials { get; }
        public Guid DuplicateTaskId { get; }
        public Guid DuplicateAttemptId { get; }
        public Guid CanonicalTaskId { get; }
        public Guid CanonicalAttemptId { get; }
        public NoteAsrState CanonicalState { get; }
        public NoteAsrState DuplicateState { get; }
        public bool HasDuplicateAttempt { get; }
        public Guid ConfigRevision { get; }
        public string ConsentVersion { get; }
        public string Model { get; }
        public CountingFactory Factory { get; }
        public NoteAsrBatchService Service { get; }

        public Seeded(TempMigrationDatabase temp, string storageRoot, Guid owner, Guid profileId,
            Guid profileRevision, Guid versionId, List<NoteAsrStep> steps, string promptText,
            NoteMaterialUploadStore materials, Guid duplicateTaskId, Guid duplicateAttemptId,
            Guid canonicalTaskId, Guid canonicalAttemptId, NoteAsrState canonicalState,
            NoteAsrState duplicateState, bool hasDuplicateAttempt, Guid configRevision,
            string consentVersion, string model, CountingFactory factory,
            NoteAsrBatchService service, PimDbContext serviceDb)
        {
            _temp = temp;
            _storageRoot = storageRoot;
            _serviceDb = serviceDb;
            Owner = owner;
            ProfileId = profileId;
            ProfileRevision = profileRevision;
            VersionId = versionId;
            Steps = steps;
            PromptText = promptText;
            Materials = materials;
            DuplicateTaskId = duplicateTaskId;
            DuplicateAttemptId = duplicateAttemptId;
            CanonicalTaskId = canonicalTaskId;
            CanonicalAttemptId = canonicalAttemptId;
            CanonicalState = canonicalState;
            DuplicateState = duplicateState;
            HasDuplicateAttempt = hasDuplicateAttempt;
            ConfigRevision = configRevision;
            ConsentVersion = consentVersion;
            Model = model;
            Factory = factory;
            Service = service;
        }

        public PimDbContext NewDb() => NoteAsrCanonicalBatchTests.NewDb(
            _temp.ConnectionString, Owner);

        public Task WriteCanonicalStateAsync() => NoteAsrCanonicalBatchTests.WriteStateAsync(
            _storageRoot, CanonicalState);

        public async Task AssertNoSubmissionAsync()
        {
            await using (var check = NewDb())
            {
                Assert.Equal(0, await check.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == Owner));
                Assert.Equal(0, await check.NoteAsrSegmentEvidence.CountAsync(x =>
                    x.OwnerId == Owner));
                Assert.Equal(0, await check.NoteAsrLeases.CountAsync(x => x.OwnerId == Owner));
                Assert.Equal(0, await check.AiRequestLogs.CountAsync(x => x.UserId == Owner));
                var canonicalAttempt = await check.NoteAsrAttempts.AsNoTracking()
                    .SingleAsync(x => x.Id == CanonicalAttemptId);
                Assert.Equal("Queued", canonicalAttempt.State);
                Assert.Equal(0L, canonicalAttempt.FencingToken);
                Assert.Equal(HasDuplicateAttempt ? 1 : 0,
                    await check.NoteAsrAttempts.CountAsync(x => x.Id == DuplicateAttemptId));
                if (HasDuplicateAttempt)
                {
                    var duplicateAttempt = await check.NoteAsrAttempts.AsNoTracking()
                        .SingleAsync(x => x.Id == DuplicateAttemptId);
                    Assert.Equal("Queued", duplicateAttempt.State);
                    Assert.Equal(0L, duplicateAttempt.FencingToken);
                }
            }
            Assert.Equal(0, Factory.Calls);
            var tasks = new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions
            {
                StorageRoot = _storageRoot
            }));
            Assert.Equal(CanonicalState.Revision,
                (await tasks.ReadAsync(Owner, CanonicalTaskId, CancellationToken.None)).Revision);
            Assert.Equal(DuplicateState.Revision,
                (await tasks.ReadAsync(Owner, DuplicateTaskId, CancellationToken.None)).Revision);
        }

        public async Task AssertRevokedAsync(bool expectUpload)
        {
            await using var check = NewDb();
            var attempt = await check.NoteAsrAttempts.AsNoTracking()
                .SingleAsync(x => x.Id == CanonicalAttemptId);
            Assert.Equal("Failed", attempt.State);
            Assert.True(attempt.StopRequested);
            Assert.Equal("route_consent_drift", attempt.LastErrorCode);
            var job = await check.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == Owner && x.AttemptId == CanonicalAttemptId);
            Assert.Equal("Failed", job.Status);
            Assert.Null(job.BatchId);
            Assert.Equal(expectUpload ? "file-in-1" : null, job.InputFileId);
            var tasks = new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions
            {
                StorageRoot = _storageRoot
            }));
            var state = await tasks.ReadAsync(Owner, CanonicalTaskId, CancellationToken.None);
            Assert.True(state.StopRequested);
            Assert.All(state.Steps, step => Assert.Equal("Blocked", step.Current.Status));
        }

        public async ValueTask DisposeAsync()
        {
            _serviceDb.Dispose();
            if (Directory.Exists(_storageRoot))
                Directory.Delete(_storageRoot, recursive: true);
            await _temp.DisposeAsync();
        }
    }

    private abstract class CountingFactory : IMimoBatchClientFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public abstract int UploadCalls { get; }
        public abstract int CreateCalls { get; }
        protected void CountCall() => Interlocked.Increment(ref _calls);
        public abstract IMimoBatchClientLease Create(Uri baseAddress, string apiKey);
    }

    private sealed class ThrowingFactory : CountingFactory
    {
        public override int UploadCalls => 0;
        public override int CreateCalls => 0;
        public override IMimoBatchClientLease Create(Uri baseAddress, string apiKey)
        {
            CountCall();
            throw new InvalidOperationException("Batch provider must not be called");
        }
    }

    private sealed class UploadOnlyFactory : CountingFactory
    {
        private int _uploads;
        private int _creates;
        public override int UploadCalls => Volatile.Read(ref _uploads);
        public override int CreateCalls => Volatile.Read(ref _creates);

        public override IMimoBatchClientLease Create(Uri baseAddress, string apiKey)
        {
            CountCall();
            var http = new HttpClient(new Handler(this))
            {
                BaseAddress = baseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            return new Lease(http, new MimoBatchClient(http,
                new MimoBatchClientOptions(baseAddress, TimeSpan.FromSeconds(2),
                    AudioBatchEnabled: true)));
        }

        private sealed class Lease(HttpClient http, MimoBatchClient client) : IMimoBatchClientLease
        {
            public HttpClient Http { get; } = http;
            public MimoBatchClient Client { get; } = client;
            public void Dispose() => Http.Dispose();
        }

        private sealed class Handler(UploadOnlyFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post &&
                    request.RequestUri?.AbsolutePath.EndsWith("/v1/files", StringComparison.Ordinal) == true)
                {
                    Interlocked.Increment(ref owner._uploads);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"id\":\"file-in-1\",\"purpose\":\"batch\",\"filename\":\"input.jsonl\",\"expire_at\":1780000000}",
                            Encoding.UTF8, "application/json")
                    });
                }
                Interlocked.Increment(ref owner._creates);
                throw new InvalidOperationException("Batch create must not be called after consent drift");
            }
        }
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
