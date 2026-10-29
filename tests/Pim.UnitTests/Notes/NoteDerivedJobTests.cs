using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using Pim.Infrastructure.Notes;
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
    private string? _recordRecordingPrompt;
    private string? _recordAnalysisPrompt;
    private int _calls;

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
                content.EnumerateArray().Any(part => part.TryGetProperty("input_audio", out _)))
            {
                return Completion(call, "Successful ASR transcript for derived fixture");
            }
            if (content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(part => part.TryGetProperty("image_url", out _)))
            {
                lock (_visionBodies) _visionBodies.Add(root.GetRawText());
                return Completion(call, "视觉证据：照片与手写预览均已逐图读取。");
            }

            var prompt = PromptText(content);
            if (prompt.Contains("独立分析器", StringComparison.Ordinal))
            {
                _recordAnalysisPrompt = prompt;
                return Completion(call, "## 独立分析\n\n保留完整记录版本引用并给出独立判断。");
            }
            _recordRecordingPrompt = prompt;
            return Completion(call, "## 完整记录\n\n全部 Markdown、ASR 与图片证据均已纳入。");
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
        Assert.True(task.Coverage.Complete);
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

    private async Task Run(Guid taskId)
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
