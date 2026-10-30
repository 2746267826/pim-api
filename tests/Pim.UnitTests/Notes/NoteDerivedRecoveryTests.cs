using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedRecoveryTests : IAsyncLifetime
{
    private const string AsrRoute = "/api/v1/native-notes/asr-tasks";
    private const string DerivedRoute = "/api/v1/native-notes/derived-tasks";
    private const string ArtifactRoute = "/api/v1/native-notes/derived-artifacts";
    private const string RecordMarker = "RECOVERY_RECORD_MARKER";
    private const string AnalysisMarker = "INDEPENDENT_ANALYSIS_MARKER";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-derived-recovery",
        Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<bool> _visionStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _visionRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebApplicationFactory<Program> _factory = null!;
    private WebApplication _provider = null!;
    private HttpClient _client = null!;
    private Guid _owner;
    private AiModelDto _generationModel = null!;
    private AiModelDto _asrModel = null!;
    private NoteDerivedGenerationConsent _consent = null!;
    private AiAsrDestinationConsent _asrConsent = null!;
    private volatile bool _blockVision;
    private string _firstRecordOutcome = "succeeded";
    private int _audioCalls;
    private int _visionCalls;
    private int _recordCalls;
    private int _analysisCalls;
    private string? _lastRecordPrompt;
    private string? _lastAnalysisPrompt;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _provider = builder.Build();
        _provider.MapPost("/v1/chat/completions", async Task<IResult> (HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            var root = document.RootElement;
            var messages = root.GetProperty("messages");
            var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
            if (content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(part => part.TryGetProperty("input_audio", out _)))
            {
                var call = Interlocked.Increment(ref _audioCalls);
                return Results.Ok(Completion($"ASR_SLICE_{call}"));
            }
            if (content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(part => part.TryGetProperty("image_url", out _)))
            {
                var call = Interlocked.Increment(ref _visionCalls);
                _visionStarted.TrySetResult(true);
                if (_blockVision) await _visionRelease.Task;
                return Results.Ok(Completion($"VISION_EVIDENCE_{call}"));
            }

            var prompt = PromptText(content);
            if (prompt.Contains(RecordMarker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _analysisCalls);
                _lastAnalysisPrompt = prompt;
                return Results.Ok(Completion(AnalysisMarker));
            }

            var recordCall = Interlocked.Increment(ref _recordCalls);
            _lastRecordPrompt = prompt;
            if (recordCall == 1 && _firstRecordOutcome == "failed")
            {
                return Results.Json(new
                {
                    error = new { message = "Synthetic upstream failure", type = "server_error" }
                }, statusCode: (int)HttpStatusCode.InternalServerError);
            }
            if (recordCall == 1 && _firstRecordOutcome == "incomplete")
                return Results.Ok(Completion("PARTIAL_RECORD", "length"));
            return Results.Ok(Completion($"## {RecordMarker}\n\nComplete source-backed record."));
        });
        await _provider.StartAsync();

        _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("derived-recovery"))
            .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
                .UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "binary"))
                .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-recovery-owner", "admin");
        _owner = session.UserId;
        _client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        _generationModel = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Derived recovery text and vision",
                _provider.Urls.Single() + "/v1/", "derived-model", true,
                [AiModelCapability.Text, AiModelCapability.Vision], 30, 1024,
                "synthetic-derived-recovery-key")));
        _asrModel = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Derived recovery ASR", _provider.Urls.Single() + "/v1/",
                "mimo-v2.5-asr", true, [AiModelCapability.Transcription], 30, 123,
                "synthetic-derived-recovery-asr-key", AsrPresetId: "mimo-asr")));
        var runtime = await Data<AiModelSettingsDto>(
            await _client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await _client.PutAsJsonAsync(
            "/api/v1/ai/model-settings", new SaveAiModelSettingsRequest(runtime.Revision, true,
                _generationModel.Id, _generationModel.Id, _asrModel.Id)));
        _consent = new(_generationModel.Id, _generationModel.Revision,
            _generationModel.BaseUrl, true);
        _asrConsent = new(_asrModel.Id, _asrModel.Revision,
            _asrModel.BaseUrl + "chat/completions", "mimo-asr", "1", true);
    }

    [Fact]
    public async Task StopDuringVisionKeepsSuccessfulReceiptAndRecoversOnlyCancelledDownstreamSteps()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);
        _blockVision = true;

        var running = Task.Run(() => RunDerivedAsync(task.TaskId));
        await _visionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        HttpResponseMessage stopResponse;
        try
        {
            stopResponse = await _client.PostAsync($"{DerivedRoute}/{task.TaskId}/stop", null);
        }
        finally
        {
            _visionRelease.TrySetResult(true);
        }
        _ = await Data<NoteDerivedTaskDto>(stopResponse);
        await running;

        var stopped = await GetDerivedAsync(task.TaskId);
        Assert.True(stopped.StopRequested);
        Assert.True(stopped.Steps.All(x => x.Key is "record-synthesis" or "analysis-synthesis"
            ? x.Status == "Cancelled"
            : x.Status == "Succeeded"));
        Assert.Equal(1, _visionCalls);
        Assert.Equal(0, _recordCalls);
        Assert.Equal(0, _analysisCalls);
        Assert.Null(_lastRecordPrompt);
        Assert.Null(_lastAnalysisPrompt);
        await AssertNoVersionsAsync(stopped);

        var replay = await GetDerivedAsync(task.TaskId);
        Assert.Equal(stopped.Revision, replay.Revision);
        Assert.Equal(1, _visionCalls);

        var visionRetry = new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner,
            replay.Revision, ["image-batch:0001"], _consent, true);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry", visionRetry)).StatusCode);
        Assert.Equal(1, _visionCalls);

        var retry = new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, replay.Revision,
            ["record-synthesis", "analysis-synthesis"], _consent, true);
        var retried = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry", retry));
        Assert.False(retried.StopRequested);
        Assert.Equal(1, retried.Steps.Single(x => x.Key == "image-batch:0001").Attempts.Count);
        Assert.Equal(2, retried.Steps.Single(x => x.Key == "record-synthesis").Attempts.Count);
        Assert.Equal(2, retried.Steps.Single(x => x.Key == "analysis-synthesis").Attempts.Count);
        Assert.Equal(1, _visionCalls);
        Assert.Equal(0, _recordCalls);
        Assert.Equal(0, _analysisCalls);

        await RunDerivedAsync(task.TaskId);
        var completed = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Succeeded", completed.Phase);
        Assert.True(completed.Coverage.Complete);
        Assert.All(completed.Steps, step => Assert.Equal("Succeeded", step.Status));
        Assert.Equal(1, _visionCalls);
        Assert.Equal(1, _recordCalls);
        Assert.Equal(1, _analysisCalls);
        Assert.Contains(material.Markdown, _lastRecordPrompt, StringComparison.Ordinal);
        Assert.Contains(material.Markdown, _lastAnalysisPrompt, StringComparison.Ordinal);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.VisionPurpose &&
                x.SourceObjectId == task.TaskId.ToString("D")));
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task TerminalOrUnknownRecordReceiptReplaysWithoutResendAndRetryAddsOnlyOneAttempt(
        string firstRecordOutcome)
    {
        _firstRecordOutcome = firstRecordOutcome;
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);

        await RunDerivedAsync(task.TaskId);
        var failed = await GetDerivedAsync(task.TaskId);
        var visionStep = failed.Steps.Single(x => x.Key == "image-batch:0001");
        Assert.True(visionStep.Status == "Succeeded", TaskJson(failed));
        var record = failed.Steps.Single(x => x.Key == "record-synthesis");
        var failedLogId = record.Attempts[^1].LogId;
        Assert.True(failedLogId is not null, TaskJson(failed));
        var expectedStatus = firstRecordOutcome == "failed" ? "Failed" : "Unknown";
        Assert.True(record.Status == expectedStatus, TaskJson(failed));
        var expectedError = firstRecordOutcome == "failed" ? "ai_call_failed" : "ai_outcome_unknown";
        Assert.True(record.Attempts[^1].ErrorCode == expectedError, TaskJson(failed));
        Assert.True(failed.Steps.Single(x => x.Key == "analysis-synthesis").Status == "Cancelled",
            TaskJson(failed));
        Assert.Contains(material.Markdown, _lastRecordPrompt, StringComparison.Ordinal);

        var calls = (_visionCalls, _recordCalls, _analysisCalls);
        var revision = failed.Revision;
        await RunDerivedAsync(task.TaskId);
        _ = await GetDerivedAsync(task.TaskId);
        _ = await GetDerivedAsync(task.TaskId);
        await RunDerivedAsync(task.TaskId);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));
        Assert.Equal(revision, (await GetDerivedAsync(task.TaskId)).Revision);

        var withoutAck = new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner,
            revision, ["record-synthesis"], _consent, false);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry", withoutAck)).StatusCode);

        var retry = new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, revision,
            ["record-synthesis"], _consent, true);
        var retried = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry", retry));
        var retriedRecord = retried.Steps.Single(x => x.Key == "record-synthesis");
        Assert.Equal(2, retriedRecord.Attempts.Count);
        Assert.Equal(1, retried.Steps.Single(x => x.Key == "image-batch:0001").Attempts.Count);
        Assert.Equal(1, retried.Steps.Single(x => x.Key == "analysis-synthesis").Attempts.Count);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));

        await RunDerivedAsync(task.TaskId);
        var completedRecord = await GetDerivedAsync(task.TaskId);
        var completedRecordStep = completedRecord.Steps.Single(x => x.Key == "record-synthesis");
        Assert.True(completedRecordStep.Status == "Succeeded", TaskJson(completedRecord));
        Assert.NotEqual(failedLogId, completedRecordStep.Attempts[^1].LogId);
        Assert.Equal("Cancelled",
            completedRecord.Steps.Single(x => x.Key == "analysis-synthesis").Status);
        Assert.Equal(1, _visionCalls);
        Assert.Equal(2, _recordCalls);
        Assert.Equal(0, _analysisCalls);
        Assert.Contains(material.Markdown, _lastRecordPrompt, StringComparison.Ordinal);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.VisionPurpose &&
                x.SourceObjectId == task.TaskId.ToString("D")));
            Assert.Equal(2, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.RecordPurpose &&
                x.SourceObjectId == task.TaskId.ToString("D")));
            Assert.Equal(0, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.AnalysisPurpose &&
                x.SourceObjectId == task.TaskId.ToString("D")));
        }

        var stableCalls = (_visionCalls, _recordCalls, _analysisCalls);
        await RunDerivedAsync(task.TaskId);
        _ = await GetDerivedAsync(task.TaskId);
        Assert.Equal(stableCalls, (_visionCalls, _recordCalls, _analysisCalls));
    }

    [Fact]
    public async Task GetRecoversUnknownRecordCheckpointFromRealReceiptWithoutResendOrDuplicateCandidate()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);
        await RunDerivedAsync(task.TaskId);
        var completed = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Succeeded", completed.Phase);

        Guid versionId;
        Guid damagedRevision;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            var artifact = await store.DescribeArtifactAsync(_owner, state.CompleteRecordArtifactId, default);
            versionId = Assert.Single(artifact.Versions, x => x.GenerationTaskId == task.TaskId).VersionId;
            var damaged = await store.UpdateTaskAsync(_owner, task.TaskId, current =>
            {
                var step = current.Steps.Single(x => x.Key == record.Key);
                step.Current.Status = "Unknown";
                step.Current.LogId = null;
                step.Current.ErrorCode = "checkpoint_unknown";
                step.Current.ErrorMessage = "Synthetic lost checkpoint";
                current.Steps.Single(x => x.Key == "analysis-synthesis").Current.Status = "Queued";
            }, default);
            damagedRevision = damaged.Revision;
        }

        var versionPath = Assert.Single(Directory.GetFiles(_root, versionId.ToString("D") + ".json",
            SearchOption.AllDirectories));
        File.Delete(versionPath);
        var calls = (_visionCalls, _recordCalls, _analysisCalls);

        var recovered = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Succeeded", recovered.Steps.Single(x => x.Key == "record-synthesis").Status);
        Assert.Equal("Queued", recovered.Steps.Single(x => x.Key == "analysis-synthesis").Status);
        Assert.NotEqual(damagedRevision, recovered.Revision);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var artifact = await store.DescribeArtifactAsync(_owner, state.CompleteRecordArtifactId, default);
            Assert.Single(artifact.Versions, x => x.VersionId == versionId);
        }

        var repeated = await GetDerivedAsync(task.TaskId);
        Assert.Equal(recovered.Revision, repeated.Revision);
        Assert.Equal("Queued", repeated.Steps.Single(x => x.Key == "analysis-synthesis").Status);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var artifact = await store.DescribeArtifactAsync(_owner, state.CompleteRecordArtifactId, default);
            Assert.Single(artifact.Versions, x => x.VersionId == versionId);
        }
    }

    [Fact]
    public async Task RetryReconcilesActualVisionReceiptBeforeRejectingNewAttempt()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);
        await RunDerivedAsync(task.TaskId);
        _ = await GetDerivedAsync(task.TaskId);

        Guid revision;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var damaged = await store.UpdateTaskAsync(_owner, task.TaskId, current =>
            {
                var vision = current.Steps.Single(x => x.Key == "image-batch:0001");
                vision.Current.Status = "Unknown";
                vision.Current.LogId = null;
                vision.Current.ErrorCode = "checkpoint_unknown";
                vision.Current.ErrorMessage = "Synthetic lost checkpoint";
                current.Steps.Single(x => x.Key == "record-synthesis").Current.Status = "Queued";
            }, default);
            revision = damaged.Revision;
        }
        var calls = (_visionCalls, _recordCalls, _analysisCalls);

        var retry = new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, revision,
            ["image-batch:0001"], _consent, true);
        var response = await _client.PostAsJsonAsync($"{DerivedRoute}/{task.TaskId}/retry", retry);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var recovered = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Succeeded", recovered.Steps.Single(x => x.Key == "image-batch:0001").Status);
        Assert.Equal(1, recovered.Steps.Single(x => x.Key == "image-batch:0001").Attempts.Count);
        Assert.Equal("Queued", recovered.Steps.Single(x => x.Key == "record-synthesis").Status);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("request")]
    [InlineData("target")]
    public async Task MismatchedReceiptCannotRecoverUnknownCheckpoint(string defect)
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);
        await RunDerivedAsync(task.TaskId);
        _ = await GetDerivedAsync(task.TaskId);

        Guid revision;
        Guid requestId;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var damaged = await store.UpdateTaskAsync(_owner, task.TaskId, current =>
            {
                var record = current.Steps.Single(x => x.Key == "record-synthesis");
                record.Current.Status = "Unknown";
                record.Current.LogId = null;
                record.Current.ErrorCode = "checkpoint_unknown";
                record.Current.ErrorMessage = "Synthetic lost checkpoint";
            }, default);
            revision = damaged.Revision;
            requestId = damaged.Steps.Single(x => x.Key == "record-synthesis").Current.RequestId;
        }

        var otherOwner = defect == "owner"
            ? await AuthRegistrationTestHost.SeedUserAsync(_factory, $"other-receipt-{Guid.NewGuid():N}")
            : (Guid?)null;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var receipt = await db.AiRequestLogs.SingleAsync(x =>
                x.UserId == _owner && x.ClientRequestId == requestId);
            switch (defect)
            {
                case "owner": receipt.UserId = otherOwner!.Value; break;
                case "source": receipt.SourceObjectId = Guid.NewGuid().ToString("D"); break;
                case "request": receipt.ClientRequestId = Guid.NewGuid(); break;
                case "target": receipt.ModelRevision = Guid.NewGuid(); break;
            }
            await db.SaveChangesAsync();
        }
        var calls = (_visionCalls, _recordCalls, _analysisCalls);

        var current = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Unknown", current.Steps.Single(x => x.Key == "record-synthesis").Status);
        Assert.Equal(revision, current.Revision);
        Assert.Equal(calls, (_visionCalls, _recordCalls, _analysisCalls));
    }

    [Fact]
    public async Task OneHundredTwentyOneSecondWaveCreatesThreeAsrSlicesWithoutDuplicateCoverage()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 121);
        await UploadAsync(material);
        var asr = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(AsrRoute,
            new StartNoteAsrRequest(Guid.NewGuid(), _owner, material.VersionId,
                _asrConsent, "zh")));
        await RunAsrAsync(asr.TaskId);
        asr = await Data<NoteAsrTaskDto>(await _client.GetAsync($"{AsrRoute}/{asr.TaskId}"));
        Assert.Equal("Succeeded", asr.Phase);
        Assert.Equal(3, asr.TotalSegments);
        Assert.Equal(3, _audioCalls);

        var task = await StartDerivedAsync(material, asr.TaskId);
        Assert.Equal(task.Steps.Count, task.Steps.Select(x => x.Key).Distinct().Count());
        await RunDerivedAsync(task.TaskId);
        var completed = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Succeeded", completed.Phase);
        Assert.True(completed.Coverage.Complete);
        Assert.Equal(3, completed.Coverage.TranscriptSegments);
        Assert.Equal(3, completed.Coverage.CompletedTranscriptSegments);
        Assert.Equal(completed.Steps.Count, completed.Steps.Select(x => x.Key).Distinct().Count());
        Assert.Contains(material.Markdown, _lastRecordPrompt, StringComparison.Ordinal);
        Assert.Contains(material.Markdown, _lastAnalysisPrompt, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(_lastRecordPrompt!, "### ASR "));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var receipts = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == _owner && x.Purpose == "ai.asr-call")
            .ToListAsync();
        Assert.Equal(3, receipts.Count);
        Assert.Equal(3, receipts.Select(x => x.Id).Distinct().Count());
        foreach (var receipt in receipts)
            Assert.Equal(1, CountOccurrences(_lastRecordPrompt!, receipt.Id.ToString("D")));
        for (var segment = 0; segment < 3; segment++)
            Assert.Equal(1, CountOccurrences(_lastRecordPrompt!, $"asrSegment={segment} |"));
    }

    [Theory]
    [InlineData("missing-ink")]
    [InlineData("tampered-ink")]
    public async Task ActualVisionReceiptMustMatchEveryPlannedImageBeforeAnySynthesisIsSent(
        string defect)
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var task = await StartDerivedAsync(material);
        Guid requestId;
        IReadOnlyList<NoteDerivedImageInput> expected;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var vision = state.Steps.Single(x => x.Kind == NoteDerivedStepKind.ImageEvidence);
            requestId = vision.Current.RequestId;
            expected = vision.Images.OrderBy(x => x.Ordinal).ToArray();
            await store.UpdateTaskAsync(_owner, task.TaskId, current =>
                current.Steps.Single(x => x.Key == vision.Key).Current.Status = "Running",
                default);

            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var receipt = new AiRequestLogEntity
            {
                Id = Guid.NewGuid(),
                UserId = _owner,
                ClientRequestId = requestId,
                Module = "notes",
                Purpose = NoteDerivedPrompts.VisionPurpose,
                SourceObjectType = NoteDerivedPrompts.SourceObjectType,
                SourceObjectId = task.TaskId.ToString("D"),
                Provider = "openai-compatible",
                Model = "derived-model",
                ModelProfileId = _generationModel.Id,
                ModelProfileName = _generationModel.Name,
                ModelRevision = _generationModel.Revision,
                ModelEndpoint = _generationModel.BaseUrl,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Status = "succeeded",
                AttemptNumber = 1,
                MaxAttempts = 1,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ResponseText = "VISION_EVIDENCE_FROM_RECEIPT",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    providerAttempted = true,
                    requestBodyCaptured = true,
                    responseBodyComplete = true,
                    imageInputsStored = true
                })
            };
            db.AiRequestLogs.Add(receipt);
            var rows = expected.Select((image, index) => new AiCallImageEntity
            {
                Id = Guid.NewGuid(),
                LogId = receipt.Id,
                Index = index,
                Name = NoteDerivedPrompts.ImageName(image),
                ContentType = image.ContentType,
                SizeBytes = image.SizeBytes,
                Sha256 = defect == "tampered-ink" && image.Kind == "InkPreview"
                    ? new string('0', 64)
                    : image.Sha256,
                ProviderKey = "synthetic-provider-key",
                ObjectKey = $"synthetic/{receipt.Id:D}/{index}"
            }).Where((_, index) => defect != "missing-ink" || index == 0).ToList();
            db.AiCallImages.AddRange(rows);
            await db.SaveChangesAsync();
        }

        await RunDerivedAsync(task.TaskId);
        var failed = await GetDerivedAsync(task.TaskId);
        Assert.Equal("Failed",
            failed.Steps.Single(x => x.Key == "image-batch:0001").Status);
        Assert.Equal("vision_evidence_missing",
            failed.Steps.Single(x => x.Key == "image-batch:0001").Attempts[^1].ErrorCode);
        Assert.Equal("Cancelled",
            failed.Steps.Single(x => x.Key == "record-synthesis").Status);
        Assert.Equal("Cancelled",
            failed.Steps.Single(x => x.Key == "analysis-synthesis").Status);
        Assert.False(failed.Coverage.Complete);
        Assert.Equal(0, _visionCalls);
        Assert.Equal(0, _recordCalls);
        Assert.Equal(0, _analysisCalls);
        Assert.Null(_lastRecordPrompt);
        Assert.Null(_lastAnalysisPrompt);
        await AssertNoVersionsAsync(failed);
    }

    [Fact]
    public async Task NewGenerationAfterManualRevisionNeverMovesTheHumanHead()
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await UploadAsync(material);
        var firstTask = await StartDerivedAsync(material);
        await RunDerivedAsync(firstTask.TaskId);

        var artifacts = await Data<IReadOnlyList<NoteDerivedArtifactDto>>(
            await _client.GetAsync($"{ArtifactRoute}?noteId={material.NoteId:D}"));
        var record = artifacts.Single(x => x.Kind == NoteDerivedArtifactKind.CompleteRecord);
        var generated = Assert.Single(record.Versions);
        var selected = await Data<NoteDerivedArtifactDto>(await _client.PostAsJsonAsync(
            $"{ArtifactRoute}/{record.ArtifactId}/select",
            new SelectNoteDerivedVersionRequest(record.Revision, generated.VersionId)));
        Assert.Equal(generated.VersionId, selected.HeadVersionId);

        var manualId = Guid.NewGuid();
        var manual = await Data<NoteDerivedMutationDto>(await _client.PostAsJsonAsync(
            $"{ArtifactRoute}/{record.ArtifactId}/manual-revisions",
            new SaveNoteDerivedManualRevisionRequest(manualId, selected.Revision,
                selected.HeadVersionId, "## MANUAL_HEAD\n\nHuman edit remains selected.")));
        Assert.Equal(manualId, manual.Artifact.HeadVersionId);

        var secondTask = await StartDerivedAsync(material);
        await RunDerivedAsync(secondTask.TaskId);
        var after = await Data<NoteDerivedArtifactDto>(
            await _client.GetAsync($"{ArtifactRoute}/{record.ArtifactId}"));
        Assert.Equal(manualId, after.HeadVersionId);
        Assert.Equal(3, after.Versions.Count);
        Assert.Equal(manualId, Assert.Single(after.Versions, x => x.IsHead).VersionId);
        var secondGenerated = Assert.Single(after.Versions,
            x => x.GenerationTaskId == secondTask.TaskId);
        Assert.Equal(manualId, secondGenerated.ParentVersionId);
        Assert.False(secondGenerated.IsHead);
    }

    private async Task UploadAsync(NoteDerivedFixture.Material material)
    {
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
    }

    private async Task<NoteDerivedTaskDto> StartDerivedAsync(NoteDerivedFixture.Material material,
        Guid? asrTaskId = null)
    {
        return await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(DerivedRoute,
            new StartNoteDerivedTaskRequest(Guid.NewGuid(), _owner, material.VersionId,
                asrTaskId, _consent)));
    }

    private async Task<NoteDerivedTaskDto> GetDerivedAsync(Guid taskId) =>
        await Data<NoteDerivedTaskDto>(await _client.GetAsync($"{DerivedRoute}/{taskId}"));

    private async Task RunAsrAsync(Guid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NoteAsrJob>().RunAsync(_owner, taskId, default);
    }

    private async Task RunDerivedAsync(Guid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var job = new NoteDerivedJob(
            scope.ServiceProvider.GetRequiredService<NoteDerivedStore>(),
            scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>(),
            scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>(),
            scope.ServiceProvider.GetRequiredService<PimDbContext>(),
            scope.ServiceProvider.GetRequiredService<AiTextCallService>(),
            NullLogger<NoteDerivedJob>.Instance);
        await job.RunAsync(_owner, taskId, default);
    }

    private async Task AssertNoVersionsAsync(NoteDerivedTaskDto task)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
        var record = await store.DescribeArtifactAsync(_owner,
            state.CompleteRecordArtifactId, default);
        var analysis = await store.DescribeArtifactAsync(_owner,
            state.IndependentAnalysisArtifactId, default);
        Assert.Empty(record.Versions);
        Assert.Empty(analysis.Versions);
    }

    private static object Completion(string content, string finishReason = "stop") => new
    {
        id = "synthetic-derived-recovery-" + Guid.NewGuid().ToString("N"),
        @object = "chat.completion",
        created = 1780000000,
        model = "derived-model",
        choices = new[]
        {
            new { index = 0, finish_reason = finishReason,
                message = new { role = "assistant", content } }
        },
        usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
    };

    private static string PromptText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        return string.Join("\n", content.EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString()));
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }
        return count;
    }

    private static string TaskJson(NoteDerivedTaskDto task) =>
        JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }

    public async Task DisposeAsync()
    {
        _visionRelease.TrySetResult(true);
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_provider is not null) await _provider.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
