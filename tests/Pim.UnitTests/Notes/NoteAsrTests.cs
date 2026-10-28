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
            return Results.Json(new { id = "synthetic-note-asr-" + call, @object = "chat.completion", created = 1780000000, model = "mimo-v2.5-asr",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "合成分片转写 " + call } } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 } });
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
    public async Task UnauthenticatedNormalAccountAndCrossSessionCannotCreateTasksOrReadResults()
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(); await Upload(fixture); var request = Start(fixture);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        var normal = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "normal-user");
        using var client = AuthRegistrationTestHost.Authed(_factory, normal.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync(Route, request with { ExpectedUserId = normal.UserId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(Route, request with { Consent = _consent with { Confirmed = false } })).StatusCode);
        Assert.Equal(0, _calls);
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

    private sealed class RecordedJobs : Hangfire.IBackgroundJobClient
    {
        public List<Hangfire.Common.Job> Items { get; } = [];
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) { Items.Add(job); return Items.Count.ToString(); }
        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => throw new NotSupportedException();
    }

    private StartNoteAsrRequest Start(NoteMaterialUploadStoreTests.Fixture fixture) => new(Guid.NewGuid(), _owner, fixture.Request.VersionId, _consent, "zh");
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
    {
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>(); await store.BeginAsync(_owner, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
            for (var position = 0; position < fixture.Content[file].Length; position += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var part = fixture.Content[file].AsSpan(position, Math.Min(NoteMaterialUploadOptions.PartSizeBytes, fixture.Content[file].Length - position)).ToArray();
                await store.UploadPartAsync(_owner, fixture.Request.VersionId, file, position / NoteMaterialUploadOptions.PartSizeBytes,
                    Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant(), new MemoryStream(part));
            }
        await store.CompleteAsync(_owner, fixture.Request.VersionId);
    }
    private async Task Run(Guid id)
    { using var scope = _factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<NoteAsrJob>().RunAsync(_owner, id, default); }
    private async Task<NoteAsrTaskDto> Get(Guid id) => await Data<NoteAsrTaskDto>(await _client.GetAsync($"{Route}/{id}"));
    private async Task<NoteAsrSegmentsDto> Segments(Guid id) => await Data<NoteAsrSegmentsDto>(await _client.GetAsync($"{Route}/{id}/segments"));
    private string TaskPath(Guid id) => Path.Combine(_root, "materials", "asr-tasks", _owner.ToString("D"), id.ToString("D") + ".json");
    private static async Task<T> Data<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, response.StatusCode + ": " + await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!; }
    public async Task DisposeAsync()
    {
        _release.TrySetResult(); _client?.Dispose(); if (_factory is not null) await _factory.DisposeAsync();
        if (_provider is not null) await _provider.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
