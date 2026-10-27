using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai;

public sealed class AiSdkTextCallTests : IDisposable
{
    private const string Key = "isolated-fixture-key";
    private readonly DbContextOptions<PimDbContext> _options = new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("ai-sdk-" + Guid.NewGuid()).Options;
    private readonly PimDbContext _db;
    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly Owner _owner = new();
    private readonly Wire _wire = new();
    private readonly AiTextCallGate _gate = new();
    private AiModelSettingsService Settings => new(_db, _secrets);
    private AiTextCallService Caller => new(_db, _secrets, new AiProfileChatClientFactory(_wire), _owner, _gate);

    public AiSdkTextCallTests() => _db = new(_options);
    private async Task<AiModelDto> Model(int timeout = 60, AiModelCapability[]? capabilities = null) =>
        await Settings.SaveAsync(null, new("模型" + Guid.NewGuid().ToString("N"), "https://fixture.invalid/v1", "fixture-model", true,
            capabilities ?? [AiModelCapability.Text], timeout, 123, Key), default);
    private async Task Enable(AiModelDto model)
    {
        var current = await Settings.GetSettingsAsync(default);
        await Settings.SaveSettingsAsync(new(current.Revision, true, model.Id, null, null), default);
    }
    private AiTextCallRequest Request(string prompt = "你好\npassword=ordinary-user-data", Guid? model = null) =>
        new(Guid.NewGuid(), _owner.UserId!.Value, prompt, "请完整回答\n不要丢弃上下文", model);

    [Fact]
    public async Task SettingsEncryptKeysRetainBlankEditSecretsAndRejectStaleOrReferencedMutations()
    {
        var model = await Model(capabilities: [AiModelCapability.Text, AiModelCapability.Vision]);
        var row = await _db.AiModelProfiles.AsNoTracking().SingleAsync();
        Assert.NotEqual(Key, row.ApiKeySecret); Assert.Equal(Key, _secrets.Unprotect(row.ApiKeySecret));
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(model)); Assert.True(model.HasApiKey);
        var updated = await Settings.SaveAsync(model.Id, new("改名", model.BaseUrl, model.Model, true,
            model.Capabilities, 10, 456, null, model.Revision), default);
        Assert.NotEqual(model.Revision, updated.Revision);
        Assert.Equal(Key, _secrets.Unprotect((await _db.AiModelProfiles.AsNoTracking().SingleAsync()).ApiKeySecret));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Settings.SaveAsync(model.Id,
            new("旧稿", model.BaseUrl, model.Model, true, model.Capabilities, 10, 123, null, model.Revision), default));
        Assert.Equal(40901, stale.ErrorCode);
        var config = await Settings.GetSettingsAsync(default);
        config = await Settings.SaveSettingsAsync(new(config.Revision, true, model.Id, model.Id, null), default);
        await Assert.ThrowsAsync<DomainException>(() => Settings.DeleteAsync(model.Id, updated.Revision, default));
        await Assert.ThrowsAsync<DomainException>(() => Settings.SaveSettingsAsync(new(Guid.Empty, false, null, null, null), default));
        await Settings.SaveSettingsAsync(new(config.Revision, false, null, null, null), default);
        await Settings.DeleteAsync(model.Id, updated.Revision, default);
        Assert.Empty(await Settings.ListAsync(default));
    }

    [Fact]
    public async Task SdkCanBeTurnedOffAfterItsDefaultProfileIsDisabled()
    {
        var model = await Model(); await Enable(model);
        await Settings.SaveAsync(model.Id, new(model.Name, model.BaseUrl, model.Model, false,
            model.Capabilities, model.TimeoutSeconds, model.MaxOutputTokens, null, model.Revision), default);
        var config = await Settings.GetSettingsAsync(default);

        var disabled = await Settings.SaveSettingsAsync(new(config.Revision, false, model.Id, null, null), default);

        Assert.False(disabled.Enabled); Assert.Equal(model.Id, disabled.TextModelId);
        var blocked = await Caller.CallAsync(Request(), default);
        Assert.Equal(AiRequestStatus.Blocked, blocked.Status); Assert.Equal(0, _wire.Calls);
        await Assert.ThrowsAsync<DomainException>(() => Settings.SaveSettingsAsync(
            new(disabled.Revision, true, model.Id, null, null), default));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("https://example.com/v1?api_key=credential")]
    [InlineData("https://example.com/v1#fragment")]
    public void EndpointsNeverAcceptCredentialUrlsOrNonHttpSchemes(string url) =>
        Assert.Throws<DomainException>(() => AiModelSettingsService.Endpoint(url));

    [Fact]
    public async Task RealSdkSerializesExactMessagesAndReplyUsageThenReplaysWithoutAnotherProviderAttempt()
    {
        var model = await Model(); await Enable(model); var request = Request();
        var result = await Caller.CallAsync(request, default);
        Assert.Equal(AiRequestStatus.Succeeded, result.Status); Assert.Equal(Wire.Reply, result.ResponseText);
        Assert.Equal(1, _wire.Calls);
        using var sent = JsonDocument.Parse(_wire.Requests.Single());
        Assert.Equal("fixture-model", sent.RootElement.GetProperty("model").GetString());
        var sentMessages = sent.RootElement.GetProperty("messages");
        Assert.Equal(request.SystemPrompt, sentMessages[0].GetProperty("content").GetString());
        Assert.Equal(request.Prompt, sentMessages[1].GetProperty("content").GetString());
        Assert.EndsWith("/v1/chat/completions", _wire.LastUrl);
        Assert.Equal("Bearer " + Key, _wire.Authorization);
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        using var saved = JsonDocument.Parse(log.RequestMessagesJson);
        Assert.Equal(request.Prompt, saved.RootElement[1].GetProperty("content").GetString());
        Assert.Equal(request.SystemPrompt, saved.RootElement[0].GetProperty("content").GetString());
        Assert.Contains("password=ordinary-user-data", log.ResponseText);
        Assert.Equal(7, log.PromptTokens); Assert.Equal(11, log.CompletionTokens); Assert.Equal(18, log.TotalTokens);
        Assert.Equal(model.Id, log.ModelProfileId); Assert.Equal(model.Revision, log.ModelRevision);
        Assert.False(log.ContentRedacted); Assert.Null(log.EstimatedCost); Assert.Null(log.Currency);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(log));
        var replayed = await Caller.CallAsync(request, default);
        Assert.True(replayed.Replayed); Assert.Equal(result.LogId, replayed.LogId); Assert.Equal(1, _wire.Calls);
        var conflict = await Assert.ThrowsAsync<DomainException>(() => Caller.CallAsync(request with { Prompt = "另一份输入" }, default));
        Assert.Equal(40901, conflict.ErrorCode); Assert.Equal(1, _wire.Calls);
        Assert.Equal(result.LogId, (await Caller.GetAsync(request.ClientRequestId, default)).LogId);
    }

    [Fact]
    public async Task CredentialEchoIsMarkedAndHiddenWhileOrdinaryUserContentStaysIntact()
    {
        var model = await Model(); await Enable(model);
        _wire.ResponseText = Wire.Reply + " " + Key;
        var result = await Caller.CallAsync(Request("ordinary password=123; " + Key), default);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.True(row.ContentRedacted); Assert.DoesNotContain(Key, JsonSerializer.Serialize(row));
        Assert.Contains("password=123", row.RequestMessagesJson);
        Assert.Contains("[REDACTED_CREDENTIAL]", result.ResponseText);
        using var valid = JsonDocument.Parse(row.ResponseRawJson);
        Assert.Equal("[REDACTED_CREDENTIAL]", valid.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Split(' ')[^1]);
    }

    [Fact]
    public async Task Upstream429HasOneAttemptAndKeepsActualErrorBodyWithoutCredentials()
    {
        var model = await Model(); await Enable(model); _wire.Status = HttpStatusCode.TooManyRequests;
        _wire.Error = JsonSerializer.Serialize(new { error = new { message = "busy; " + Key, code = "rate_limit" } });
        var result = await Caller.CallAsync(Request(), default);
        Assert.Equal(AiRequestStatus.Failed, result.Status); Assert.Equal(1, _wire.Calls);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("upstream_http_429", row.ErrorCode); Assert.Contains("rate_limit", row.ResponseRawJson);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task InvalidJsonReplyIsRetainedAndOversizedResponseFailsExplicitly()
    {
        var model = await Model(); await Enable(model);
        _wire.RawBody = "not-json:这是实际错误回复";
        Assert.Equal(AiRequestStatus.Failed, (await Caller.CallAsync(Request(), default)).Status);
        var first = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        using var body = JsonDocument.Parse(first.ResponseRawJson);
        Assert.Equal(_wire.RawBody, body.RootElement.GetProperty("body").GetString());
        _wire.RawBody = new string('x', AiTextResponseLimitHandler.MaxBytes + 1);
        var tooLarge = await Caller.CallAsync(Request(), default);
        var stored = await _db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == tooLarge.LogId);
        Assert.Equal(AiRequestStatus.Failed, tooLarge.Status); Assert.Equal("response_too_large", stored.ErrorCode);
    }

    [Fact]
    public async Task TruncatedOrMissingFinishReasonIsNotReportedAsCompleteAndMissingUsageRemainsUnknown()
    {
        var model = await Model(); await Enable(model); _wire.Finish = "length"; _wire.IncludeUsage = false;
        var result = await Caller.CallAsync(Request(), default);
        Assert.Equal(AiRequestStatus.Incomplete, result.Status); Assert.Equal(Wire.Reply, result.ResponseText);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("length", row.FinishReason); Assert.Null(row.PromptTokens); Assert.Null(row.TotalTokens);
        var usage = await new AiUsageService(_db, Options.Create(new AiOptions())).GetUsageSummaryAsync(null, null);
        Assert.Equal(1, usage.FailureCount); Assert.Equal(1, usage.UnknownTokenUsageCount);
        Assert.Equal(0, usage.KnownCostRequestCount);
    }

    [Fact]
    public async Task CancellationAndTimeoutPersistDifferentOutcomes()
    {
        var model = await Model(1); await Enable(model); _wire.WaitForCancellation = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        Assert.Equal(AiRequestStatus.Cancelled, (await Caller.CallAsync(Request(), cancel.Token)).Status);
        Assert.Equal(AiRequestStatus.TimedOut, (await Caller.CallAsync(Request(), default)).Status);
        var rows = await _db.AiRequestLogs.AsNoTracking().ToListAsync();
        Assert.Contains(rows, r => r.Status == "cancelled"); Assert.Contains(rows, r => r.Status == "timed_out");
        Assert.All(rows, r => Assert.NotNull(r.FinishedAt));
    }

    [Fact]
    public async Task DisabledCapabilitiesAndChangedActorNeverSendModelRequests()
    {
        var model = await Model();
        Assert.Equal(AiRequestStatus.Blocked, (await Caller.CallAsync(Request(), default)).Status);
        var vision = await Model(capabilities: [AiModelCapability.Vision]); await Enable(model);
        Assert.Equal(AiRequestStatus.Blocked, (await Caller.CallAsync(Request(model: vision.Id), default)).Status);
        var error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallAsync(Request() with { ExpectedUserId = Guid.NewGuid() }, default));
        Assert.Equal(40301, error.ErrorCode); Assert.Equal(0, _wire.Calls);
    }

    [Fact]
    public async Task ExpiredInFlightRecordBecomesUnconfirmedWithoutReexecuting()
    {
        var request = Request();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { request.ModelId, request.SystemPrompt, request.Prompt }, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();
        _db.AiRequestLogs.Add(new AiRequestLogEntity { UserId = _owner.UserId, ClientRequestId = request.ClientRequestId,
            InputHash = fingerprint, Status = "running", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await _db.SaveChangesAsync();
        var result = await Caller.CallAsync(request, default);
        Assert.Equal(AiRequestStatus.Incomplete, result.Status); Assert.True(result.Replayed); Assert.Equal(0, _wire.Calls);
        Assert.Equal("outcome_unknown", (await _db.AiRequestLogs.AsNoTracking().SingleAsync()).ErrorCode);
    }

    public void Dispose() { _db.Dispose(); _wire.Dispose(); }
    private sealed class Owner : ICurrentUserService { public Guid? UserId { get; } = Guid.NewGuid(); public string Role => "admin"; }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public const string Reply = "完整回复\npassword=ordinary-user-data";
        public int Calls; public string? LastUrl; public string? Authorization;
        public List<string> Requests { get; } = [];
        public string ResponseText { get; set; } = Reply;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Error { get; set; } = "{}";
        public string? Finish { get; set; } = "stop";
        public bool IncludeUsage { get; set; } = true;
        public bool WaitForCancellation { get; set; }
        public string? RawBody { get; set; }
        public HttpClient CreateClient(string name) => new(new AiTextResponseLimitHandler { InnerHandler = this }, false) { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; LastUrl = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString();
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            var body = RawBody ?? (Status != HttpStatusCode.OK ? Error : JsonSerializer.Serialize(new
            {
                id = "chatcmpl-fixture", created = 1780000000, model = "fixture-model", @object = "chat.completion",
                choices = new[] { new { index = 0, finish_reason = Finish, message = new { role = "assistant", content = ResponseText } } },
                usage = IncludeUsage ? new { prompt_tokens = 7, completion_tokens = 11, total_tokens = 18 } : null
            }));
            return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
