using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Storage;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedEndpointsTests : IDisposable
{
    private const string Tasks = "/api/v1/native-notes/derived-tasks";
    private const string Artifacts = "/api/v1/native-notes/derived-artifacts";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-derived-http",
        Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public NoteDerivedEndpointsTests() => _factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("note-derived"))
        .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
            .UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
            .UseSetting("ManagedStorage:Root", Path.Combine(_root, "managed-storage"))
            .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));

    [Fact]
    public async Task AnonymousIsRejected_AndAuthenticatedUserSeesOnlyOwnTaskSummaries()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Tasks + "/limits")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Tasks + "/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Artifacts + "?noteId=" + Guid.NewGuid())).StatusCode);

        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "derived-owner-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var limits = await Data<NoteDerivedLimitsDto>(await client.GetAsync(Tasks + "/limits"));
        Assert.Equal(4, limits.MaxImagesPerVisionCall);
        Assert.Equal(200_000, limits.MaxSynthesisInputChars);
        var sources = await Data<NoteAsrSourcesDto>(await client.GetAsync(Tasks + "/sources"));
        Assert.Equal(0, sources.Total);
    }

    [Fact]
    public async Task TaskAndStepEndpointsExposeBoundedCurrentAttempts_WithoutGuessingStepKeys()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-steps-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var noteId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var sourceVersionId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<NoteDerivedStore>();
        var plan = Plan(owner.UserId, noteId, sourceVersionId, 12);
        await store.CreateTaskAsync(owner.UserId,
            new(taskId, owner.UserId, sourceVersionId, null,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/", true)),
            plan, "task-fingerprint", default);

        var task = await Data<NoteDerivedTaskDto>(await client.GetAsync($"{Tasks}/{taskId}"));
        Assert.Equal(10, task.Steps.Count);
        Assert.Equal("Queued", task.Phase);

        var steps = await Data<NoteDerivedStepsDto>(await client.GetAsync(
            $"{Tasks}/{taskId}/steps?offset=0&limit=20"));
        Assert.Equal(12, steps.Total);
        Assert.Equal(12, steps.Items.Count);
        Assert.Single(steps.Items[0].Attempts);
        Assert.NotEqual(Guid.Empty, steps.Items[0].Attempts[0].RequestId);

        var stopped = await Data<NoteDerivedTaskDto>(await client.PostAsync(
            $"{Tasks}/{taskId}/stop", null));
        Assert.True(stopped.StopRequested);
        Assert.Equal("Stopped", stopped.Phase);
        var repeatedStop = await Data<NoteDerivedTaskDto>(await client.PostAsync(
            $"{Tasks}/{taskId}/stop", null));
        Assert.Equal(stopped.Revision, repeatedStop.Revision);
    }

    [Fact]
    public async Task ZeroRecordingMaterialAppearsInSourcesAndStartsWithoutAsrTask()
    {
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-admin-" + Guid.NewGuid().ToString("N"), "admin");
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-zero-audio-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var model = await CreateModelAsync(admin.AccessToken);

        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, owner.UserId, material);
        var sources = await Data<NoteAsrSourcesDto>(await client.GetAsync(Tasks + "/sources"));
        var source = Assert.Single(sources.Items);
        Assert.Equal(material.VersionId, source.VersionId);
        Assert.Equal(0, source.Recordings);

        var taskId = Guid.NewGuid();
        var startResponse = await client.PostAsJsonAsync(Tasks,
            new StartNoteDerivedTaskRequest(taskId, owner.UserId, material.VersionId, null,
                new(model.Id, model.Revision, model.BaseUrl, true)));
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var startJson = await startResponse.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(startJson))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.TryGetProperty("steps", out var steps));
            Assert.True(steps[0].TryGetProperty("attempts", out var attempts));
            Assert.True(attempts[0].TryGetProperty("requestId", out _));
            Assert.True(data.TryGetProperty("coverage", out _));
        }
        var started = JsonSerializer.Deserialize<ApiResponse<NoteDerivedTaskDto>>(startJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
        Assert.Equal(taskId, started.TaskId);
        Assert.Null(started.AsrTaskId);
        Assert.Equal("Queued", started.Phase);
        Assert.Equal(3, started.Steps.Count);
        Assert.DoesNotContain(started.Issues, issue => issue.Code == "asr_required");

        var recordArtifact = started.Artifacts.Single(x =>
            x.Kind == NoteDerivedArtifactKind.CompleteRecord);
        var recordLocator = Locator(owner.UserId, recordArtifact.ArtifactId);
        File.Delete(recordLocator);
        var replay = await Data<NoteDerivedTaskDto>(await client.PostAsJsonAsync(Tasks,
            new StartNoteDerivedTaskRequest(taskId, owner.UserId, material.VersionId, null,
                new(model.Id, model.Revision, model.BaseUrl, true))));
        Assert.Equal(started.Revision, replay.Revision);
        Assert.True(File.Exists(recordLocator));

        var analysisArtifact = started.Artifacts.Single(x =>
            x.Kind == NoteDerivedArtifactKind.IndependentAnalysis);
        var analysisLocator = Locator(owner.UserId, analysisArtifact.ArtifactId);
        File.Delete(analysisLocator);
        _ = await Data<NoteDerivedTaskDto>(await client.GetAsync($"{Tasks}/{taskId}"));
        Assert.True(File.Exists(analysisLocator));
    }

    [Fact]
    public async Task AsrTaskMissingOneRebuiltSliceCreatesStructuredBlockedTask()
    {
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-admin-missing-slice-" + Guid.NewGuid().ToString("N"), "admin");
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-missing-slice-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var model = await CreateModelAsync(admin.AccessToken);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 121);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, owner.UserId, material);
        var version = await materials.GetAsync(owner.UserId, material.VersionId, default);
        var expected = NoteAsrPlan.Build(version);
        Assert.Equal(3, expected.Count);

        var asrTaskId = Guid.NewGuid();
        var asrStore = _factory.Services.GetRequiredService<NoteAsrTaskStore>();
        await asrStore.CreateAsync(owner.UserId,
            new StartNoteAsrRequest(asrTaskId, owner.UserId, material.VersionId,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/chat/completions",
                    "mimo-asr", "1", true)),
            version, expected.Take(2).ToList(), default);
        await asrStore.UpdateAsync(owner.UserId, asrTaskId, state =>
        {
            foreach (var step in state.Steps)
            {
                step.Current.Status = "Succeeded";
                step.Current.LogId = Guid.NewGuid();
            }
        }, default);

        var started = await Data<NoteDerivedTaskDto>(await client.PostAsJsonAsync(Tasks,
            new StartNoteDerivedTaskRequest(Guid.NewGuid(), owner.UserId, material.VersionId, asrTaskId,
                new(model.Id, model.Revision, model.BaseUrl, true))));
        Assert.Equal("Blocked", started.Phase);
        Assert.Contains(started.Issues, issue => issue.Code == "asr_segment_set_mismatch");
        Assert.False(started.Coverage.Complete);
        Assert.All(started.Steps, step => Assert.Equal("Queued", step.Status));
    }

    [Fact]
    public async Task TamperedDocumentMarkdownBlocksTaskBeforeAnyModelSend()
    {
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-admin-markdown-" + Guid.NewGuid().ToString("N"), "admin");
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-markdown-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var model = await CreateModelAsync(admin.AccessToken);
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, owner.UserId, material);

        var binaries = _factory.Services.GetRequiredService<ManagedBinaryStore>();
        var markdownPart = binaries.ObjectPath(owner.UserId, ManagedBinaryStore.NoteParts,
            material.VersionId, NoteMaterialManifestValidator.Hash("1/0"));
        var bytes = await File.ReadAllBytesAsync(markdownPart);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(markdownPart, bytes);

        var started = await Data<NoteDerivedTaskDto>(await client.PostAsJsonAsync(Tasks,
            new StartNoteDerivedTaskRequest(Guid.NewGuid(), owner.UserId, material.VersionId, null,
                new(model.Id, model.Revision, model.BaseUrl, true))));
        Assert.Equal("Blocked", started.Phase);
        Assert.Contains(started.Issues, issue => issue.Code == "markdown_unreadable");
        Assert.False(started.Coverage.Complete);
    }

    [Fact]
    public async Task ManualCasConflictReturnsImmutableCandidate_AndOtherOwnerGetsNotFound()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-manual-" + Guid.NewGuid().ToString("N"));
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-other-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        using var otherClient = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        var store = _factory.Services.GetRequiredService<NoteDerivedStore>();
        var artifact = await store.EnsureArtifactAsync(owner.UserId, Guid.NewGuid(),
            NoteDerivedArtifactKind.CompleteRecord, default);
        var generatedId = Guid.NewGuid();
        await store.AppendGeneratedAsync(owner.UserId, artifact.ArtifactId,
            Draft(owner.UserId, artifact, generatedId, "生成完整记录"), default);
        var generated = await store.GetArtifactAsync(owner.UserId, artifact.ArtifactId, default);
        var selected = await store.SelectHeadAsync(owner.UserId, artifact.ArtifactId,
            new(generated.HeadRevision, generatedId), default);

        var first = new SaveNoteDerivedManualRevisionRequest(
            Guid.NewGuid(), selected.Revision, selected.HeadVersionId, "人工稿 A");
        var matched = await Data<NoteDerivedMutationDto>(await client.PostAsJsonAsync(
            $"{Artifacts}/{artifact.ArtifactId}/manual-revisions", first));
        Assert.False(matched.HasConflict);
        Assert.True(matched.IsHead);
        Assert.Equal(first.RevisionId, matched.Artifact.HeadVersionId);

        var stale = new SaveNoteDerivedManualRevisionRequest(
            Guid.NewGuid(), selected.Revision, selected.HeadVersionId, "并发人工稿 B");
        var conflicted = await Data<NoteDerivedMutationDto>(await client.PostAsJsonAsync(
            $"{Artifacts}/{artifact.ArtifactId}/manual-revisions", stale));
        Assert.True(conflicted.HasConflict);
        Assert.False(conflicted.IsHead);
        Assert.Equal(first.RevisionId, conflicted.Artifact.HeadVersionId);
        var candidate = await Data<NoteDerivedVersionDto>(await client.GetAsync(
            $"{Artifacts}/{artifact.ArtifactId}/versions/{stale.RevisionId}"));
        Assert.Equal("并发人工稿 B", candidate.ContentMarkdown);
        Assert.True(candidate.HasConflict);

        var selectedB = await Data<NoteDerivedArtifactDto>(await client.PostAsJsonAsync(
            $"{Artifacts}/{artifact.ArtifactId}/select",
            new SelectNoteDerivedVersionRequest(conflicted.Artifact.Revision, stale.RevisionId)));
        Assert.Equal(stale.RevisionId, selectedB.HeadVersionId);

        // Replaying an earlier RevisionId must return its original result, not move the human head back.
        var replayedA = await Data<NoteDerivedMutationDto>(await client.PostAsJsonAsync(
            $"{Artifacts}/{artifact.ArtifactId}/manual-revisions", first));
        Assert.False(replayedA.HasConflict);
        Assert.False(replayedA.IsHead);
        Assert.Equal(stale.RevisionId, replayedA.Artifact.HeadVersionId);
        var replayedB = await Data<NoteDerivedMutationDto>(await client.PostAsJsonAsync(
            $"{Artifacts}/{artifact.ArtifactId}/manual-revisions", stale));
        Assert.True(replayedB.HasConflict);
        Assert.True(replayedB.IsHead);
        Assert.Equal(stale.RevisionId, replayedB.Artifact.HeadVersionId);

        var staleSelect = await client.PostAsJsonAsync($"{Artifacts}/{artifact.ArtifactId}/select",
            new SelectNoteDerivedVersionRequest(conflicted.Artifact.Revision, first.RevisionId));
        Assert.Equal(HttpStatusCode.Conflict, staleSelect.StatusCode);
        Assert.Equal(40901, (await AuthRegistrationTestHost.ReadErrorAsync(staleSelect)).Code);

        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"{Artifacts}/{artifact.ArtifactId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"{Artifacts}/{artifact.ArtifactId}/versions/{stale.RevisionId}")).StatusCode);
    }

    [Fact]
    public async Task DisabledOrdinaryAccountCannotReadOrMutateDerivedResources()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-disabled-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var noteId = Guid.NewGuid();
        var sourceVersionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<NoteDerivedStore>();
        await store.CreateTaskAsync(owner.UserId,
            new(taskId, owner.UserId, sourceVersionId, null,
                new(Guid.NewGuid(), Guid.NewGuid(), "https://synthetic.invalid/v1/", true)),
            Plan(owner.UserId, noteId, sourceVersionId, 1), "disabled-task", default);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == owner.UserId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Tasks}/{taskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"{Artifacts}?noteId={noteId}")).StatusCode);
    }

    private static NoteDerivedPlan Plan(Guid owner, Guid noteId, Guid sourceVersionId, int stepCount)
    {
        var input = new NoteDerivedInputSnapshot(owner, noteId, sourceVersionId, null, null,
            "input-sha", "manifest-sha", "合成笔记", "正文",
            [new(0, Guid.NewGuid(), "正文")], [], [], []);
        var coverage = new NoteDerivedCoverageDto(1, 0, 0, 0, 0, 0, 0, 0, 0, true, []);
        var steps = Enumerable.Range(0, stepCount).Select(index => new NoteDerivedPlanStep(
            $"image-batch:{index + 1:D4}", NoteDerivedStepKind.ImageEvidence, [], [], 1)).ToArray();
        return new(NoteDerivedLimits.PlanVersion, input, steps, coverage);
    }

    private static NoteDerivedVersionDraft Draft(Guid owner, NoteDerivedArtifactState artifact,
        Guid versionId, string content) => new(
        versionId,
        artifact.Kind,
        null,
        null,
        artifact.NoteId,
        null,
        content,
        "input-sha",
        "manifest-sha",
        NoteDerivedLimits.PlanVersion,
        "prompt-v1",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://synthetic.invalid/v1/",
        new NoteDerivedCoverageDto(1, 0, 0, 1, 1, 1, 1, 1, 1, true, []));

    private async Task<AiModelDto> CreateModelAsync(string adminAccessToken)
    {
        using var adminClient = AuthRegistrationTestHost.Authed(_factory, adminAccessToken);
        var model = await Data<AiModelDto>(await adminClient.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("派生本机模型-" + Guid.NewGuid().ToString("N"),
                "https://synthetic.invalid/v1", "fixture-derived", true,
                [AiModelCapability.Text, AiModelCapability.Vision], 30, 1024, "synthetic-derived-key")));
        var settings = await Data<AiModelSettingsDto>(
            await adminClient.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await adminClient.PutAsJsonAsync(
            "/api/v1/ai/model-settings", new SaveAiModelSettingsRequest(
                settings.Revision, true, model.Id, model.Id, null)));
        return model;
    }

    private string Locator(Guid owner, Guid artifactId) => Path.Combine(
        _root, "materials", "derived", owner.ToString("D"), "artifact-index",
        artifactId.ToString("D") + ".json");

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
