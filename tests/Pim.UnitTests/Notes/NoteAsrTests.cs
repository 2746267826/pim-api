using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAsrTests : IAsyncLifetime
{
    private const string Route = "/api/v1/native-notes/asr-tasks";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-asr", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplication _provider = null!;
    private HttpClient _client = null!;
    private Guid _owner;
    private AiModelDto _model = null!;
    private AiAsrDestinationConsent _consent = null!;
    private int _calls;
    private int _failCall;
    private bool _block;
    private bool _truncateFirst;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte[]> _received = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        _provider = builder.Build();
        _provider.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            var call = Interlocked.Increment(ref _calls);
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var audio = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio");
            var bytes = Convert.FromBase64String(audio.GetProperty("data").GetString()!);
            lock (_received) _received.Add(bytes);
            Assert.InRange(bytes.Length, 46, 1_920_044);
            Assert.Equal(bytes.Length - 44, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
            _started.TrySetResult(); if (_block) await _release.Task;
            if (call == _failCall) return Results.StatusCode(503);
            var truncated = _truncateFirst && call == 1;
            return Results.Json(new { id = "synthetic-note-asr-" + call, @object = "chat.completion", created = 1780000000, model = "mimo-v2.5-asr",
                choices = new[] { new { index = 0, finish_reason = truncated ? "length" : "stop",
                    message = new { role = "assistant", content = truncated
                        ? "0-1 | A: 丢弃的截断尾段"
                        : _truncateFirst ? $"0-1 | A: 合成分片转写 {call}" : "合成分片转写 " + call } } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15,
                    prompt_tokens_details = new { audio_tokens = 10 } } });
        });
        await _provider.StartAsync();
        _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("durable-asr"))
            .WithWebHostBuilder(b => b.UseEnvironment("Test").UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "binary")).UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "note-asr-owner", "admin");
        _owner = session.UserId; _client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);
        _model = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest("分片ASR",
            _provider.Urls.Single() + "/v1/", "mimo-v2.5-asr", true, [AiModelCapability.Transcription], 30, 123, "synthetic-key", AsrPresetId: "mimo-asr")));
        var runtime = await Data<AiModelSettingsDto>(await _client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await _client.PutAsJsonAsync("/api/v1/ai/model-settings", new SaveAiModelSettingsRequest(runtime.Revision, true, null, null, _model.Id)));
        _consent = new(_model.Id, _model.Revision, _model.BaseUrl + "chat/completions", "mimo-asr", "1", true);
    }

    [Fact]
    public async Task AllAudioBeyondSingleCallLimitHasExactCoverageOffsetsAndOwnerOnlyTranscripts()
    {
        var fixture = Patterned(NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 181)); await Upload(fixture);
        var request = Start(fixture);
        var sources = await Data<NoteAsrSourcesDto>(await _client.GetAsync(Route + "/sources"));
        Assert.Equal(fixture.Request.VersionId, Assert.Single(sources.Items).VersionId);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, request));
        Assert.Equal(4, task.TotalSegments); Assert.Equal(16_000L * 181, task.TotalSamples);
        await Run(task.TaskId); task = await Get(task.TaskId);
        Assert.Equal("Succeeded", task.Phase); Assert.Equal(task.TotalSamples, task.CompletedSamples);
        var parts = await Segments(task.TaskId);
        Assert.Equal(new long[] { 0, 960_000, 1_920_000, 2_880_000 }, parts.Items.Select(x => x.SourceOffsetSamples));
        Assert.Equal(new long[] { 960_000, 960_000, 960_000, 16_000 }, parts.Items.Select(x => x.Samples));
        Assert.All(parts.Items, x => { Assert.Equal("Succeeded", x.Status); Assert.NotNull(x.Transcript); Assert.Single(x.Attempts); });
        Assert.Equal(fixture.Content[^1].AsSpan(44).ToArray(), _received.SelectMany(x => x.Skip(44)).ToArray());
        var replay = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, request));
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync(Route, request with { Language = "en" })).StatusCode);
        await Run(task.TaskId); Assert.Equal(4, _calls); Assert.Equal(task.InputSha256, replay.InputSha256);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Equal(4, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner && x.SourceObjectId == task.TaskId.ToString()));
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "other-admin", "admin");
        using var stranger = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Route}/{task.TaskId}/segments")).StatusCode);
        Assert.Empty(await Data<NoteAsrTaskDto[]>(await stranger.GetAsync(Route)));
        Assert.Empty((await Data<NoteAsrSourcesDto>(await stranger.GetAsync(Route + "/sources"))).Items);
    }

    [Fact]
    public async Task FailureRetryOnlyAddsOneAttemptForSelectedFailedSegmentAndKeepsSuccessfulReceipts()
    {
        _failCall = 2; var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 121); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture))); await Run(task.TaskId);
        task = await Get(task.TaskId); Assert.Equal("NeedsAttention", task.Phase); Assert.Equal(2, task.CompletedSegments);
        var before = await Segments(task.TaskId); Assert.Equal("Failed", before.Items[1].Status);
        await Run(task.TaskId); Assert.Equal(3, _calls);
        var retry = new RetryNoteAsrRequest(Guid.NewGuid(), _owner, task.Revision, [1], _consent, true);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", retry with { AcknowledgePossibleDuplicateCharge = false })).StatusCode);
        _ = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", retry));
        _ = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", retry));
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", retry with { SegmentIndexes = [0] })).StatusCode);
        await Run(task.TaskId); var after = await Segments(task.TaskId);
        Assert.Equal(4, _calls); Assert.Equal("Succeeded", (await Get(task.TaskId)).Phase);
        Assert.Equal(before.Items[0].Attempts[0].LogId, after.Items[0].Attempts[0].LogId);
        Assert.Equal(before.Items[2].Attempts[0].LogId, after.Items[2].Attempts[0].LogId);
        Assert.Equal(2, after.Items[1].Attempts.Count); Assert.Equal("Failed", after.Items[1].Attempts[0].Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", retry with { RetryId = Guid.NewGuid(), ExpectedRevision = (await Get(task.TaskId)).Revision, SegmentIndexes = [0] })).StatusCode);
    }

    [Fact]
    public async Task ReopenedCheckpointReconcilesReceiptAfterResultSavedWithoutResendingAudio()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture))); await Run(task.TaskId);
        var path = TaskPath(task.TaskId); var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var attempt = checkpoint["steps"]![0]!["attempts"]![0]!;
        attempt["status"] = "Running"; attempt["logId"] = null;
        await File.WriteAllTextAsync(path, checkpoint.ToJsonString());
        await Run(task.TaskId); Assert.Equal(1, _calls); Assert.Equal("Succeeded", (await Get(task.TaskId)).Phase);
        Assert.NotNull((await Segments(task.TaskId)).Items[0].Attempts[0].LogId);
    }

    [Fact]
    public async Task ExclusiveRunnerAndStopDuringProviderCallKeepCurrentResultAndDoNotSendRemainingAudio()
    {
        _block = true; var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 61); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        var running = Run(task.TaskId); await _started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Run(task.TaskId); Assert.Equal(1, _calls);
        var stopped = await Data<NoteAsrTaskDto>(await _client.PostAsync($"{Route}/{task.TaskId}/stop", null));
        Assert.True(stopped.StopRequested); _release.TrySetResult(); await running;
        task = await Get(task.TaskId); Assert.Equal("Stopped", task.Phase); Assert.Equal(1, task.CompletedSegments); Assert.Equal(1, _calls);
        _block = false;
        _ = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry", new RetryNoteAsrRequest(Guid.NewGuid(), _owner, task.Revision, [1], _consent, true)));
        await Run(task.TaskId); Assert.Equal(2, _calls); Assert.Equal("Succeeded", (await Get(task.TaskId)).Phase);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(180)]
    public async Task SyntheticClassLengthAcrossRecordingsAndSegmentsRetainsExactCoverage(int minutes)
    {
        var fixture = LongFixture(minutes); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        Assert.Equal(minutes, task.TotalSegments); Assert.Equal(960_000L * minutes, task.TotalSamples);
        // Each new scope reopens the worker. Executions are capped at64 slices for fairness.
        for (var run = 0; run < (minutes + 63) / 64; run++) await Run(task.TaskId);
        task = await Get(task.TaskId); Assert.Equal("Succeeded", task.Phase); Assert.Equal(minutes, _calls);
        var all = new List<NoteAsrSegmentDto>();
        for (var offset = 0; offset < minutes; offset += 20)
            all.AddRange((await Data<NoteAsrSegmentsDto>(await _client.GetAsync($"{Route}/{task.TaskId}/segments?offset={offset}&limit=20"))).Items);
        Assert.Equal(Enumerable.Range(0, minutes).Select(x => 960_000L * x), all.Select(x => x.TimelineOffsetSamples));
        Assert.Equal(2, all.Select(x => x.RecordingId).Distinct().Count());
        Assert.Equal(0, all[minutes / 2].RecordingOffsetSamples);
        Assert.All(all, x => Assert.Equal(0, x.SourceOffsetSamples)); Assert.Equal(task.TotalSamples, task.CompletedSamples);
    }

    [Fact]
    public async Task ExpiredUnknownOutcomeRequiresExplicitRetryAndNeverResendsOnRefresh()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture))); await Run(task.TaskId);
        var path = TaskPath(task.TaskId); var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        checkpoint["steps"]![0]!["attempts"]![0]!["status"] = "Running";
        await File.WriteAllTextAsync(path, checkpoint.ToJsonString());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>(); var log = await db.AiRequestLogs.SingleAsync();
            log.Status = "running"; log.ResponseText = null; log.ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        task = await Get(task.TaskId); Assert.Equal("NeedsAttention", task.Phase);
        Assert.Equal("Incomplete", (await Segments(task.TaskId)).Items[0].Status);
        await Run(task.TaskId); Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task ChangedConfigurationBlocksBeforeSendWhileLostStartResponseCanStillReplay()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var request = Start(fixture); var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, request));
        _ = await Data<AiModelDto>(await _client.PutAsJsonAsync("/api/v1/ai/models/" + _model.Id, new SaveAiModelRequest(_model.Name, _model.BaseUrl,
            _model.Model, true, _model.Capabilities, 30, 123, ExpectedRevision: _model.Revision, AsrPresetId: "mimo-asr")));
        await Run(task.TaskId); Assert.Equal(0, _calls); Assert.True((await Get(task.TaskId)).StopRequested);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync(Route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync(Route, request with { TaskId = Guid.NewGuid() })).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedMaterialOrDisabledOwnerStopsQueuedWork(bool deleteMaterial)
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        using (var scope = _factory.Services.CreateScope())
        {
            if (deleteMaterial) await scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>().DeleteNoteAsync(_owner, fixture.Request.NoteId);
            else
            {
                var db = scope.ServiceProvider.GetRequiredService<PimDbContext>(); var owner = await db.Users.SingleAsync(x => x.Id == _owner);
                owner.IsActive = false; await db.SaveChangesAsync();
            }
        }
        await Run(task.TaskId); Assert.Equal(0, _calls);
        var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(TaskPath(task.TaskId)))!;
        Assert.True(checkpoint["stopRequested"]!.GetValue<bool>());
        Assert.Equal("Blocked", checkpoint["steps"]![0]!["attempts"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task NormalAccountOwnsTaskLifecycleAndOnlyReceivesSafeDestinationsWhileAdminSettingsStayProtected()
    {
        var adminFixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(adminFixture);
        var adminRequest = Start(adminFixture);
        var adminTask = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, adminRequest));
        var normal = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "ordinary-note-asr");
        using var client = AuthRegistrationTestHost.Authed(_factory, normal.AccessToken);
        using var anonymous = _factory.CreateClient();
        foreach (var path in new[] { Route, Route + "/sources", Route + "/destinations" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        var destinationResponse = await client.GetAsync(Route + "/destinations");
        var destinationRaw = await destinationResponse.Content.ReadAsStringAsync();
        var destinations = await Data<NoteAsrDestinationsDto>(destinationResponse);
        var destination = Assert.Single(destinations.Items);
        Assert.Equal(1, destinations.Total); Assert.Equal(_model.Id, destination.ModelId);
        Assert.Equal(_model.Revision, destination.ModelRevision); Assert.Equal(_model.Name, destination.DisplayName);
        Assert.Equal(_model.Model, destination.Model); Assert.Equal(_consent.Endpoint, destination.Endpoint);
        Assert.Equal("mimo-asr", destination.PresetId); Assert.Equal("1", destination.PresetVersion);
        Assert.Contains("zh", destination.Languages);
        Assert.DoesNotContain("synthetic-key", destinationRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", destinationRaw, StringComparison.OrdinalIgnoreCase);
        using (var document = JsonDocument.Parse(destinationRaw))
        {
            var item = document.RootElement.GetProperty("data").GetProperty("items")[0];
            Assert.Equal(new[] { "modelId", "modelRevision", "displayName", "model", "endpoint", "presetId",
                "presetVersion", "languages", "backgroundProcessingAvailable" }, item.EnumerateObject().Select(x => x.Name));
        }

        foreach (var path in new[] { "/api/v1/ai/models", "/api/v1/ai/model-settings", "/api/v1/ai/asr-presets" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/ai/models", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/ai/model-settings", new { })).StatusCode);

        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await UploadAs(normal.UserId, fixture);
        Assert.Equal(0, _calls);
        var consent = new AiAsrDestinationConsent(destination.ModelId, destination.ModelRevision, destination.Endpoint,
            destination.PresetId, destination.PresetVersion, true);
        var request = StartFor(normal.UserId, fixture, consent);
        var task = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(Route, request));
        await RunAs(normal.UserId, task.TaskId);
        task = await Data<NoteAsrTaskDto>(await client.GetAsync($"{Route}/{task.TaskId}"));
        Assert.Equal("Succeeded", task.Phase); Assert.Equal(1, _calls);
        Assert.Equal(fixture.Request.VersionId, Assert.Single(
            (await Data<NoteAsrSourcesDto>(await client.GetAsync(Route + "/sources"))).Items).VersionId);
        Assert.Equal(task.TaskId, Assert.Single(await Data<NoteAsrTaskDto[]>(await client.GetAsync(Route))).TaskId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{adminTask.TaskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Route}/{adminTask.TaskId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Route}/{task.TaskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Route}/{adminTask.TaskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Route, StartFor(normal.UserId,
            adminFixture, consent))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route,
            StartFor(normal.UserId, fixture, consent with { Confirmed = false }))).StatusCode);
    }

    [Fact]
    public async Task DisabledNormalAccountCannotStartReplayRetryQueryOrResumeQueuedWork()
    {
        var normal = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "disabled-note-asr");
        using var client = AuthRegistrationTestHost.Authed(_factory, normal.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await UploadAs(normal.UserId, fixture);
        var request = StartFor(normal.UserId, fixture, _consent);
        var task = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(Route, request));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var owner = await db.Users.SingleAsync(x => x.Id == normal.UserId); owner.IsActive = false; await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route + "/destinations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Route}/{task.TaskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Route}/{task.TaskId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Route}/{task.TaskId}/retry",
            new RetryNoteAsrRequest(Guid.NewGuid(), normal.UserId, task.Revision, [0], _consent, true))).StatusCode);

        await RunAs(normal.UserId, task.TaskId);
        Assert.Equal(0, _calls);
        var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(TaskPath(normal.UserId, task.TaskId)))!;
        Assert.True(checkpoint["stopRequested"]!.GetValue<bool>());
        Assert.Equal("Blocked", checkpoint["steps"]![0]!["attempts"]![0]!["status"]!.GetValue<string>());
        Assert.Contains("停用", checkpoint["steps"]![0]!["attempts"]![0]!["errorMessage"]!.GetValue<string>());
    }

    [Fact]
    public async Task DestinationCatalogueFiltersUnavailableTargetsAndGlobalRuntimeDisableStopsAfterCurrentSegment()
    {
        var disabled = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest(
            "停用ASR", _provider.Urls.Single() + "/v1/", "mimo-v2.5-asr", false, [AiModelCapability.Transcription],
            30, 123, "disabled-synthetic-key", AsrPresetId: "mimo-asr")));
        var text = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest(
            "文字模型", _provider.Urls.Single() + "/v1/", "text-model", true, [AiModelCapability.Text],
            30, 123, "text-synthetic-key")));
        var normal = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "filtered-destination-asr");
        using var client = AuthRegistrationTestHost.Authed(_factory, normal.AccessToken);
        var destinations = await Data<NoteAsrDestinationsDto>(await client.GetAsync(Route + "/destinations"));
        var destination = Assert.Single(destinations.Items);
        Assert.Equal(_model.Id, destination.ModelId);
        Assert.DoesNotContain(destinations.Items, x => x.ModelId == disabled.Id || x.ModelId == text.Id);

        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 181); await UploadAs(normal.UserId, fixture);
        var consent = new AiAsrDestinationConsent(destination.ModelId, destination.ModelRevision, destination.Endpoint,
            destination.PresetId, destination.PresetVersion, true);
        var task = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(Route, StartFor(normal.UserId, fixture, consent)));
        var runtime = await Data<AiModelSettingsDto>(await _client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await _client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(runtime.Revision, false, runtime.TextModelId, runtime.VisionModelId, runtime.TranscriptionModelId)));
        Assert.Empty((await Data<NoteAsrDestinationsDto>(await client.GetAsync(Route + "/destinations"))).Items);

        await RunAs(normal.UserId, task.TaskId);
        Assert.Equal(0, _calls);
        var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(TaskPath(normal.UserId, task.TaskId)))!;
        Assert.True(checkpoint["stopRequested"]!.GetValue<bool>());
        Assert.Equal("Blocked", checkpoint["steps"]![0]!["attempts"]![0]!["status"]!.GetValue<string>());
        Assert.All(checkpoint["steps"]!.AsArray().Skip(1), step =>
            Assert.Equal("Queued", step!["attempts"]![0]!["status"]!.GetValue<string>()));
        var segments = await Data<NoteAsrSegmentsDto>(await client.GetAsync($"{Route}/{task.TaskId}/segments"));
        Assert.Equal(4, segments.Total);
        Assert.Equal("Blocked", segments.Items[0].Status);
        Assert.All(segments.Items.Skip(1), segment => Assert.Equal("Queued", segment.Status));
        task = await Data<NoteAsrTaskDto>(await client.GetAsync($"{Route}/{task.TaskId}"));
        Assert.True(task.StopRequested); Assert.Equal("Stopped", task.Phase);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == normal.UserId &&
            x.SourceObjectId == task.TaskId.ToString()));
    }

    [Fact]
    public async Task BusyAsrGateKeepsCurrentAndFollowingSegmentsQueuedForAutomaticResume()
    {
        var normal = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "busy-note-asr");
        using var client = AuthRegistrationTestHost.Authed(_factory, normal.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 181); await UploadAs(normal.UserId, fixture);
        var task = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(Route, StartFor(normal.UserId, fixture, _consent)));
        var gate = _factory.Services.GetRequiredService<AiTextCallGate>();
        Assert.True(gate.TryEnter(asr: true)); Assert.True(gate.TryEnter(asr: true));
        try { await RunAs(normal.UserId, task.TaskId); }
        finally { gate.Exit(asr: true); gate.Exit(asr: true); }

        Assert.Equal(0, _calls);
        task = await Data<NoteAsrTaskDto>(await client.GetAsync($"{Route}/{task.TaskId}"));
        Assert.False(task.StopRequested); Assert.Equal("Queued", task.Phase);
        var queued = await Data<NoteAsrSegmentsDto>(await client.GetAsync($"{Route}/{task.TaskId}/segments"));
        Assert.Equal(4, queued.Total); Assert.All(queued.Items, segment => Assert.Equal("Queued", segment.Status));
        using (var scope = _factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PimDbContext>().AiRequestLogs
                .CountAsync(x => x.SourceObjectId == task.TaskId.ToString()));

        await RunAs(normal.UserId, task.TaskId);
        task = await Data<NoteAsrTaskDto>(await client.GetAsync($"{Route}/{task.TaskId}"));
        Assert.Equal("Succeeded", task.Phase); Assert.Equal(4, _calls);
    }

    [Fact]
    public async Task DemotedAdminOldTokenLosesAiAdminButKeepsOwnNativeAsrAndDisabledAccountIsRejected()
    {
        var target = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "downgraded-ai-admin", "admin");
        using var targetClient = AuthRegistrationTestHost.Authed(_factory, target.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/v1/ai/models")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync($"/api/v1/admin/users/{target.UserId}/role",
            new { role = "user" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync("/api/v1/ai/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.PutAsJsonAsync($"/api/v1/ai/models/{_model.Id}",
            new SaveAiModelRequest(_model.Name, _model.BaseUrl, _model.Model, true, _model.Capabilities,
                ExpectedRevision: _model.Revision, AsrPresetId: "mimo-asr"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.PostAsJsonAsync("/api/v1/ai/asr-calls",
            new AiAsrCallRequest(Guid.NewGuid(), target.UserId, new("probe.wav", "wav", ""), _consent, "zh"))).StatusCode);

        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await UploadAs(target.UserId, fixture);
        var task = await Data<NoteAsrTaskDto>(await targetClient.PostAsJsonAsync(Route, StartFor(target.UserId, fixture, _consent)));
        await RunAs(target.UserId, task.TaskId);
        Assert.Equal("Succeeded", (await Data<NoteAsrTaskDto>(
            await targetClient.GetAsync($"{Route}/{task.TaskId}"))).Phase);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync($"/api/v1/admin/users/{target.UserId}/status",
            new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync("/api/v1/ai/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync(Route + "/destinations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync($"{Route}/{task.TaskId}")).StatusCode);
    }

    [Fact]
    public async Task CorruptedSourceAudioDoesNotSendAReplacementOrClaimCoverage()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        var binary = Directory.GetFiles(Path.Combine(_root, "binary", _owner.ToString("D"), "note-parts", fixture.Request.VersionId.ToString("D")), "*.blob")
            .Single(x => new FileInfo(x).Length == fixture.Content[^1].Length);
        var bytes = await File.ReadAllBytesAsync(binary); bytes[^1] ^= 7; await File.WriteAllBytesAsync(binary, bytes);
        await Run(task.TaskId); Assert.Equal(0, _calls); Assert.Equal(0, (await Get(task.TaskId)).CompletedSamples);
    }

    [Fact]
    public async Task DispatcherResumesPersistedIntentsButSkipsCompletedTasks()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        var jobs = new RecordedJobs();
        var dispatch = new NoteAsrDispatchJob(_factory.Services.GetRequiredService<NoteAsrTaskStore>(), jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteAsrDispatchJob>.Instance);
        await dispatch.RunAsync(default);
        var job = Assert.Single(jobs.Items); Assert.Equal(typeof(NoteAsrJob), job.Type);
        Assert.Equal(_owner, job.Args[0]); Assert.Equal(task.TaskId, job.Args[1]);
        await Run(task.TaskId); jobs.Items.Clear(); await dispatch.RunAsync(default); Assert.Empty(jobs.Items);
    }

    [Fact]
    public async Task ARequestIdentifierOccupiedByAnotherCallBlocksWithoutStallingTheQueue()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(TaskPath(task.TaskId)))!;
        var requestId = checkpoint["steps"]![0]!["attempts"]![0]!["requestId"]!.GetValue<Guid>();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.AiRequestLogs.Add(new Pim.Infrastructure.Data.Entities.AiRequestLogEntity
            { UserId = _owner, ClientRequestId = requestId, Status = "succeeded", Purpose = "ai.text-call", SourceObjectId = "another-call" });
            await db.SaveChangesAsync();
        }
        await Run(task.TaskId); Assert.Equal(0, _calls); Assert.True((await Get(task.TaskId)).StopRequested);
        Assert.Equal("Blocked", (await Segments(task.TaskId)).Items[0].Status);
    }

    [Fact]
    public async Task LegacyQueuedTaskKeepsCloudAsrSemanticsAfterLocalBecomesDefault()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        Assert.Null(task.Route);
        await ConfigureLocalRoute();

        await Run(task.TaskId);

        Assert.Equal(1, _calls);
        Assert.Equal("Succeeded", (await Get(task.TaskId)).Phase);
    }

    [Fact]
    public async Task TruncatedRoutedCloudSegmentAutomaticallySplitsAndPublishesOrderedEvidence()
    {
        _truncateFirst = true;
        var multimodal = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("多模态截断恢复", _provider.Urls.Single() + "/v1/",
                "mimo-v2.6-flash", true, [AiModelCapability.Transcription], 30, 1024,
                "synthetic-multimodal-key", AsrPresetId: "mimo-multimodal")));
        var current = await Data<NoteAsrRouteConfigDto>(await _client.GetAsync(
            "/api/v1/native-notes/asr-routing/config"));
        var routeConfig = await Data<NoteAsrRouteConfigDto>(await _client.PutAsJsonAsync(
            "/api/v1/native-notes/asr-routing/config", new UpdateNoteAsrRouteConfigRequest(
                current.Revision, NoteAsrRoute.CloudMultimodal, null, null, multimodal.Id, "batch-v1")));
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 60);
        await Upload(fixture);
        var consent = new AiAsrDestinationConsent(multimodal.Id, multimodal.Revision,
            multimodal.BaseUrl + "chat/completions", "mimo-multimodal", "1", true);
        var start = new StartNoteAsrRequest(Guid.NewGuid(), _owner, fixture.Request.VersionId, consent, "zh",
            new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                routeConfig.ConsentVersion, true, NoteAsrExecutionMode.Realtime));

        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, start));
        Assert.Equal(NoteAsrExecutionMode.Realtime, task.ExecutionMode);
        await Run(task.TaskId);

        Assert.Equal(3, _calls);
        Assert.Equal("Succeeded", (await Get(task.TaskId)).Phase);
        var segment = Assert.Single((await Segments(task.TaskId)).Items);
        Assert.Equal("Succeeded", segment.Status);
        Assert.Equal("adaptive", segment.ReceiptKind);
        Assert.Contains("合成分片转写", segment.Transcript);
        Assert.DoesNotContain("丢弃的截断尾段", segment.Transcript);
        Assert.Contains("30-31 | A: 合成分片转写", segment.Transcript);
        Assert.Equal(3, segment.Attempts.Count);
        Assert.Equal(3, segment.Attempts.Select(x => x.LogId).Distinct().Count(x => x.HasValue));
        await Run(task.TaskId);
        Assert.Equal(3, _calls);
        using var scope = _factory.Services.CreateScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == task.AttemptId);
        Assert.Equal(1, attempt.CompletedSegments);
        Assert.Equal("Succeeded", attempt.State);
    }

    [Fact]
    public async Task BatchExecutionModeIsFrozenOnAttemptAndNeverDispatchedToSynchronousJob()
    {
        var multimodal = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Batch冻结", _provider.Urls.Single() + "/v1/",
                "mimo-v2.6-flash", true, [AiModelCapability.Transcription], 30, 1024,
                "synthetic-batch-key", AsrPresetId: "mimo-multimodal")));
        var current = await Data<NoteAsrRouteConfigDto>(await _client.GetAsync(
            "/api/v1/native-notes/asr-routing/config"));
        var routeConfig = await Data<NoteAsrRouteConfigDto>(await _client.PutAsJsonAsync(
            "/api/v1/native-notes/asr-routing/config", new UpdateNoteAsrRouteConfigRequest(
                current.Revision, NoteAsrRoute.CloudMultimodal, null, null, multimodal.Id, "batch-v1")));
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 60);
        await Upload(fixture);
        var consent = new AiAsrDestinationConsent(multimodal.Id, multimodal.Revision,
            multimodal.BaseUrl + "chat/completions", "mimo-multimodal", "1", true);
        var start = new StartNoteAsrRequest(Guid.NewGuid(), _owner, fixture.Request.VersionId, consent, "zh",
            new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                routeConfig.ConsentVersion, true, NoteAsrExecutionMode.Batch, true));

        var task = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, start));
        Assert.Equal(NoteAsrExecutionMode.Batch, task.ExecutionMode);
        await Run(task.TaskId);

        Assert.Equal(0, _calls);
        Assert.Equal("Queued", (await Get(task.TaskId)).Phase);
        using var scope = _factory.Services.CreateScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == task.AttemptId);
        Assert.Equal(NoteAsrExecutionMode.Batch, attempt.ExecutionMode);
        Assert.Equal("Queued", attempt.State);
    }

    [Fact]
    public async Task LocalRouteWithNoNodeStaysQueuedWithoutProviderCallAndAckFeedsSegmentsAndDerivedPlan()
    {
        await ConfigureLocalRoute();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture);
        var started = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, Start(fixture)));
        Assert.Equal(NoteAsrRoute.Local, started.Route);
        Assert.NotNull(started.AttemptId);
        await Run(started.TaskId);
        Assert.Equal(0, _calls);
        Assert.Equal("Queued", (await Get(started.TaskId)).Phase);

        var nodeId = Guid.NewGuid();
        _ = await Data<NoteAsrNodeDto>(await _client.PostAsJsonAsync(
            "/api/v1/native-notes/asr-routing/nodes/register", new RegisterNoteAsrNodeRequest(
                nodeId, "synthetic-local-node", true, "local-model", "r1", true, ["transcription"])));
        var claim = await Data<NoteAsrClaimDto>(await _client.PostAsJsonAsync(
            "/api/v1/native-notes/asr-routing/leases/claim",
            new ClaimNoteAsrTaskRequest(nodeId, "local-evidence-claim")));
        Assert.Equal("claimed", claim.Status);
        var execution = claim.Execution!;
        for (var index = 0; index < execution.Segments.Count; index++)
        {
            _ = await Data<NoteAsrAckDto>(await _client.PostAsJsonAsync(
                $"/api/v1/native-notes/asr-routing/leases/{execution.AttemptId}/ack",
                new NoteAsrAckRequest(claim.LeaseId!.Value, claim.Lease!.LeaseToken,
                    claim.Lease.FencingToken, "segment", "Succeeded", index, $"local-{index}",
                    null, null, $"本地合成转写 {index}", $"[{{\"segment\":{index},\"complete\":true}}]")));
        }
        _ = await Data<NoteAsrAckDto>(await _client.PostAsJsonAsync(
            $"/api/v1/native-notes/asr-routing/leases/{execution.AttemptId}/ack",
            new NoteAsrAckRequest(claim.LeaseId.Value, claim.Lease.LeaseToken,
                claim.Lease.FencingToken, "complete", "Succeeded", 0, "local-complete", null, null)));

        var completed = await Get(started.TaskId);
        Assert.Equal("Succeeded", completed.Phase);
        var segments = await Segments(started.TaskId);
        Assert.All(segments.Items, x =>
        {
            Assert.Equal("Succeeded", x.Status);
            Assert.NotNull(x.EvidenceId);
            Assert.Equal("local", x.ReceiptKind);
            Assert.Contains("本地合成转写", x.Transcript);
        });
        using (var scope = _factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PimDbContext>()
                .AiRequestLogs.CountAsync(x => x.UserId == _owner && x.SourceObjectId == started.TaskId.ToString()));

        var text = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest(
            "派生文本", _provider.Urls.Single() + "/v1/", "mimo-v2.6-flash", true,
            [AiModelCapability.Text], 30, 1024, "synthetic-derived-key")));
        var runtime = await Data<AiModelSettingsDto>(await _client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await _client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(runtime.Revision, true, text.Id, null, _model.Id)));
        var derived = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
            "/api/v1/native-notes/derived-tasks", new StartNoteDerivedTaskRequest(Guid.NewGuid(), _owner,
                fixture.Request.VersionId, started.TaskId,
                new NoteDerivedGenerationConsent(text.Id, text.Revision, text.BaseUrl, true))));
        Assert.Equal(segments.Total, derived.Coverage.CompletedTranscriptSegments);
        Assert.DoesNotContain(derived.Coverage.Issues, x => x.Code.StartsWith("asr_", StringComparison.Ordinal));
    }

    private async Task ConfigureLocalRoute()
    {
        var current = await Data<NoteAsrRouteConfigDto>(await _client.GetAsync(
            "/api/v1/native-notes/asr-routing/config"));
        _ = await Data<NoteAsrRouteConfigDto>(await _client.PutAsJsonAsync(
            "/api/v1/native-notes/asr-routing/config", new UpdateNoteAsrRouteConfigRequest(
                current.Revision, NoteAsrRoute.Local, "local-model", "r1", null, "local-v1")));
    }

    private sealed class RecordedJobs : Hangfire.IBackgroundJobClient
    {
        public List<Hangfire.Common.Job> Items { get; } = [];
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) { Items.Add(job); return Items.Count.ToString(); }
        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => throw new NotSupportedException();
    }

    private StartNoteAsrRequest Start(NoteMaterialUploadStoreTests.Fixture fixture) => StartFor(_owner, fixture, _consent);
    private static StartNoteAsrRequest StartFor(Guid owner, NoteMaterialUploadStoreTests.Fixture fixture,
        AiAsrDestinationConsent consent) => new(Guid.NewGuid(), owner, fixture.Request.VersionId, consent, "zh");
    private static NoteMaterialUploadStoreTests.Fixture Patterned(NoteMaterialUploadStoreTests.Fixture fixture)
    {
        var bytes = fixture.Content[^1];
        for (var i = 44; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), (short)((i / 32000 + i % 127) % 30000));
        var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!;
        manifest["files"]!.AsArray()[^1]!["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return fixture with { Request = fixture.Request with { ManifestJson = manifest.ToJsonString(Json) } };
    }
    private static NoteMaterialUploadStoreTests.Fixture LongFixture(int minutes)
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(960_000);
        var input = JsonNode.Parse(fixture.Request.InputJson)!; var manifest = JsonNode.Parse(fixture.Request.ManifestJson)!;
        var content = fixture.Content.Take(4).ToList(); var files = new JsonArray();
        foreach (var file in manifest["files"]!.AsArray().Take(4)) files.Add(file!.DeepClone());
        var recordings = new JsonArray();
        for (var recordingIndex = 0; recordingIndex < 2; recordingIndex++)
        {
            var recording = input["recordings"]![0]!.DeepClone(); var id = Guid.NewGuid(); recording["id"] = id.ToString("D"); recording["currentSegment"] = 2;
            var chunks = new JsonArray(); var count = recordingIndex == 0 ? minutes / 2 : minutes - minutes / 2;
            for (var i = 0; i < count; i++) chunks.Add(new JsonObject { ["segment"] = i < count / 2 ? 1 : 2, ["index"] = i + 1, ["samples"] = 960_000 });
            recording["chunks"] = chunks; recordings.Add(recording);
            var bytes = Encoding.UTF8.GetBytes(recording.ToJsonString(Json));
            content.Add(bytes); files.Add(Descriptor($"recordings/{id:D}/manifest.json", "RecordingManifest", bytes, id));
            for (var i = 0; i < count; i++)
            {
                var segment = i < count / 2 ? 1 : 2; var wave = fixture.Content[^1]; content.Add(wave);
                var file = Descriptor($"recordings/{id:D}/s{segment:D4}-c{i + 1:D6}.wav", "AudioChunk", wave, id);
                file["segment"] = segment; file["chunkIndex"] = i + 1; file["samples"] = 960_000; files.Add(file);
            }
        }
        input["recordings"] = recordings; var inputJson = input.ToJsonString(Json);
        using (var finalInput = JsonDocument.Parse(inputJson))
        {
            content[0] = Encoding.UTF8.GetBytes(finalInput.RootElement.GetProperty("note").GetRawText());
            files[0]!["sha256"] = Convert.ToHexString(SHA256.HashData(content[0])).ToLowerInvariant(); files[0]!["sizeBytes"] = content[0].Length;
            foreach (var recording in finalInput.RootElement.GetProperty("recordings").EnumerateArray())
            {
                var path = $"recordings/{recording.GetProperty("id").GetString()}/manifest.json";
                var index = files.Select((file, index) => (file, index)).Single(x => x.file!["path"]!.GetValue<string>() == path).index;
                content[index] = Encoding.UTF8.GetBytes(recording.GetRawText());
                files[index]!["sha256"] = Convert.ToHexString(SHA256.HashData(content[index])).ToLowerInvariant(); files[index]!["sizeBytes"] = content[index].Length;
            }
        }
        manifest["inputSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson))).ToLowerInvariant(); manifest["files"] = files;
        return new(fixture.Request with { InputJson = inputJson, ManifestJson = manifest.ToJsonString(Json) }, content);
        static JsonObject Descriptor(string path, string kind, byte[] bytes, Guid recording) => new()
        { ["path"] = path, ["kind"] = kind, ["sizeBytes"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["recordingId"] = recording.ToString("D") };
    }
    private async Task Upload(NoteMaterialUploadStoreTests.Fixture fixture)
        => await UploadAs(_owner, fixture);
    private async Task UploadAs(Guid owner, NoteMaterialUploadStoreTests.Fixture fixture)
    {
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>(); await store.BeginAsync(owner, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
            for (var position = 0; position < fixture.Content[file].Length; position += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var part = fixture.Content[file].AsSpan(position, Math.Min(NoteMaterialUploadOptions.PartSizeBytes, fixture.Content[file].Length - position)).ToArray();
                await store.UploadPartAsync(owner, fixture.Request.VersionId, file, position / NoteMaterialUploadOptions.PartSizeBytes,
                    Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant(), new MemoryStream(part));
            }
        await store.CompleteAsync(owner, fixture.Request.VersionId);
    }
    private async Task Run(Guid id) => await RunAs(_owner, id);
    private async Task RunAs(Guid owner, Guid id)
    { using var scope = _factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<NoteAsrJob>().RunAsync(owner, id, default); }
    private async Task<NoteAsrTaskDto> Get(Guid id) => await Data<NoteAsrTaskDto>(await _client.GetAsync($"{Route}/{id}"));
    private async Task<NoteAsrSegmentsDto> Segments(Guid id) => await Data<NoteAsrSegmentsDto>(await _client.GetAsync($"{Route}/{id}/segments"));
    private string TaskPath(Guid id) => TaskPath(_owner, id);
    private string TaskPath(Guid owner, Guid id) => Path.Combine(_root, "materials", "asr-tasks", owner.ToString("D"), id.ToString("D") + ".json");
    private static async Task<T> Data<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, response.StatusCode + ": " + await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!; }
    public async Task DisposeAsync()
    {
        _release.TrySetResult(); _client?.Dispose(); if (_factory is not null) await _factory.DisposeAsync();
        if (_provider is not null) await _provider.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
