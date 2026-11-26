using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai;

public sealed class AiConnectionTestTests : IDisposable
{
    private const string Key = "connection-test-fixture-key";
    private readonly DbContextOptions<PimDbContext> _options = new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("ai-connection-test-" + Guid.NewGuid()).Options;
    private readonly PimDbContext _db;
    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly Owner _owner = new();
    private readonly Wire _wire = new();
    private readonly FakeAsrPreset _asr = new();
    private readonly AiTextCallGate _gate = new();
    private readonly AiModelSettingsService _settings;

    public AiConnectionTestTests()
    {
        _db = new(_options);
        _settings = new(_db, _secrets, new AiAsrPresetRegistry([_asr]));
    }

    [Fact]
    public async Task SavedRevisionRunsOneRealSdkProbeAndReplaysWithoutAnotherProviderCall()
    {
        var model = await Model();
        await Enable(model);
        var request = new AiConnectionTestRequest(Guid.NewGuid(), model.Revision, AiModelCapability.Text);

        var result = await Service().TestAsync(model.Id, request, default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, result.Status);
        Assert.Equal("text-minimal", result.Probe);
        Assert.True(result.ProviderAttempted);
        Assert.False(result.Replayed);
        Assert.Equal(1, _wire.Calls);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(result));
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("ai.connection-test", log.Purpose);
        Assert.Equal("succeeded", log.Status);
        Assert.Equal(model.Id, log.ModelProfileId);
        Assert.Equal(model.Revision, log.ModelRevision);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(log));

        var replay = await Service().TestAsync(model.Id, request, default);

        Assert.True(replay.Replayed);
        Assert.Equal(result.LogId, replay.LogId);
        Assert.Equal(1, _wire.Calls);
        Assert.Single(await _db.AiRequestLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LatestResultAndSummaryAreBoundToTheSavedRevision()
    {
        var model = await Model();
        await Enable(model);
        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        var latest = await Service().GetLatestAsync(model.Id, model.Revision,
            AiModelCapability.Text, default);
        var wrongRevision = await Service().GetLatestAsync(model.Id, Guid.NewGuid(),
            AiModelCapability.Text, default);
        var wrongCapability = await Service().GetLatestAsync(model.Id, model.Revision,
            AiModelCapability.Vision, default);
        var summaries = await Service().ListLatestAsync(default);

        Assert.NotNull(latest);
        Assert.Equal(result.LogId, latest.LogId);
        Assert.True(latest.Replayed);
        Assert.Equal(AiConnectionTestStatus.Succeeded, latest.Status);
        Assert.Null(wrongRevision);
        Assert.Null(wrongCapability);
        var summary = Assert.Single(summaries);
        Assert.Equal(model.Id, summary.ModelId);
        Assert.Equal(model.Revision, summary.ModelRevision);
        Assert.Equal(AiModelCapability.Text, summary.Capability);
        Assert.Equal(AiConnectionTestStatus.Succeeded, summary.Status);
        Assert.True(summary.ProviderAttempted);
        Assert.Equal(result.LogId, summary.LogId);
    }

    [Fact]
    public async Task ExpiredRunningRecordIsReportedAsUnknownWithoutCallingTheProviderAgain()
    {
        var model = await Model();
        await Enable(model);
        var requestId = Guid.NewGuid();
        _db.AiRequestLogs.Add(new AiRequestLogEntity
        {
            UserId = _owner.UserId,
            ClientRequestId = requestId,
            Purpose = "ai.connection-test",
            SourceObjectType = "ai-model",
            SourceObjectId = model.Id.ToString(),
            ModelProfileId = model.Id,
            ModelRevision = model.Revision,
            Model = model.Model,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            MetadataJson = """{"probe":"text-minimal","capability":"Text","providerAttempted":true}""",
            InputHash = "expired-fixture"
        });
        await _db.SaveChangesAsync();

        var latest = await Service().GetLatestAsync(model.Id, model.Revision,
            AiModelCapability.Text, default);

        Assert.NotNull(latest);
        Assert.Equal(AiConnectionTestStatus.Unknown, latest.Status);
        Assert.Equal(0, _wire.Calls);
    }

    [Fact]
    public async Task CapabilityMismatchAndStaleRevisionNeverReachTheProvider()
    {
        var model = await Model();
        await Enable(model);

        var mismatch = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Vision), default);

        Assert.Equal(AiConnectionTestStatus.CapabilityMismatch, mismatch.Status);
        Assert.False(mismatch.ProviderAttempted);
        Assert.Equal("capability_mismatch", mismatch.ErrorCode);
        Assert.Equal(0, _wire.Calls);

        var updated = await _settings.SaveAsync(model.Id, new(model.Name, model.BaseUrl, model.Model,
            model.Enabled, model.Capabilities, model.TimeoutSeconds, model.MaxOutputTokens, null,
            model.Revision), default);
        var stale = await Assert.ThrowsAsync<DomainException>(() => Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default));

        Assert.Equal(40901, stale.ErrorCode);
        Assert.Equal(0, _wire.Calls);
        Assert.NotEqual(model.Revision, updated.Revision);
    }

    [Fact]
    public async Task TwoHundredWithUnparseableBodyIsFailureNotSuccess()
    {
        var model = await Model();
        await Enable(model);
        _wire.RawBody = "not-json-but-http-200";

        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Failed, result.Status);
        Assert.True(result.ProviderAttempted);
        Assert.Equal(1, _wire.Calls);
        Assert.NotEqual(AiConnectionTestStatus.Succeeded, result.Status);
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("failed", log.Status);
        Assert.Contains("not-json-but-http-200", log.ResponseRawJson);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "provider_unavailable")]
    public async Task HttpFailuresAreClassifiedAndNeverSilentlyRetried(HttpStatusCode status, string errorCode)
    {
        var model = await Model();
        await Enable(model);
        _wire.Status = status;

        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Failed, result.Status);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.True(result.ProviderAttempted);
        Assert.Equal(1, _wire.Calls);
    }

    [Fact]
    public async Task TimeoutIsReportedWithProviderAttemptAndNoAutomaticRetry()
    {
        var model = await Model(timeout: 1);
        await Enable(model);
        _wire.WaitForCancellation = true;

        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.TimedOut, result.Status);
        Assert.True(result.ProviderAttempted);
        Assert.Equal(1, _wire.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsPersistedAsCancelledAndReplayDoesNotCallProviderAgain()
    {
        var model = await Model(timeout: 30);
        await Enable(model);
        _wire.WaitForCancellation = true;
        var request = new AiConnectionTestRequest(Guid.NewGuid(), model.Revision, AiModelCapability.Text);
        using var cancellation = new CancellationTokenSource();

        var running = Service().TestAsync(model.Id, request, cancellation.Token);
        await _wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await running;

        Assert.Equal(AiConnectionTestStatus.Cancelled, result.Status);
        Assert.Equal("cancelled", result.ErrorCode);
        Assert.Equal("cancelled", (await _db.AiRequestLogs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, _wire.Calls);

        var replay = await Service().TestAsync(model.Id, request, default);
        Assert.True(replay.Replayed);
        Assert.Equal(AiConnectionTestStatus.Cancelled, replay.Status);
        Assert.Equal(result.LogId, replay.LogId);
        Assert.Equal(1, _wire.Calls);
    }

    [Fact]
    public async Task RotatingTheCredentialCreatesANewRevisionAndOldRevisionCannotTestOrReplayTheNewKey()
    {
        var model = await Model();
        await Enable(model);
        var firstRequest = new AiConnectionTestRequest(Guid.NewGuid(), model.Revision, AiModelCapability.Text);
        var first = await Service().TestAsync(model.Id, firstRequest, default);
        Assert.Equal("Bearer " + Key, _wire.Authorization);

        var rotated = await _settings.SaveAsync(model.Id, new(model.Name, model.BaseUrl, model.Model,
            model.Enabled, model.Capabilities, model.TimeoutSeconds, model.MaxOutputTokens,
            "rotated-connection-key", model.Revision), default);

        Assert.NotEqual(model.Revision, rotated.Revision);
        Assert.True(rotated.HasApiKey);
        var stale = await Assert.ThrowsAsync<DomainException>(() => Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default));
        Assert.Equal(40901, stale.ErrorCode);
        Assert.Equal(1, _wire.Calls);

        var second = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), rotated.Revision, AiModelCapability.Text), default);
        Assert.Equal(AiConnectionTestStatus.Succeeded, second.Status);
        Assert.Equal("Bearer rotated-connection-key", _wire.Authorization);
        Assert.Equal(2, _wire.Calls);

        Assert.Equal(first.LogId,
            (await Service().GetLatestAsync(model.Id, model.Revision, AiModelCapability.Text, default))!.LogId);
        Assert.Equal(second.LogId,
            (await Service().GetLatestAsync(model.Id, rotated.Revision, AiModelCapability.Text, default))!.LogId);
    }

    [Fact]
    public async Task SaturatedConcurrencyGateBlocksBeforeNetwork()
    {
        var model = await Model();
        await Enable(model);
        for (var index = 0; index < 4; index++) Assert.True(_gate.TryEnter());

        try
        {
            var result = await Service().TestAsync(model.Id,
                new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

            Assert.Equal(AiConnectionTestStatus.Blocked, result.Status);
            Assert.Equal("busy", result.ErrorCode);
            Assert.False(result.ProviderAttempted);
            Assert.Equal(0, _wire.Calls);
        }
        finally
        {
            for (var index = 0; index < 4; index++) _gate.Exit();
        }
    }

    [Fact]
    public async Task ThreeTransientFailuresOpenTheCircuitAndTheWindowLetsOneProbeRecover()
    {
        var clock = new ManualTimeProvider();
        _wire.Breaker = new AiProviderCircuitBreaker(clock);
        _wire.Status = HttpStatusCode.InternalServerError;
        var model = await Model();
        await Enable(model);

        for (var index = 0; index < 3; index++)
        {
            var failed = await Service().TestAsync(model.Id,
                new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);
            Assert.Equal(AiConnectionTestStatus.Failed, failed.Status);
            Assert.Equal("provider_unavailable", failed.ErrorCode);
            Assert.True(failed.ProviderAttempted);
        }
        Assert.Equal(3, _wire.Calls);

        var blocked = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Blocked, blocked.Status);
        Assert.Equal("circuit_open", blocked.ErrorCode);
        Assert.False(blocked.ProviderAttempted);
        Assert.Equal(3, _wire.Calls);

        _wire.Status = HttpStatusCode.OK;
        clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, recovered.Status);
        Assert.True(recovered.ProviderAttempted);
        Assert.Equal(4, _wire.Calls);
    }

    [Fact]
    public async Task ThreeTimeoutsOpenTheCircuitAndHalfOpenTimeoutReopensWithoutReleasing()
    {
        var clock = new ManualTimeProvider();
        _wire.Breaker = new AiProviderCircuitBreaker(clock);
        _wire.WaitForCancellation = true;
        var model = await Model(timeout: 1);
        await Enable(model);

        for (var index = 0; index < 3; index++)
        {
            var timedOut = await Service().TestAsync(model.Id,
                new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);
            Assert.Equal(AiConnectionTestStatus.TimedOut, timedOut.Status);
            Assert.Equal("timeout", timedOut.ErrorCode);
            Assert.True(timedOut.ProviderAttempted);
        }
        Assert.Equal(3, _wire.Calls);

        var blocked = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Blocked, blocked.Status);
        Assert.Equal("circuit_open", blocked.ErrorCode);
        Assert.False(blocked.ProviderAttempted);
        Assert.Equal(3, _wire.Calls);

        clock.Advance(TimeSpan.FromSeconds(31));
        var probeTimedOut = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.TimedOut, probeTimedOut.Status);
        Assert.Equal("timeout", probeTimedOut.ErrorCode);
        Assert.True(probeTimedOut.ProviderAttempted);
        Assert.Equal(4, _wire.Calls);

        // A timed-out half-open probe must re-open the breaker instead of abandoning the probe.
        var blockedAfterProbeTimeout = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Blocked, blockedAfterProbeTimeout.Status);
        Assert.Equal("circuit_open", blockedAfterProbeTimeout.ErrorCode);
        Assert.False(blockedAfterProbeTimeout.ProviderAttempted);
        Assert.Equal(4, _wire.Calls);

        _wire.WaitForCancellation = false;
        _wire.Status = HttpStatusCode.OK;
        clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, recovered.Status);
        Assert.True(recovered.ProviderAttempted);
        Assert.Equal(5, _wire.Calls);
    }

    [Fact]
    public async Task HalfOpenCallerCancellationAbandonsProbeWithoutTrippingOrReopeningCircuit()
    {
        var clock = new ManualTimeProvider();
        _wire.Breaker = new AiProviderCircuitBreaker(clock);
        _wire.Status = HttpStatusCode.InternalServerError;
        var model = await Model(timeout: 30);
        await Enable(model);

        for (var index = 0; index < 3; index++)
        {
            var failed = await Service().TestAsync(model.Id,
                new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);
            Assert.Equal(AiConnectionTestStatus.Failed, failed.Status);
        }
        Assert.Equal(3, _wire.Calls);

        clock.Advance(TimeSpan.FromSeconds(31));
        _wire.WaitForCancellation = true;
        _wire.ResetStarted();
        using var cancellation = new CancellationTokenSource();
        var probe = Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), cancellation.Token);
        await _wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await probe;

        Assert.Equal(AiConnectionTestStatus.Cancelled, cancelled.Status);
        Assert.Equal("cancelled", cancelled.ErrorCode);
        Assert.Equal(4, _wire.Calls);

        _wire.WaitForCancellation = false;
        _wire.Status = HttpStatusCode.OK;
        var next = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, next.Status);
        Assert.True(next.ProviderAttempted);
        Assert.Equal(5, _wire.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", false)]
    [InlineData(HttpStatusCode.OK, "connection_failed", true)]
    public async Task RateLimitAndConnectionFailuresOpenCircuitAndPreserveBehavior(
        HttpStatusCode status, string errorCode, bool throwConnectionFailure)
    {
        var clock = new ManualTimeProvider();
        _wire.Breaker = new AiProviderCircuitBreaker(clock);
        _wire.Status = status;
        _wire.ThrowConnectionFailure = throwConnectionFailure;
        var model = await Model();
        await Enable(model);

        for (var index = 0; index < 3; index++)
        {
            var failed = await Service().TestAsync(model.Id,
                new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);
            Assert.Equal(AiConnectionTestStatus.Failed, failed.Status);
            Assert.Equal(errorCode, failed.ErrorCode);
            Assert.True(failed.ProviderAttempted);
        }

        var blocked = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Blocked, blocked.Status);
        Assert.Equal("circuit_open", blocked.ErrorCode);
        Assert.False(blocked.ProviderAttempted);
        Assert.Equal(3, _wire.Calls);
    }

    [Fact]
    public async Task DirectChatClientNetworkTimeoutTripsCircuitBreakerWithoutServiceScope()
    {
        var clock = new ManualTimeProvider();
        _wire.Breaker = new AiProviderCircuitBreaker(clock);
        _wire.WaitForCancellation = true;
        var factory = new AiProfileChatClientFactory(_wire);

        for (var index = 0; index < 3; index++)
        {
            using var lease = factory.Create(new AiModelConnection(
                "https://fixture.invalid/v1", "fixture-model", Key, 1, 64));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                lease.Client.GetResponseAsync(
                    [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "ping")],
                    cancellationToken: CancellationToken.None));
        }

        using var blockedLease = factory.Create(new AiModelConnection(
            "https://fixture.invalid/v1", "fixture-model", Key, 1, 64));
        await Assert.ThrowsAsync<AiProviderCircuitOpenException>(() =>
            blockedLease.Client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "ping")],
                cancellationToken: CancellationToken.None));
        Assert.Equal(3, _wire.Calls);
    }

    [Fact]
    public async Task UsageLimitBlocksBeforeNetworkAndIsAudited()
    {
        var connection = new AiProviderConnectionEntity
        {
            Id = Guid.NewGuid(), Name = "测试连接", NormalizedName = "测试连接"
        };
        _db.AiProviderConnections.Add(connection);
        _db.AiUsageLimitRules.Add(new AiUsageLimitRuleEntity
        {
            ConnectionId = connection.Id, Enabled = true, Period = AiUsageLimitPeriods.Month,
            WarningPercent = 80, TokenLimit = 100, TimeZoneId = "Asia/Shanghai"
        });
        _db.AiRequestLogs.Add(new AiRequestLogEntity
        {
            Module = "system", Purpose = "ai.text-call", SourceObjectType = "ai-model",
            SourceObjectId = "usage-limit-fixture", Model = "fixture-model",
            CorrelationId = Guid.NewGuid().ToString("N"), Status = "succeeded",
            StartedAt = DateTimeOffset.UtcNow, TotalTokens = 100,
            ProviderConnectionId = connection.Id
        });
        await _db.SaveChangesAsync();
        var model = await _settings.SaveAsync(null, new("限额模型", "https://fixture.invalid/v1",
            "fixture-model", true, [AiModelCapability.Text], 60, 64, Key,
            ProviderConnectionId: connection.Id), default);
        await Enable(model);

        var result = await Service(new AiUsageLimitService(_db, _owner)).TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Blocked, result.Status);
        Assert.Equal("usage_limit_reached", result.ErrorCode);
        Assert.False(result.ProviderAttempted);
        Assert.Equal(0, _wire.Calls);
        Assert.Equal("blocked", (await _db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Purpose == "ai.connection-test")).Status);
    }

    [Fact]
    public async Task ProviderEchoedCredentialIsRedactedFromResultAndAuditLog()
    {
        var model = await Model();
        await Enable(model);
        _wire.ResponseText = "OK " + Key;
        _wire.ExtraJson = JsonSerializer.Serialize(new { leaked = Key });

        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, result.Status);
        Assert.NotNull(result.ResponsePreview);
        Assert.DoesNotContain(Key, result.ResponsePreview);
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(log));
        Assert.True(log.ContentRedacted);
    }

    [Fact]
    public async Task SameRequestIdForDifferentModelIsConflictAndDoesNotLeakPreviousResult()
    {
        var first = await Model();
        await Enable(first);
        var second = await Model();
        var request = new AiConnectionTestRequest(Guid.NewGuid(), first.Revision, AiModelCapability.Text);
        await Service().TestAsync(first.Id, request, default);

        var conflict = await Assert.ThrowsAsync<DomainException>(() => Service().TestAsync(
            second.Id, request with { ExpectedRevision = second.Revision }, default));

        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(1, _wire.Calls);
    }

    [Fact]
    public async Task AsrUsesTheBuiltInSpeechSampleAndAcceptsMatchingTranscript()
    {
        var model = await NAsrModel();
        await EnableAsr(model);
        _asr.Text = "Connection test.";

        var result = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Transcription), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, result.Status);
        Assert.True(result.ProviderAttempted);
        Assert.Equal("asr-speech-mp3", result.Probe);
        Assert.Equal("mp3", _asr.Received?.Format);
        Assert.True(_asr.Received!.Bytes.Length > 3000);
        Assert.NotEmpty(_asr.Received.Sha256);
        Assert.Equal(0, _wire.Calls);
    }

    [Fact]
    public async Task AsrHttpSuccessWithUnrelatedOrEmptyTextIsFailure()
    {
        var model = await NAsrModel();
        await EnableAsr(model);
        _asr.Text = "OK";

        var unrelated = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Transcription), default);

        Assert.Equal(AiConnectionTestStatus.Failed, unrelated.Status);
        Assert.Equal("asr_probe_mismatch", unrelated.ErrorCode);
        Assert.True(unrelated.ProviderAttempted);

        _asr.Text = "";
        var empty = await Service().TestAsync(model.Id,
            new(Guid.NewGuid(), model.Revision, AiModelCapability.Transcription), default);

        Assert.Equal(AiConnectionTestStatus.Failed, empty.Status);
        Assert.Equal("asr_probe_mismatch", empty.ErrorCode);
        Assert.Equal(0, _wire.Calls);
    }

    private AiConnectionTestService Service(AiUsageLimitService? usageLimits = null) =>
        new(_db, _secrets, new AiProfileChatClientFactory(_wire), _owner,
            new AiAsrPresetRegistry([_asr]), _gate, usageLimits);

    private async Task<AiModelDto> Model(int timeout = 60) =>
        await _settings.SaveAsync(null, new("模型" + Guid.NewGuid().ToString("N"),
            "https://fixture.invalid/v1", "fixture-model", true, [AiModelCapability.Text],
            timeout, 64, Key), default);

    private async Task Enable(AiModelDto model)
    {
        var current = await _settings.GetSettingsAsync(default);
        await _settings.SaveSettingsAsync(new(current.Revision, true, model.Id, null, null), default);
    }

    private async Task<AiModelDto> NAsrModel() =>
        await _settings.SaveAsync(null, new("ASR模型" + Guid.NewGuid().ToString("N"),
            "https://fixture.invalid/v1", _asr.Descriptor.DefaultModel, true,
            [AiModelCapability.Transcription], 5, 64, Key, AsrPresetId: _asr.Descriptor.Id), default);

    private async Task EnableAsr(AiModelDto model)
    {
        var current = await _settings.GetSettingsAsync(default);
        await _settings.SaveSettingsAsync(new(current.Revision, true, null, null, model.Id), default);
    }

    public void Dispose()
    {
        _db.Dispose();
        _wire.Dispose();
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Role => "admin";
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseText { get; set; } = "OK";
        public string? RawBody { get; set; }
        public string? ExtraJson { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool ThrowConnectionFailure { get; set; }
        public AiProviderCircuitBreaker? Breaker { get; set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Authorization { get; private set; }

        public void ResetStarted() =>
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient CreateClient(string name)
        {
            HttpMessageHandler pipeline = new AiTextResponseLimitHandler { InnerHandler = this };
            if (Breaker is not null)
                pipeline = new AiProviderCircuitBreakerHandler(Breaker) { InnerHandler = pipeline };
            return new HttpClient(pipeline, false) { Timeout = Timeout.InfiniteTimeSpan };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Started.TrySetResult();
            if (ThrowConnectionFailure) throw new HttpRequestException("fixture connection failure");
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            var body = RawBody ?? (Status != HttpStatusCode.OK
                ? JsonSerializer.Serialize(new { error = new { message = "fixture failure" } })
                : JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-connection-test",
                    @object = "chat.completion",
                    created = 1780000000,
                    model = "fixture-model",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            finish_reason = "stop",
                            message = new { role = "assistant", content = ResponseText }
                        }
                    },
                    usage = new { prompt_tokens = 2, completion_tokens = 2, total_tokens = 4 },
                    fixture = ExtraJson is null ? (JsonElement?)null : ParseClone(ExtraJson)
                }));
            return new(statusCode: Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static JsonElement ParseClone(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeAsrPreset : IAiAsrPreset
    {
        public AiAsrPresetDto Descriptor { get; } = new("test-asr", "1", "测试ASR",
            "https://fixture.invalid/v1/", "chat/completions", "test-asr-model",
            ["test-asr-model"], ["mp3"], ["en"], 4 * 1024 * 1024, false);
        public AiAsrInput? Received { get; private set; }
        public string Text { get; set; } = "Connection test.";

        public string NormalizeEndpoint(string value) => AiModelSettingsService.Endpoint(value);
        public void ValidateModel(string model)
        {
            if (model != Descriptor.DefaultModel) throw new DomainException(40001, "测试ASR模型无效");
        }
        public string AuditRequest(byte[] sdkBody, AiModelConnection connection, AiAsrInput audio,
            Guid audioId, string language, AiAsrPromptSnapshot? prompt = null) => "{}";

        public Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection, AiAsrInput audio,
            string language, CancellationToken ct, AiAsrPromptSnapshot? prompt = null)
        {
            Received = audio;
            return Task.FromResult(new AiAsrProviderResult(Text, "stop", 10, 2, 12, "{}",
                !string.IsNullOrWhiteSpace(Text), 1.3));
        }
    }
}
