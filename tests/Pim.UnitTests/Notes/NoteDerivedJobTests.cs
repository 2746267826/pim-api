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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedJobTests : IAsyncLifetime
{
    private const string AsrRoute = "/api/v1/native-notes/asr-tasks";
    private const string DerivedRoute = "/api/v1/native-notes/derived-tasks";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-derived-job",
        Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplication _provider = null!;
    private HttpClient _client = null!;
    private Guid _owner;
    private AiModelDto _generationModel = null!;
    private AiModelDto _asrModel = null!;
    private NoteDerivedGenerationConsent _consent = null!;
    private AiAsrDestinationConsent _asrConsent = null!;
    private readonly List<string> _visionBodies = [];
    private readonly List<string> _recordBodies = [];
    private readonly List<string> _analysisBodies = [];
    private string? _recordRecordingPrompt;
    private string? _recordAnalysisPrompt;
    private int _calls;
    private int _activeAudioBatches;
    private int _maxActiveAudioBatches;
    private bool _failFirstAudioBatchOnce;
    private bool _failFirstRecordOnce;
    private Func<Task>? _recordHook;
    private readonly Dictionary<int, int> _audioBatchAttempts = [];
    private readonly List<(int Index, string Body)> _audioBatchBodies = [];
    private readonly List<(int Index, string Text)> _audioBatchResults = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _provider = builder.Build();
        _provider.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            var call = Interlocked.Increment(ref _calls);
            using var document = await JsonDocument.ParseAsync(request.Body);
            var root = document.RootElement;
            var messages = root.GetProperty("messages");
            var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
            if (content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(part => part.TryGetProperty("image_url", out _)))
            {
                lock (_visionBodies) _visionBodies.Add(root.GetRawText());
                return Completion(call, "视觉证据：照片与手写预览均已逐图读取。");
            }

            var prompt = PromptText(content);
            if (prompt.Contains("长录音分批整理器", StringComparison.Ordinal))
            {
                var batch = AudioBatchNumber(prompt);
                var active = Interlocked.Increment(ref _activeAudioBatches);
                lock (_audioBatchAttempts)
                    _maxActiveAudioBatches = Math.Max(_maxActiveAudioBatches, active);
                try
                {
                    await Task.Delay(150);
                    int attempt;
                    lock (_audioBatchAttempts)
                    {
                        _audioBatchAttempts.TryGetValue(batch, out var prior);
                        attempt = prior + 1;
                        _audioBatchAttempts[batch] = attempt;
                    }
                    if (_failFirstAudioBatchOnce && batch == 1 && attempt == 1)
                    {
                        request.HttpContext.Response.StatusCode = 500;
                        return Completion(call, "synthetic upstream failure");
                    }
                    lock (_audioBatchBodies) _audioBatchBodies.Add((batch, root.GetRawText()));
                    var evidence = $"批次证据 {batch}：本批录音已按时间顺序整理。";
                    lock (_audioBatchResults) _audioBatchResults.Add((batch, evidence));
                    return Completion(call, evidence);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeAudioBatches);
                }
            }
            if (prompt.Contains("独立分析器", StringComparison.Ordinal))
            {
                lock (_analysisBodies) _analysisBodies.Add(root.GetRawText());
                _recordAnalysisPrompt = prompt;
                return Completion(call, "## 独立分析\n\n保留完整记录版本引用并给出独立判断。");
            }
            if (prompt.Contains("完整记录整理器", StringComparison.Ordinal))
            {
                lock (_recordBodies) _recordBodies.Add(root.GetRawText());
                _recordRecordingPrompt = prompt;
                if (_recordHook is { } hook)
                {
                    _recordHook = null;
                    await hook();
                }
                if (_failFirstRecordOnce)
                {
                    _failFirstRecordOnce = false;
                    request.HttpContext.Response.StatusCode = 500;
                    return Completion(call, "synthetic record failure");
                }
                return Completion(call, RecordCompletion());
            }
            if (content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(part => part.TryGetProperty("input_audio", out _)))
                return Completion(call, "Successful ASR transcript for derived fixture");
            _recordRecordingPrompt = prompt;
            return Completion(call, RecordCompletion());
        });
        await _provider.StartAsync();

        _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("derived-job"))
            .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
                .UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "binary"))
                .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "derived-job-owner", "admin");
        _owner = session.UserId;
        _client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        _generationModel = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Derived text and vision", _provider.Urls.Single() + "/v1/",
                "derived-model", true, [AiModelCapability.Text, AiModelCapability.Vision],
                30, 1024, "synthetic-derived-key")));
        _asrModel = await Data<AiModelDto>(await _client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Derived ASR", _provider.Urls.Single() + "/v1/",
                "mimo-v2.5-asr", true, [AiModelCapability.Transcription],
                30, 123, "synthetic-derived-asr-key", AsrPresetId: "mimo-asr")));
        var runtime = await Data<AiModelSettingsDto>(await _client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await _client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(runtime.Revision, true, _generationModel.Id,
                _generationModel.Id, _asrModel.Id)));
        _consent = new(_generationModel.Id, _generationModel.Revision, _generationModel.BaseUrl, true);
        _asrConsent = new(_asrModel.Id, _asrModel.Revision,
            _asrModel.BaseUrl + "chat/completions", "mimo-asr", "1", true);
    }

    [Fact]
    public async Task RealLoopbackReceivesExactPhotoAndInkBytesAndPublishesTwoIndependentCandidates()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);

        var asr = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(AsrRoute,
            new StartNoteAsrRequest(Guid.NewGuid(), _owner, material.VersionId, _asrConsent, "zh")));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NoteAsrJob>().RunAsync(_owner, asr.TaskId, default);
        asr = await Data<NoteAsrTaskDto>(await _client.GetAsync($"{AsrRoute}/{asr.TaskId}"));
        Assert.Equal("Succeeded", asr.Phase);

        var request = new StartNoteDerivedTaskRequest(Guid.NewGuid(), _owner, material.VersionId,
            asr.TaskId, _consent);
        var task = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(DerivedRoute, request));
        Assert.Equal(3, task.Steps.Count);
        Assert.Equal(new[] { "image-batch:0001", "record-synthesis", "analysis-synthesis" },
            task.Steps.Select(x => x.Key));

        await Run(task.TaskId);
        task = await Data<NoteDerivedTaskDto>(await _client.GetAsync($"{DerivedRoute}/{task.TaskId}"));
        Assert.Equal("Succeeded", task.Phase);
        Assert.True(task.Coverage.Complete, JsonSerializer.Serialize(task.Coverage));
        Assert.Equal(1, task.Coverage.TranscriptSegments);
        Assert.Equal(1, task.Coverage.CompletedTranscriptSegments);
        Assert.Equal(1, task.Coverage.Photos);
        Assert.Equal(1, task.Coverage.CompletedPhotos);
        Assert.Equal(1, task.Coverage.InkPreviews);
        Assert.Equal(1, task.Coverage.CompletedInkPreviews);
        Assert.Equal(1, task.Coverage.VisionBatches);
        Assert.Equal(1, task.Coverage.CompletedVisionBatches);
        Assert.Equal(3, task.Steps.Count(x => x.Status == "Succeeded"));

        var vision = Assert.Single(_visionBodies);
        using var visionDocument = JsonDocument.Parse(vision);
        var imageUrls = ImageUrls(visionDocument.RootElement).ToArray();
        Assert.Equal(2, imageUrls.Length);
        var sentPhoto = Convert.FromBase64String(DataUrlPayload(imageUrls[0]));
        var sentInk = Convert.FromBase64String(DataUrlPayload(imageUrls[1]));
        Assert.Equal(NoteDerivedFixture.PhotoBytes, sentPhoto);
        Assert.Equal(NoteDerivedFixture.InkBytes, sentInk);
        Assert.False(sentPhoto.SequenceEqual(sentInk));

        Assert.NotNull(_recordRecordingPrompt);
        Assert.Contains(material.Markdown, _recordRecordingPrompt, StringComparison.Ordinal);
        Assert.Contains("Successful ASR transcript for derived fixture", _recordRecordingPrompt,
            StringComparison.Ordinal);
        Assert.Contains("视觉证据：照片与手写预览均已逐图读取。", _recordRecordingPrompt,
            StringComparison.Ordinal);
        Assert.NotNull(_recordAnalysisPrompt);
        Assert.Contains("## 完整记录", _recordAnalysisPrompt, StringComparison.Ordinal);
        Assert.Contains("全部 Markdown、ASR 与图片证据均已纳入。", _recordAnalysisPrompt,
            StringComparison.Ordinal);

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var recordArtifact = await store.DescribeArtifactAsync(_owner,
                state.CompleteRecordArtifactId, default);
            var analysisArtifact = await store.DescribeArtifactAsync(_owner,
                state.IndependentAnalysisArtifactId, default);
            var recordVersion = Assert.Single(recordArtifact.Versions);
            var analysisVersion = Assert.Single(analysisArtifact.Versions);
            Assert.NotEqual(recordVersion.VersionId, analysisVersion.VersionId);
            Assert.NotEqual(recordArtifact.ArtifactId, analysisArtifact.ArtifactId);
            Assert.Null(recordArtifact.HeadVersionId);
            Assert.Null(analysisArtifact.HeadVersionId);
            var analysis = await store.GetVersionAsync(_owner, analysisArtifact.ArtifactId,
                analysisVersion.VersionId, default);
            Assert.Equal(recordVersion.VersionId, analysis.SourceCompleteRecordVersionId);
            Assert.Equal(task.TaskId, analysis.GenerationTaskId);

            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(2, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                (x.Purpose == NoteDerivedPrompts.RecordPurpose ||
                 x.Purpose == NoteDerivedPrompts.AnalysisPurpose)));
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.VisionPurpose &&
                x.SourceObjectId == task.TaskId.ToString("D")));
            var visionLog = await db.AiRequestLogs.SingleAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.VisionPurpose);
            var images = await db.AiCallImages.Where(x => x.LogId == visionLog.Id)
                .OrderBy(x => x.Index).ToListAsync();
            Assert.Equal(2, images.Count);
            Assert.All(images, image => Assert.NotNull(image.ObjectKey));
            Assert.All(images, image => Assert.NotNull(image.ProviderKey));
        }

        var calls = _calls;
        await Run(task.TaskId);
        Assert.Equal(calls, _calls);
    }

    [Fact]
    public async Task AllowlistedDerivedCallCarriesTheExactFixedAudioSliceWithoutAsr()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);

        var allowlist = new NoteDerivedAudioContextOptions { ModelNames = ["derived-model"] };
        var task = await StartWithAudioContextAsync(material, allowlist);

        Assert.Equal("Queued", task.Phase);
        Assert.DoesNotContain(task.Issues, issue => issue.Code == "asr_required");
        Assert.Equal(3, task.Steps.Count);

        await Run(task.TaskId, allowlist);
        using var scope = _factory.Services.CreateScope();
        var completed = NoteDerivedStore.DescribeTask(
            await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default));
        Assert.Equal("Succeeded", completed.Phase);
        Assert.True(completed.Coverage.Complete, JsonSerializer.Serialize(completed.Coverage));
        Assert.Equal(0, completed.Coverage.TranscriptSegments);

        var recordBody = Assert.Single(_recordBodies);
        using var record = JsonDocument.Parse(recordBody);
        var recordMessages = record.RootElement.GetProperty("messages");
        var recordContent = recordMessages[recordMessages.GetArrayLength() - 1].GetProperty("content");
        var audio = recordContent.EnumerateArray()
            .Single(part => part.GetProperty("type").GetString() == "input_audio")
            .GetProperty("input_audio");
        var sent = audio.GetProperty("data").GetString();
        Assert.True(sent == Convert.ToBase64String(material.Content[5]) ||
            sent == "data:audio/wav;base64," + Convert.ToBase64String(material.Content[5]));

        var analysisBody = Assert.Single(_analysisBodies);
        using var analysis = JsonDocument.Parse(analysisBody);
        var analysisMessages = analysis.RootElement.GetProperty("messages");
        var analysisContent = analysisMessages[analysisMessages.GetArrayLength() - 1]
            .GetProperty("content");
        var analysisAudio = analysisContent.EnumerateArray()
            .Single(part => part.GetProperty("type").GetString() == "input_audio")
            .GetProperty("input_audio").GetProperty("data").GetString();
        Assert.True(analysisAudio == Convert.ToBase64String(material.Content[5]) ||
            analysisAudio == "data:audio/wav;base64," + Convert.ToBase64String(material.Content[5]));

        var receipt = await CreateJob(scope.ServiceProvider, allowlist)
            .ReceiptAsync(_owner, task.TaskId, default);
        Assert.True(receipt.Complete, JsonSerializer.Serialize(receipt.Issues));
        Assert.Equal(0, receipt.Sources.Count(x => x.Kind == NoteDerivedSourceKind.TranscriptSegment));

        using var verificationScope = _factory.Services.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var audioLogs = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == _owner &&
                (x.Purpose == NoteDerivedPrompts.RecordPurpose ||
                 x.Purpose == NoteDerivedPrompts.AnalysisPurpose))
            .ToListAsync();
        Assert.Equal(2, audioLogs.Count);
        Assert.All(audioLogs, log => Assert.Contains("pim-ai-audio:", log.RequestMessagesJson,
            StringComparison.Ordinal));
        Assert.All(audioLogs, log => Assert.DoesNotContain(Convert.ToBase64String(material.Content[5]),
            log.RequestPayloadJson, StringComparison.Ordinal));

        var legacyState = await verificationScope.ServiceProvider
            .GetRequiredService<NoteDerivedStore>()
            .ReadTaskAsync(_owner, task.TaskId, default);
        var legacyRecord = legacyState.Steps.Single(x => x.Key == "record-synthesis");
        var legacyReceipt = audioLogs.Single(x =>
            x.Purpose == NoteDerivedPrompts.RecordPurpose);
        Assert.Empty(NoteDerivedReceipts.ValidateSynthesisAudioWire(
            legacyState, legacyRecord, legacyReceipt));
        var legacyOriginalMessages = legacyReceipt.RequestMessagesJson;
        var legacyValidHashes = JsonNode.Parse(legacyReceipt.MetadataJson)!["audioInputsSha256"]!
            .AsArray().Select(x => x?.DeepClone()).ToArray();
        var legacyMessages = JsonNode.Parse(legacyReceipt.RequestMessagesJson)!.AsArray();
        var legacyContent = legacyMessages.OfType<JsonObject>()
            .Select(message => message["content"] as JsonArray)
            .First(array => array is not null)!;
        var legacyAudioPart = legacyContent.OfType<JsonObject>()
            .First(part => part["type"]?.GetValue<string>() == "input_audio");
        legacyContent.Add(legacyAudioPart.DeepClone());
        legacyReceipt.RequestMessagesJson = legacyMessages.ToJsonString();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(
                legacyState, legacyRecord, legacyReceipt),
            issue => issue.Code == "audio_wire_mismatch");

        legacyReceipt.RequestMessagesJson = legacyOriginalMessages;
        var legacyMetadata = JsonNode.Parse(legacyReceipt.MetadataJson)!.AsObject();
        var invalidLegacyHashes = new JsonArray();
        foreach (var hash in legacyValidHashes)
        {
            JsonNode? invalid = invalidLegacyHashes.Count == 0 ? JsonValue.Create(7) : hash;
            invalidLegacyHashes.Add(invalid);
        }
        legacyMetadata["audioInputsSha256"] = invalidLegacyHashes;
        legacyReceipt.MetadataJson = legacyMetadata.ToJsonString();
        Assert.Contains(NoteDerivedReceipts.ValidateSynthesisAudioWire(
                legacyState, legacyRecord, legacyReceipt),
            issue => issue.Code == "audio_wire_incomplete");
    }

    [Fact]
    public async Task JobRevalidatesTheAudioAllowlistBeforeSending()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var task = await StartWithAudioContextAsync(material,
            new NoteDerivedAudioContextOptions { ModelNames = ["derived-model"] });

        await Run(task.TaskId);

        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
            .ReadTaskAsync(_owner, task.TaskId, default);
        Assert.True(state.StopRequested);
        Assert.Contains(state.Steps, step =>
            step.Current.ErrorCode == "audio_context_not_allowed");
        Assert.Empty(_recordBodies);
        Assert.Empty(_analysisBodies);
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.AiRequestLogs.AsNoTracking().AnyAsync(x => x.UserId == _owner &&
            (x.Purpose == NoteDerivedPrompts.RecordPurpose ||
             x.Purpose == NoteDerivedPrompts.AnalysisPurpose)));
    }

    [Fact]
    public async Task UnlistedDerivedModelKeepsTheExistingAsrRequirement()
    {
        var material = NoteDerivedFixture.CreateMaterial();
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);

        using var scope = _factory.Services.CreateScope();
        var service = new NoteDerivedService(
            scope.ServiceProvider.GetRequiredService<NoteDerivedStore>(),
            scope.ServiceProvider.GetRequiredService<NoteDerivedJob>(),
            materials,
            scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>(),
            scope.ServiceProvider.GetRequiredService<NoteDerivedSnapshotBuilder>(),
            scope.ServiceProvider.GetRequiredService<PimDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICurrentUserService>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            Options.Create(new NoteDerivedAudioContextOptions()));
        var task = await service.StartForOwnerAsync(_owner,
            new StartNoteDerivedTaskRequest(Guid.NewGuid(), _owner, material.VersionId, null, _consent),
            waitForAsr: false, allowIncompleteAsr: false, default);

        Assert.Equal("Blocked", task.Phase);
        Assert.Contains(task.Issues, issue => issue.Code == "asr_required");
    }

    [Fact]
    public async Task LongAudioIsSplitIntoBoundedBatchesThenMergedWithoutReplayingAudio()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };

        var task = await StartWithAudioContextAsync(material, allowlist);

        Assert.Equal(new[]
        {
            "image-batch:0001",
            "audio-batch:0001", "audio-batch:0002", "audio-batch:0003",
            "record-synthesis", "analysis-synthesis"
        }, task.Steps.Select(x => x.Key));
        await Run(task.TaskId, allowlist);

        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
            .ReadTaskAsync(_owner, task.TaskId, default);
        var completed = NoteDerivedStore.DescribeTask(state);
        Assert.Equal("Succeeded", completed.Phase);
        Assert.True(completed.Coverage.Complete, JsonSerializer.Serialize(completed.Coverage));
        Assert.Equal(3, state.Steps.Count(x =>
            x.Kind == NoteDerivedStepKind.AudioBatchEvidence &&
            x.Current.Status == "Succeeded"));
        Assert.Equal(2, _maxActiveAudioBatches);

        var ordered = _audioBatchBodies.OrderBy(x => x.Index).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Select(x => x.Index));
        var payloads = ordered.Select(item => AudioPayloads(item.Body)).ToArray();
        Assert.Equal(new[] { 1, 1, 2 }, payloads.Select(x => x.Count));
        var pcm = payloads.SelectMany(x => x)
            .SelectMany(wav => wav.AsSpan(44).ToArray()).ToArray();
        var expected = material.Content[^1].AsSpan(44).ToArray();
        Assert.Equal(expected, pcm);

        var record = Assert.Single(_recordBodies);
        Assert.DoesNotContain("input_audio", record, StringComparison.Ordinal);
        Assert.DoesNotContain("pim-ai-audio:", record, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(material.Content[^1]), record,
            StringComparison.Ordinal);
        Assert.Contains("批次证据 1", record, StringComparison.Ordinal);
        Assert.Contains("批次证据 2", record, StringComparison.Ordinal);
        Assert.Contains("批次证据 3", record, StringComparison.Ordinal);
        var analysis = Assert.Single(_analysisBodies);
        Assert.DoesNotContain("input_audio", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("pim-ai-audio:", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(material.Content[^1]), analysis,
            StringComparison.Ordinal);
        Assert.Contains("批次证据 1", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("input_audio", _recordRecordingPrompt!, StringComparison.Ordinal);
        Assert.DoesNotContain("pim-ai-audio:", _recordRecordingPrompt!, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(material.Content[^1]), _recordRecordingPrompt!,
            StringComparison.Ordinal);

        var receipt = await CreateJob(scope.ServiceProvider, allowlist)
            .ReceiptAsync(_owner, task.TaskId, default);
        Assert.True(receipt.Complete, JsonSerializer.Serialize(receipt.Issues));
        Assert.Equal(3, receipt.Batches.Count(x => x.StepKey.StartsWith("audio-batch:", StringComparison.Ordinal)));
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Equal(3, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
            x.Purpose == NoteDerivedPrompts.AudioBatchPurpose));
        Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
            x.Purpose == NoteDerivedPrompts.RecordPurpose));
        Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
            x.Purpose == NoteDerivedPrompts.AnalysisPurpose));
    }

    [Fact]
    public async Task SucceededAudioBatchWithoutWireEvidenceCannotFeedRecordOnRetry()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        var task = await StartWithAudioContextAsync(material, allowlist);
        await Run(task.TaskId, allowlist);
        Assert.Single(_recordBodies);
        Assert.Single(_analysisBodies);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var batchLog = await db.AiRequestLogs
                .Where(x => x.UserId == _owner &&
                    x.Purpose == NoteDerivedPrompts.AudioBatchPurpose)
                .OrderBy(x => x.Id)
                .FirstAsync();
            batchLog.MetadataJson = "{}";
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .UpdateTaskAsync(_owner, task.TaskId, current =>
                {
                    var record = current.Steps.Single(x => x.Key == "record-synthesis");
                    record.Current.Status = "Cancelled";
                    record.Current.ErrorCode = "synthetic_cancelled";
                    record.Current.ErrorMessage = "Synthetic cancelled record for gate test";
                }, default);
        }

        var current = await Data<NoteDerivedTaskDto>(
            await _client.GetAsync($"{DerivedRoute}/{task.TaskId}"));
        Assert.Equal("Cancelled", current.Steps.Single(x =>
            x.Key == "record-synthesis").Status);
        var recordCalls = _recordBodies.Count;
        var retry = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry",
            new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, current.Revision,
                ["record-synthesis"], current.Destination, true)));
        Assert.Equal("Queued", retry.Phase);

        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            Assert.Equal("Blocked", record.Current.Status);
            Assert.Equal("audio_batch_wire_incomplete", record.Current.ErrorCode);
            Assert.Equal(recordCalls, _recordBodies.Count);
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.RecordPurpose));
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.AnalysisPurpose));
        }
    }

    [Fact]
    public async Task RetryRunsOnlyExplicitStepsThenRestoresDownstreamWhenSelected()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        _failFirstAudioBatchOnce = true;
        var task = await StartWithAudioContextAsync(material, allowlist);

        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            Assert.Equal("NeedsAttention", NoteDerivedStore.DescribeTask(state).Phase);
            Assert.Equal("Failed", state.Steps.Single(x => x.Key == "audio-batch:0001").Current.Status);
            Assert.Equal("Succeeded", state.Steps.Single(x => x.Key == "audio-batch:0002").Current.Status);
            Assert.Equal("Succeeded", state.Steps.Single(x => x.Key == "audio-batch:0003").Current.Status);
            Assert.Equal("Cancelled", state.Steps.Single(x => x.Key == "record-synthesis").Current.Status);
            Assert.Equal("Cancelled", state.Steps.Single(x => x.Key == "analysis-synthesis").Current.Status);
            Assert.Empty(_recordBodies);
            Assert.Empty(_analysisBodies);
            _failFirstAudioBatchOnce = false;

            var unmetPrerequisite = await _client.PostAsJsonAsync(
                $"{DerivedRoute}/{task.TaskId}/retry",
                new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, state.Revision,
                    ["record-synthesis"], state.Request.Consent, true));
            Assert.Equal(HttpStatusCode.Conflict, unmetPrerequisite.StatusCode);
            Assert.Empty(_recordBodies);
            Assert.Empty(_analysisBodies);
            var retry = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
                $"{DerivedRoute}/{task.TaskId}/retry",
                new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, state.Revision,
                    ["audio-batch:0001"], state.Request.Consent, true)));
            Assert.Equal("Queued", retry.Phase);
        }

        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            Assert.Equal("NeedsAttention", NoteDerivedStore.DescribeTask(state).Phase);
            Assert.Equal(2, _audioBatchAttempts[1]);
            Assert.Equal(1, _audioBatchAttempts[2]);
            Assert.Equal(1, _audioBatchAttempts[3]);
            Assert.Equal("Cancelled", state.Steps.Single(x =>
                x.Key == "record-synthesis").Current.Status);
            Assert.Equal("Cancelled", state.Steps.Single(x =>
                x.Key == "analysis-synthesis").Current.Status);
            Assert.Empty(_recordBodies);
            Assert.Empty(_analysisBodies);
            var downstreamRetry = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
                $"{DerivedRoute}/{task.TaskId}/retry",
                new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, state.Revision,
                    ["record-synthesis", "analysis-synthesis"], state.Request.Consent, true)));
            Assert.Equal("Queued", downstreamRetry.Phase);
        }

        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            Assert.Equal("Succeeded", NoteDerivedStore.DescribeTask(state).Phase);
            Assert.Single(_recordBodies);
            Assert.Single(_analysisBodies);
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(4, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.AudioBatchPurpose));
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.RecordPurpose));
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.AnalysisPurpose));
        }
    }

    [Fact]
    public async Task RecordPublishRevalidatesAudioBatchAfterProviderCallBeforeCreatingVersion()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        var task = await StartWithAudioContextAsync(material, allowlist);
        _recordHook = CorruptFirstAudioBatchAsync;

        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
            var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            Assert.Equal("Blocked", record.Current.Status);
            Assert.StartsWith("audio_batch_", record.Current.ErrorCode);
            Assert.Single(_recordBodies);
            Assert.Empty(_analysisBodies);
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(1, await db.AiRequestLogs.CountAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.RecordPurpose));
        }
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Theory]
    [InlineData("metadata", "audio_batch_wire_incomplete")]
    [InlineData("delete", "audio_batch_receipt_missing")]
    [InlineData("fingerprint", "publish_evidence_changed")]
    public async Task RecordPublishRejectsAudioBatchReceiptChangeInsidePublishBoundary(
        string corruption, string expectedCode)
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        var task = await StartWithAudioContextAsync(material, allowlist);
        var mutated = 0;

        await Run(task.TaskId, allowlist, async _ =>
        {
            if (Interlocked.Increment(ref mutated) == 1)
                await MutateFirstAudioBatchAsync(corruption);
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            Assert.Equal("Blocked", record.Current.Status);
            Assert.Equal(expectedCode, record.Current.ErrorCode);
            Assert.Empty(_analysisBodies);
        }
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Theory]
    [InlineData("owner", "synthesis_receipt_missing")]
    [InlineData("delete", "synthesis_receipt_missing")]
    [InlineData("request", "synthesis_request_mismatch")]
    [InlineData("purpose", "synthesis_purpose_mismatch")]
    [InlineData("source", "synthesis_source_mismatch")]
    [InlineData("model", "synthesis_model_mismatch")]
    [InlineData("input", "synthesis_input_mismatch")]
    [InlineData("body", "synthesis_output_mismatch")]
    [InlineData("output", "synthesis_output_mismatch")]
    [InlineData("result", "synthesis_result_mismatch")]
    [InlineData("fingerprint", "publish_evidence_changed")]
    public async Task RecordPublishRejectsSynthesisReceiptChangeInsidePublishBoundary(
        string corruption, string expectedCode)
    {
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions { ModelNames = ["derived-model"] };
        var task = await StartWithAudioContextAsync(material, allowlist);
        var mutated = 0;

        await Run(task.TaskId, allowlist, async _ =>
        {
            if (Interlocked.Increment(ref mutated) == 1)
                await MutateRecordReceiptAsync(corruption);
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            Assert.Equal("Blocked", record.Current.Status);
            Assert.Equal(expectedCode, record.Current.ErrorCode);
            Assert.Empty(_analysisBodies);
        }
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Theory]
    [InlineData("owner", "synthesis_receipt_missing")]
    [InlineData("delete", "synthesis_receipt_missing")]
    [InlineData("request", "synthesis_request_mismatch")]
    [InlineData("purpose", "synthesis_purpose_mismatch")]
    [InlineData("source", "synthesis_source_mismatch")]
    [InlineData("model", "synthesis_model_mismatch")]
    [InlineData("body", "synthesis_result_mismatch")]
    [InlineData("input", "synthesis_input_mismatch")]
    [InlineData("output", "synthesis_output_mismatch")]
    [InlineData("result", "synthesis_result_mismatch")]
    public async Task RecoveredRecordReceiptUsesTheSameSynthesisReceiptGate(
        string corruption, string expectedCode)
    {
        var (task, allowlist) = await StartRecordRetryAsync();
        await AddRecoveredRecordReceiptAsync(task.TaskId, "Running", "none", corruption);

        var calls = _recordBodies.Count;
        await Run(task.TaskId, allowlist);

        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
            .ReadTaskAsync(_owner, task.TaskId, default);
        var record = state.Steps.Single(x => x.Key == "record-synthesis");
        Assert.Equal("Blocked", record.Current.Status);
        Assert.Equal(expectedCode, record.Current.ErrorCode);
        Assert.Equal(calls, _recordBodies.Count);
        Assert.Empty(_analysisBodies);
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Theory]
    [InlineData("Running", "delete", "synthesis_receipt_missing")]
    [InlineData("Unknown", "delete", "synthesis_receipt_missing")]
    [InlineData("Running", "request", "synthesis_request_mismatch")]
    [InlineData("Unknown", "request", "synthesis_request_mismatch")]
    [InlineData("Running", "purpose", "synthesis_purpose_mismatch")]
    [InlineData("Unknown", "model", "synthesis_model_mismatch")]
    [InlineData("Running", "body", "synthesis_result_mismatch")]
    [InlineData("Unknown", "output", "synthesis_output_mismatch")]
    [InlineData("Running", "result", "synthesis_result_mismatch")]
    [InlineData("Unknown", "result", "synthesis_result_mismatch")]
    public async Task ReconcilingRecoveredRecordBlocksCorruptedSynthesisReceipt(
        string checkpointStatus, string corruption, string expectedCode)
    {
        var (task, _) = await StartRecordRetryAsync();
        await AddRecoveredRecordReceiptAsync(task.TaskId, checkpointStatus, "none", corruption);

        var calls = _recordBodies.Count;
        var reconciled = await Data<NoteDerivedTaskDto>(
            await _client.GetAsync($"{DerivedRoute}/{task.TaskId}"));
        var recordAfter = reconciled.Steps.Single(x => x.Key == "record-synthesis");
        Assert.Equal("Blocked", recordAfter.Status);
        Assert.Equal(expectedCode, recordAfter.Attempts[^1].ErrorCode);
        Assert.Equal(calls, _recordBodies.Count);
        Assert.Empty(_analysisBodies);
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Fact]
    public async Task RecoveredRecordReceiptWithValidEvidencePublishesCandidate()
    {
        var (task, allowlist) = await StartRecordRetryAsync();
        await AddRecoveredRecordReceiptAsync(task.TaskId, "Running", "none");

        var calls = _recordBodies.Count;
        await Run(task.TaskId, allowlist);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
        var record = state.Steps.Single(x => x.Key == "record-synthesis");
        Assert.Equal("Succeeded", record.Current.Status);
        Assert.Equal(calls, _recordBodies.Count);

        var artifact = await store.DescribeArtifactAsync(_owner,
            state.CompleteRecordArtifactId, default);
        var version = Assert.Single(artifact.Versions);
        var stored = await store.GetVersionAsync(_owner, artifact.ArtifactId,
            version.VersionId, default);
        Assert.Equal("## synthetic recovered record\n\nBatch evidence was validated again.",
            stored.ContentMarkdown);
    }

    [Theory]
    [InlineData("Running", "metadata")]
    [InlineData("Unknown", "missing")]
    public async Task ReconcilingRecoveredRecordReceiptRevalidatesAudioBatchesBeforePublishing(
        string checkpointStatus, string corruption)
    {
        var (task, _) = await StartRecordRetryAsync();
        await AddRecoveredRecordReceiptAsync(task.TaskId, checkpointStatus, corruption);

        var calls = _recordBodies.Count;
        var reconciled = await Data<NoteDerivedTaskDto>(
            await _client.GetAsync($"{DerivedRoute}/{task.TaskId}"));
        var recordAfter = reconciled.Steps.Single(x => x.Key == "record-synthesis");
        Assert.Equal("Blocked", recordAfter.Status);
        Assert.StartsWith("audio_batch_", recordAfter.Attempts[^1].ErrorCode);
        Assert.Equal(calls, _recordBodies.Count);
        Assert.Empty(_analysisBodies);
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Fact]
    public async Task RecoveredRecordReceiptRevalidatesAudioBatchesBeforePublishing()
    {
        var (task, allowlist) = await StartRecordRetryAsync();
        await AddRecoveredRecordReceiptAsync(task.TaskId, "Running", "metadata");

        var calls = _recordBodies.Count;
        await Run(task.TaskId, allowlist);

        using (var scope = _factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<NoteDerivedStore>()
                .ReadTaskAsync(_owner, task.TaskId, default);
            var record = state.Steps.Single(x => x.Key == "record-synthesis");
            Assert.Equal("Blocked", record.Current.Status);
            Assert.StartsWith("audio_batch_", record.Current.ErrorCode);
        }
        Assert.Equal(calls, _recordBodies.Count);
        Assert.Empty(_analysisBodies);
        await AssertNoDerivedVersionsAsync(task.TaskId);
    }

    [Fact]
    public async Task AudioBatchReceiptRejectsNonExactHashesAndRequestParts()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        var task = await StartWithAudioContextAsync(material, allowlist);
        await Run(task.TaskId, allowlist);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
        var step = state.Steps.First(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var log = await db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == step.Current.LogId);
        Assert.Empty(NoteDerivedReceipts.ValidateAudioBatchReceipt(state, step, log));

        var metadata = JsonNode.Parse(log.MetadataJson)!.AsObject();
        var validHashes = metadata["audioInputsSha256"]!.AsArray();
        metadata["audioInputsSha256"] = new JsonArray(
            validHashes.Select(x => x?.DeepClone()).Append(JsonValue.Create("extra")).ToArray());
        log.MetadataJson = metadata.ToJsonString();
        Assert.Contains(NoteDerivedReceipts.ValidateAudioBatchReceipt(state, step, log),
            issue => issue.Code == "audio_batch_wire_mismatch");

        var invalidHashes = new JsonArray();
        foreach (var hash in validHashes)
            invalidHashes.Add(invalidHashes.Count == 0 ? JsonValue.Create(7) : hash?.DeepClone());
        metadata["audioInputsSha256"] = invalidHashes;
        log.MetadataJson = metadata.ToJsonString();
        Assert.Contains(NoteDerivedReceipts.ValidateAudioBatchReceipt(state, step, log),
            issue => issue.Code == "audio_batch_wire_mismatch");

        metadata["audioInputsSha256"] = new JsonArray(
            validHashes.Select(x => x?.DeepClone()).ToArray());
        log.MetadataJson = metadata.ToJsonString();
        var messages = JsonNode.Parse(log.RequestMessagesJson)!.AsArray();
        var content = messages.OfType<JsonObject>()
            .Select(message => message["content"] as JsonArray)
            .First(array => array is not null)!;
        var audioPart = content.OfType<JsonObject>()
            .First(part => part["type"]?.GetValue<string>() == "input_audio");
        content.Add(audioPart.DeepClone());
        log.RequestMessagesJson = messages.ToJsonString();
        Assert.Contains(NoteDerivedReceipts.ValidateAudioBatchReceipt(state, step, log),
            issue => issue.Code == "audio_batch_wire_snapshot_mismatch");
    }

    private NoteDerivedJob CreateJob(IServiceProvider services,
        NoteDerivedAudioContextOptions? audioContext = null,
        Func<CancellationToken, Task>? publishProbe = null) => new(
        services.GetRequiredService<NoteDerivedStore>(),
        services.GetRequiredService<NoteMaterialUploadStore>(),
        services.GetRequiredService<NoteAsrTaskStore>(),
        services.GetRequiredService<NoteDerivedSnapshotBuilder>(),
        services.GetRequiredService<PimDbContext>(),
        services.GetRequiredService<AiTextCallService>(),
        Options.Create(audioContext ?? new NoteDerivedAudioContextOptions()),
        NullLogger<NoteDerivedJob>.Instance,
        services.GetRequiredService<IServiceScopeFactory>(),
        publishProbe);

    private async Task<NoteDerivedTaskDto> StartWithAudioContextAsync(
        NoteDerivedFixture.Material material, NoteDerivedAudioContextOptions audioContext)
    {
        using var scope = _factory.Services.CreateScope();
        var service = new NoteDerivedService(
            scope.ServiceProvider.GetRequiredService<NoteDerivedStore>(),
            scope.ServiceProvider.GetRequiredService<NoteDerivedJob>(),
            _factory.Services.GetRequiredService<NoteMaterialUploadStore>(),
            scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>(),
            scope.ServiceProvider.GetRequiredService<NoteDerivedSnapshotBuilder>(),
            scope.ServiceProvider.GetRequiredService<PimDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICurrentUserService>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            Options.Create(audioContext));
        return await service.StartForOwnerAsync(_owner,
            new StartNoteDerivedTaskRequest(Guid.NewGuid(), _owner, material.VersionId, null, _consent),
            waitForAsr: false, allowIncompleteAsr: false, default);
    }

    private async Task Run(Guid taskId, NoteDerivedAudioContextOptions? audioContext = null,
        Func<CancellationToken, Task>? publishProbe = null)
    {
        using var scope = _factory.Services.CreateScope();
        var job = CreateJob(scope.ServiceProvider, audioContext, publishProbe);
        await job.RunAsync(_owner, taskId, default);
    }

    private async Task<(NoteDerivedTaskDto Task, NoteDerivedAudioContextOptions Allowlist)>
        StartRecordRetryAsync()
    {
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 16_000 * 181);
        var materials = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await NoteDerivedFixture.UploadAsync(materials, _owner, material);
        var allowlist = new NoteDerivedAudioContextOptions
        {
            ModelNames = ["derived-model"],
            MaxBatchBytes = 2 * 1024 * 1024,
            MaxParallelCalls = 2
        };
        _failFirstRecordOnce = true;
        var task = await StartWithAudioContextAsync(material, allowlist);
        await Run(task.TaskId, allowlist);
        Assert.Empty(_analysisBodies);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var state = await store.ReadTaskAsync(_owner, task.TaskId, default);
        Assert.Equal("Failed",
            state.Steps.Single(x => x.Key == "record-synthesis").Current.Status);
        var retry = await Data<NoteDerivedTaskDto>(await _client.PostAsJsonAsync(
            $"{DerivedRoute}/{task.TaskId}/retry",
            new RetryNoteDerivedTaskRequest(Guid.NewGuid(), _owner, state.Revision,
                ["record-synthesis"], state.Request.Consent, true)));
        Assert.Equal("Queued", retry.Phase);
        return (task, allowlist);
    }

    private async Task AddRecoveredRecordReceiptAsync(Guid taskId, string checkpointStatus,
        string corruption, string? synthesisCorruption = null)
    {
        const string recoveredRecord = "## synthetic recovered record\n\nBatch evidence was validated again.";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var state = await store.ReadTaskAsync(_owner, taskId, default);
        var record = state.Steps.Single(x => x.Key == "record-synthesis");
        var batch = state.Steps.First(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence);
        var vision = state.Steps
            .Where(x => x.Kind == NoteDerivedStepKind.ImageEvidence &&
                x.Current.Status == "Succeeded" && x.Current.LogId is not null)
            .ToArray();
        var batches = state.Steps
            .Where(x => x.Kind == NoteDerivedStepKind.AudioBatchEvidence)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        var receiptIds = vision.Concat(batches)
            .Select(x => x.Current.LogId!.Value)
            .ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.UserId == _owner && receiptIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);
        var evidence = vision.Select(step =>
            {
                var receipt = receipts[step.Current.LogId!.Value];
                return new NoteDerivedEvidence(step.Key, receipt.Id, receipt.ResponseText!,
                    step.Images);
            })
            .Concat(batches.Select(step =>
            {
                var receipt = receipts[step.Current.LogId!.Value];
                return new NoteDerivedEvidence(step.Key, receipt.Id, receipt.ResponseText!, []);
            }))
            .ToArray();
        var prompt = NoteDerivedPrompts.Record(state.Plan.Input, evidence, audioBatched: true);
        var inputHash = NoteDerivedReceipts.SynthesisInputFingerprint(state, record, prompt);
        var receipt = new AiRequestLogEntity
        {
            Id = Guid.NewGuid(),
            UserId = _owner,
            ClientRequestId = record.Current.RequestId,
            Module = "notes",
            Purpose = NoteDerivedPrompts.RecordPurpose,
            SourceObjectType = NoteDerivedPrompts.SourceObjectType,
            SourceObjectId = taskId.ToString("D"),
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
            ResponseText = recoveredRecord,
            OutputHash = Hash(recoveredRecord),
            InputHash = inputHash,
            MetadataJson = """
                {"providerAttempted":true,"requestBodyCaptured":true,"responseBodyComplete":true,
                 "audioInputsReferenced":0,"audioInputsSha256":[],"audioEncoding":null}
                """,
            RequestMessagesJson = "[]"
        };
        switch (synthesisCorruption)
        {
            case null:
                break;
            case "owner":
                receipt.UserId = Guid.NewGuid();
                break;
            case "delete":
                break;
            case "body":
                receipt.ResponseText = "## synthetic tampered recovered record body";
                break;
            case "request":
                receipt.ClientRequestId = Guid.NewGuid();
                break;
            case "purpose":
                receipt.Purpose = "note.derived.tampered";
                break;
            case "source":
                receipt.SourceObjectId = Guid.NewGuid().ToString("D");
                break;
            case "model":
                receipt.ModelProfileId = Guid.NewGuid();
                break;
            case "input":
                receipt.InputHash = Hash("tampered input");
                break;
            case "output":
                receipt.OutputHash = Hash("tampered output");
                break;
            case "result":
                receipt.ResponseText = "## synthetic tampered recovered record";
                receipt.OutputHash = Hash(receipt.ResponseText);
                break;
            default:
                throw new InvalidOperationException("unknown synthesis corruption " +
                    synthesisCorruption);
        }
        if (synthesisCorruption != "delete")
            db.AiRequestLogs.Add(receipt);
        var batchLog = await db.AiRequestLogs.SingleAsync(x => x.Id == batch.Current.LogId);
        if (corruption == "metadata") batchLog.MetadataJson = "{}";
        await db.SaveChangesAsync();
        await store.UpdateTaskAsync(_owner, taskId, current =>
        {
            var step = current.Steps.Single(x => x.Key == "record-synthesis");
            step.Current.Status = checkpointStatus;
            step.Current.LogId = receipt.Id;
            step.Current.ResultSha256 = Hash(recoveredRecord);
            if (corruption == "missing")
                current.Steps.Single(x => x.Key == batch.Key).Current.LogId = Guid.NewGuid();
            step.Current.ErrorCode = "synthetic_checkpoint_unknown";
            step.Current.ErrorMessage = "Synthetic recovered checkpoint";
        }, default);
    }

    private Task CorruptFirstAudioBatchAsync() => MutateFirstAudioBatchAsync("metadata");

    private async Task MutateFirstAudioBatchAsync(string corruption)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var log = await db.AiRequestLogs
            .Where(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.AudioBatchPurpose)
            .OrderBy(x => x.Id)
            .FirstAsync();
        if (corruption == "delete") db.AiRequestLogs.Remove(log);
        else if (corruption == "fingerprint")
        {
            var metadata = JsonNode.Parse(log.MetadataJson)!.AsObject();
            metadata["publishProbe"] = "changed";
            log.MetadataJson = metadata.ToJsonString();
        }
        else log.MetadataJson = "{}";
        await db.SaveChangesAsync();
    }

    private async Task MutateRecordReceiptAsync(string corruption)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var log = await db.AiRequestLogs
            .SingleAsync(x => x.UserId == _owner &&
                x.Purpose == NoteDerivedPrompts.RecordPurpose);
        switch (corruption)
        {
            case "owner":
                log.UserId = Guid.NewGuid();
                break;
            case "delete":
                db.AiRequestLogs.Remove(log);
                break;
            case "request":
                log.ClientRequestId = Guid.NewGuid();
                break;
            case "purpose":
                log.Purpose = "note.derived.tampered";
                break;
            case "source":
                log.SourceObjectId = Guid.NewGuid().ToString("D");
                break;
            case "model":
                log.ModelProfileId = Guid.NewGuid();
                break;
            case "input":
                log.InputHash = Hash("tampered input");
                break;
            case "body":
                log.ResponseText = "## tampered record body only\n\nOutput hash is unchanged.";
                break;
            case "output":
                log.OutputHash = Hash("tampered output");
                break;
            case "result":
                log.ResponseText = "## tampered record\n\nDifferent provider text.";
                log.OutputHash = Hash(log.ResponseText);
                break;
            case "fingerprint":
                log.RequestMessagesJson = " " + log.RequestMessagesJson + " ";
                break;
            default:
                throw new InvalidOperationException("unknown corruption " + corruption);
        }
        await db.SaveChangesAsync();
    }

    private async Task AssertNoDerivedVersionsAsync(Guid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<NoteDerivedStore>();
        var state = await store.ReadTaskAsync(_owner, taskId, default);
        var record = await store.DescribeArtifactAsync(_owner,
            state.CompleteRecordArtifactId, default);
        var analysis = await store.DescribeArtifactAsync(_owner,
            state.IndependentAnalysisArtifactId, default);
        Assert.Empty(record.Versions);
        Assert.Empty(analysis.Versions);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static object Completion(int call, string content) => new
    {
        id = "synthetic-derived-" + call,
        @object = "chat.completion",
        created = 1780000000,
        model = "derived-model",
        choices = new[]
        {
            new { index = 0, finish_reason = "stop",
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

    private string RecordCompletion()
    {
        var text = new StringBuilder("## 完整记录\n\n全部 Markdown、ASR 与图片证据均已纳入。");
        lock (_audioBatchResults)
        {
            foreach (var batch in _audioBatchResults.OrderBy(x => x.Index))
                text.Append($"\n\n### 录音批次 {batch.Index}\n{batch.Text}");
        }
        return text.ToString();
    }

    private static int AudioBatchNumber(string prompt)
    {
        const string marker = "（第 ";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return 0;
        start += marker.Length;
        var end = prompt.IndexOf(" /", start, StringComparison.Ordinal);
        return end > start && int.TryParse(prompt[start..end], out var number) ? number : 0;
    }

    private static IReadOnlyList<byte[]> AudioPayloads(string body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
        const string prefix = "data:audio/wav;base64,";
        var payloads = new List<byte[]>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.GetProperty("type").GetString() != "input_audio") continue;
            var data = part.GetProperty("input_audio").GetProperty("data").GetString()!;
            var bytes = Convert.FromBase64String(data.StartsWith(prefix, StringComparison.Ordinal)
                ? data[prefix.Length..] : data);
            Assert.True(bytes.Length > 44, "Each audio part must contain a WAV header and PCM bytes");
            payloads.Add(bytes);
        }
        return payloads;
    }

    private static IEnumerable<string> ImageUrls(JsonElement root)
    {
        var messages = root.GetProperty("messages");
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("image_url", out var image) &&
                    image.TryGetProperty("url", out var url) &&
                    url.GetString() is { } value)
                    yield return value;
            }
        }
    }

    private static string DataUrlPayload(string url)
    {
        var comma = url.IndexOf(',');
        Assert.True(comma > 0);
        return url[(comma + 1)..];
    }

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_provider is not null) await _provider.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
