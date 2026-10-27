using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai;

public sealed class AiAsrPresetTests : IDisposable
{
    private readonly PimDbContext _db = new(new DbContextOptionsBuilder<PimDbContext>().UseInMemoryDatabase("asr-" + Guid.NewGuid()).Options);
    private readonly Owner _owner = new(); private readonly Files _files = new(); private readonly Wire _wire = new();
    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly AiTextCallGate _gate = new();
    private IAiProfileChatClientFactory Factory => new AiProfileChatClientFactory(_wire);
    private AiAsrPresetRegistry Registry => new([new MimoAsrPreset(Factory)]);
    private AiModelSettingsService Settings => new(_db, _secrets, Registry);
    private AiAsrMaterialService Materials => new(_db, _files, _owner);
    private AiAsrCallService Caller => new(_db, _secrets, _owner, _gate, Registry, Materials);
    internal static byte[] Wave()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + 320); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(320);
        for (var i = 0; i < 160; i++) writer.Write((short)(i % 32 * 10)); return stream.ToArray();
    }
    private async Task<(AiAsrCallRequest Request, AiModelDto Model)> Setup(int timeout = 30)
    {
        var model = await Settings.SaveAsync(null, new("MiMo配置", "http://synthetic-asr.invalid/v1/chat/completions", "mimo-v2.5-asr", true,
            [AiModelCapability.Transcription], timeout, 123, "synthetic-asr-key", AsrPresetId: "mimo-asr"), default);
        var runtime = await Settings.GetSettingsAsync(default);
        await Settings.SaveSettingsAsync(new(runtime.Revision, true, null, null, model.Id), default);
        var preset = Registry.Resolve(model.AsrPresetId).Descriptor;
        return (new(Guid.NewGuid(), _owner.UserId!.Value, new("示例.wav", "wav", Convert.ToBase64String(Wave())),
            new(model.Id, model.Revision, AiAsrPresetRegistry.OperationEndpoint(model.BaseUrl, preset), preset.Id, preset.Version, true), "zh"), model);
    }

    [Theory]
    [InlineData("auto", "wav")]
    [InlineData("zh", "wav")]
    [InlineData("en", "wav")]
    [InlineData("auto", "mp3")]
    public async Task OfficialSdkBuildsAudioOnlyRequestWithProviderLanguage_NoTextTokenOverride(string language, string format)
    {
        var preset = new MimoAsrPreset(Factory); using var capture = new AiTextWireCapture();
        var bytes = format == "wav" ? Wave() : "ID3synthetic-mp3-fixture"u8.ToArray();
        var decoded = AiAsrMaterialService.Decode(new("test." + format, format, Convert.ToBase64String(bytes)));
        Assert.Equal(bytes, decoded.Bytes);
        var result = await preset.TranscribeAsync(new("http://synthetic-asr.invalid/v1/", "mimo-v2.5-asr", "synthetic-asr-key", 30),
            decoded, language, default);
        Assert.Equal("识别结果", result.Text); Assert.Equal(5, result.ReportedSeconds);
        using var json = JsonDocument.Parse(_wire.Bodies.Single()); var root = json.RootElement;
        Assert.Equal(language, root.GetProperty("asr_options").GetProperty("language").GetString());
        Assert.False(root.TryGetProperty("max_completion_tokens", out _)); Assert.False(root.TryGetProperty("max_tokens", out _));
        var messages = root.GetProperty("messages"); Assert.Equal(1, messages.GetArrayLength()); Assert.Equal("user", messages[0].GetProperty("role").GetString());
        var parts = messages[0].GetProperty("content"); Assert.Equal(1, parts.GetArrayLength()); Assert.Equal("input_audio", parts[0].GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(bytes), parts[0].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal(format, parts[0].GetProperty("input_audio").GetProperty("format").GetString());
    }

    [Fact]
    public async Task ModelConfigurationUsesPresetDefaultsAndNormalizesFullOperation_WithoutChangingLegacyProfiles()
    {
        Assert.Single(Registry.List()); Assert.Equal("mimo-asr", Registry.List()[0].Id);
        var (_, model) = await Setup(); Assert.Equal("http://synthetic-asr.invalid/v1/", model.BaseUrl); Assert.Equal("mimo-asr", model.AsrPresetId);
        Assert.DoesNotContain("synthetic-asr-key", JsonSerializer.Serialize(model));
        var edited = await Settings.SaveAsync(model.Id, new(model.Name, model.BaseUrl, model.Model, true, model.Capabilities, 30, 123, ExpectedRevision: model.Revision), default);
        Assert.Equal("mimo-asr", edited.AsrPresetId);
        await Assert.ThrowsAsync<DomainException>(() => Settings.SaveAsync(model.Id, new(model.Name, model.BaseUrl, "not-an-asr-model", true,
            model.Capabilities, 30, 123, ExpectedRevision: edited.Revision, AsrPresetId: "mimo-asr"), default));
        await Assert.ThrowsAsync<DomainException>(() => Settings.SaveAsync(null, new("mixed", model.BaseUrl, model.Model, true,
            [AiModelCapability.Text, AiModelCapability.Transcription], ApiKey: "synthetic", AsrPresetId: "mimo-asr"), default));
        await Assert.ThrowsAsync<DomainException>(() => Settings.SaveAsync(null, new("unknown", model.BaseUrl, model.Model, true,
            [AiModelCapability.Transcription], ApiKey: "synthetic", AsrPresetId: "missing"), default));
    }

    [Fact]
    public async Task FullCallRetainsExactOriginalAndSdkAudit_ReplayDoesNotStoreOrTransmitAgain()
    {
        var (request, _) = await Setup(); var result = await Caller.CallAsync(request, default);
        Assert.Equal(AiRequestStatus.Succeeded, result.Status); Assert.Equal("识别结果", result.ResponseText); Assert.Equal(1, _wire.Calls);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync(); Assert.Equal("ai.asr-call", row.Purpose); Assert.Equal(21, row.TotalTokens); Assert.Null(row.EstimatedCost);
        var original = (await Materials.DescribeAsync(row.Id, default))!; Assert.True(original.Stored);
        Assert.Equal(Wave(), (await Materials.ReadAsync(row.Id, default)).Bytes);
        Assert.Contains("pim-ai-audio:" + original.Id, row.RequestPayloadJson); Assert.DoesNotContain(request.Audio.Base64, row.RequestPayloadJson);
        using var meta = JsonDocument.Parse(row.MetadataJson); Assert.Equal(5, meta.RootElement.GetProperty("reportedAudioSeconds").GetDouble());
        Assert.Equal("mimo-asr", meta.RootElement.GetProperty("asrPresetId").GetString());
        Assert.True((await Caller.CallAsync(request, default)).Replayed); Assert.Equal(1, _wire.Calls); Assert.Equal(1, _files.Writes);
        await Assert.ThrowsAsync<DomainException>(() => Caller.CallAsync(request with { Language = "en" }, default)); Assert.Equal(1, _wire.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task EmptyTranscriptIsPreservedButMissingTranscriptIsIncomplete(string? content)
    {
        var (request, _) = await Setup(); _wire.Reply = content;
        var result = await Caller.CallAsync(request, default);
        Assert.Equal(content is null ? AiRequestStatus.Incomplete : AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("", result.ResponseText);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("preset")]
    [InlineData("version")]
    [InlineData("endpoint")]
    [InlineData("actor")]
    [InlineData("language")]
    public async Task InvalidBindingNeverSavesFilesOrContactsProvider(string problem)
    {
        var (request, _) = await Setup();
        request = problem switch {
            "consent" => request with { Consent = request.Consent with { Confirmed = false } },
            "preset" => request with { Consent = request.Consent with { PresetId = "other-preset" } },
            "version" => request with { Consent = request.Consent with { PresetVersion = "future" } },
            "endpoint" => request with { Consent = request.Consent with { Endpoint = "http://unapproved.invalid/" } },
            "actor" => request with { ExpectedUserId = Guid.NewGuid() }, _ => request with { Language = "unsupported" } };
        await Assert.ThrowsAsync<DomainException>(() => Caller.CallAsync(request, default)); Assert.Equal(0, _wire.Calls); Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public async Task OwnershipAndDigestProtectPlayback_StorageFailureNeverCallsModel()
    {
        var (request, _) = await Setup(); _files.Fail = true;
        var failed = await Caller.CallAsync(request, default); Assert.Equal(AiRequestStatus.Failed, failed.Status); Assert.Equal(0, _wire.Calls);
        Assert.Equal("audio_prepare_failed", (await _db.AiRequestLogs.SingleAsync()).ErrorCode);
        _files.Fail = false; var done = await Caller.CallAsync(request with { ClientRequestId = Guid.NewGuid() }, default);
        var owner = _owner.UserId; _owner.UserId = Guid.NewGuid();
        await Assert.ThrowsAsync<DomainException>(() => Materials.DescribeAsync(done.LogId, default));
        await Assert.ThrowsAsync<DomainException>(() => Materials.ReadAsync(done.LogId, default)); Assert.Equal(0, _files.Reads);
        _owner.UserId = owner; var audio = await _db.AiCallAudio.SingleAsync(x => x.LogId == done.LogId);
        _files.Objects[audio.ObjectKey!] = new byte[44];
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() => Materials.ReadAsync(done.LogId, default))).ErrorCode);
    }

    [Theory]
    [InlineData("length", 200)]
    [InlineData("stop", 429)]
    public async Task TruncationAndHttpErrorsRemainVisibleWithoutSdkRetry(string finish, int status)
    {
        var (request, _) = await Setup(); _wire.Finish = finish; _wire.Status = status;
        var result = await Caller.CallAsync(request, default);
        Assert.Equal(status == 200 ? AiRequestStatus.Incomplete : AiRequestStatus.Failed, result.Status); Assert.Equal(1, _wire.Calls);
        Assert.NotEqual("{}", (await _db.AiRequestLogs.SingleAsync()).ResponseRawJson);
    }

    [Fact]
    public void RegistryAcceptsAnAdditionalStrategyWithoutControllerOrUiChanges_AndRejectsDuplicateIds()
    {
        var other = new OtherPreset(); var registry = new AiAsrPresetRegistry([new MimoAsrPreset(Factory), other]);
        Assert.Equal(2, registry.List().Count); Assert.Same(other, registry.Resolve("test-asr"));
        Assert.Throws<ArgumentException>(() => new AiAsrPresetRegistry([other, other]));
    }
    [Theory]
    [InlineData("https://example.invalid/custom/chat/completions", "https://example.invalid/custom/")]
    [InlineData("https://chat/completions", "https://chat/completions/")]
    public void OperationNormalizationOnlyRemovesTheCompletePathSuffix(string input, string expected) =>
        Assert.Equal(expected, Registry.Resolve("mimo-asr").NormalizeEndpoint(input));

    [Fact]
    public async Task CancelledTransportKeepsIntentUnknownUntilFinalPersistence_AndRetainsOriginalAudio()
    {
        var (request, _) = await Setup(); _wire.Block = true; using var cancel = new CancellationTokenSource();
        var running = Caller.CallAsync(request, cancel.Token); await _wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var intent = JsonDocument.Parse((await _db.AiRequestLogs.AsNoTracking().SingleAsync()).MetadataJson))
            Assert.Equal(JsonValueKind.Null, intent.RootElement.GetProperty("providerAttempted").ValueKind);
        cancel.Cancel(); var result = await running;
        Assert.Equal(AiRequestStatus.Cancelled, result.Status); Assert.Equal(1, _wire.Calls);
        Assert.Equal(Wave(), (await Materials.ReadAsync(result.LogId, default)).Bytes);
        Assert.True((await Caller.GetAsync(request.ClientRequestId, default)).Replayed);
    }
    [Fact]
    public async Task TimeoutIsRecordedWithoutRepeatedTransport()
    {
        var (request, _) = await Setup(1); _wire.Block = true;
        var result = await Caller.CallAsync(request, default);
        Assert.Equal(AiRequestStatus.TimedOut, result.Status); Assert.Equal(1, _wire.Calls);
    }
    private sealed class OtherPreset : IAiAsrPreset
    {
        public AiAsrPresetDto Descriptor { get; } = new("test-asr", "1", "Test-only strategy", "http://test.invalid/", "transcribe", "test", ["test"], ["wav"], ["auto"], 1000, false);
        public string NormalizeEndpoint(string endpoint) => endpoint;
        public void ValidateModel(string model) { }
        public string AuditRequest(byte[] body, AiModelConnection connection, AiAsrInput audio, Guid id, string language) => "{}";
        public Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection, AiAsrInput audio, string language, CancellationToken ct) => throw new NotSupportedException("Test registration only");
    }
    private sealed class Owner : ICurrentUserService
    { public Guid? UserId { get; set; } = Guid.NewGuid(); public string? Username => "synthetic-owner"; public string? Role => "admin"; }
    private sealed class Files : IFileAttachmentStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(); public int Writes; public int Reads; public bool Fail;
        public async Task<FileAttachmentRef> StoreAsync(Guid user, string key, Stream content, string mime, long size, CancellationToken ct = default)
        { Writes++; if (Fail) throw new IOException("Synthetic unavailable Files source"); using var memory = new MemoryStream(); await content.CopyToAsync(memory, ct); Objects[key] = memory.ToArray(); return new(key, "local", user); }
        public Task<Stream> OpenReadAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default)
        { Reads++; return Task.FromResult<Stream>(new MemoryStream(Objects[reference.ObjectKey])); }
        public Task<string?> GetDirectLinkAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Wire : IHttpClientFactory
    {
        public List<string> Bodies { get; } = []; public int Calls; public string? Reply = "识别结果"; public string Finish = "stop"; public int Status = 200; public bool Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new AiTextResponseLimitHandler { InnerHandler = new Handler(this) });
        private sealed class Handler(Wire owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Calls++; owner.Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
                owner.Started.TrySetResult(); if (owner.Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                object body = owner.Status == 200 ? new { id = "synthetic-asr", @object = "chat.completion", created = 1780000000, model = "mimo-v2.5-asr",
                    choices = new[] { new { index = 0, finish_reason = owner.Finish, message = new { role = "assistant", content = owner.Reply } } },
                    usage = new { prompt_tokens = 15, completion_tokens = 6, total_tokens = 21, seconds = 5 } }
                    : new { error = new { message = "synthetic busy", type = "rate_limit_error" } };
                return new((HttpStatusCode)owner.Status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            }
        }
    }
    public void Dispose() => _db.Dispose();
}
