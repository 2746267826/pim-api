using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using static Pim.Infrastructure.Ai.AiCallAudit;

namespace Pim.Infrastructure.Ai;

public interface IAiConnectionTestService
{
    Task<AiConnectionTestResultDto> TestAsync(Guid modelId, AiConnectionTestRequest request,
        CancellationToken ct);
    Task<AiConnectionTestResultDto?> GetLatestAsync(Guid modelId, Guid revision,
        AiModelCapability capability, CancellationToken ct);
    Task<IReadOnlyList<AiConnectionTestSummaryDto>> ListLatestAsync(CancellationToken ct);
}

/// <summary>
/// One minimal, real provider request against an already-saved model revision. A 2xx response is
/// only accepted after the provider protocol parser returns a readable result; local mocks or a
/// saved key echo never count as a successful provider connection.
/// </summary>
public sealed class AiConnectionTestService(
    PimDbContext db,
    ISecretProtector secrets,
    IAiProfileChatClientFactory clients,
    ICurrentUserService currentUser,
    AiAsrPresetRegistry presets,
    AiTextCallGate gate,
    AiUsageLimitService? usageLimits = null) : IAiConnectionTestService
{
    private const int ChatTestOutputTokens = 16;
    private const int AsrTestOutputTokens = 128;
    private const int PreviewChars = 240;
    private const string PurposeKey = "ai.connection-test";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiConnectionTestResultDto> TestAsync(Guid modelId, AiConnectionTestRequest request,
        CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (request.ClientRequestId == Guid.Empty)
            throw Invalid("测试请求标识无效");
        if (!Enum.IsDefined(request.Capability))
            throw Invalid("测试能力无效");

        var profile = await db.AiModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == modelId, ct)
            ?? throw new DomainException(40401, "模型配置不存在");
        if (profile.Revision != request.ExpectedRevision)
            throw new DomainException(40901, "模型配置已被修改，测试请求未发送；请刷新核对后再测试");

        var probe = ProbeName(request.Capability);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            kind = PurposeKey,
            modelId = profile.Id,
            revision = profile.Revision,
            capability = request.Capability
        }, Json));
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var existing = await FindAsync(owner, request.ClientRequestId, ct);
        if (existing is not null) return Replay(existing, fingerprint);

        var runtimeEnabled = await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct);
        var (configurationError, capabilityMismatch) = Preflight(profile, runtimeEnabled, request.Capability);
        var started = DateTimeOffset.UtcNow;
        var row = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = request.ClientRequestId,
            InputHash = fingerprint,
            Module = "system",
            Purpose = PurposeKey,
            SourceObjectType = "ai-model",
            SourceObjectId = profile.Id.ToString(),
            Provider = profile.AsrPresetId is { Length: > 0 } preset ? preset : "openai-compatible",
            Model = profile.Model,
            ModelProfileId = profile.Id,
            ModelProfileName = profile.Name,
            ProviderConnectionId = profile.ProviderConnectionId,
            ModelRevision = profile.Revision,
            ModelEndpoint = profile.BaseUrl,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = configurationError is null ? "running" : "blocked",
            AttemptNumber = 1,
            MaxAttempts = 1,
            StartedAt = started,
            InitiatedAt = started,
            FinishedAt = configurationError is null ? null : started,
            ExecutionExpiresAt = started.AddSeconds(profile.TimeoutSeconds + 60),
            RequestMessagesJson = RequestSnapshot(request.Capability),
            RequestPayloadJson = JsonSerializer.Serialize(new
            {
                probe,
                capability = request.Capability.ToString(),
                model = profile.Model,
                maxOutputTokens = request.Capability == AiModelCapability.Transcription
                    ? AsrTestOutputTokens : ChatTestOutputTokens,
                timeoutSeconds = profile.TimeoutSeconds
            }, Json),
            ErrorCode = configurationError is null ? null
                : capabilityMismatch ? "capability_mismatch" : "configuration_unavailable",
            ErrorMessage = configurationError,
            MetadataJson = JsonSerializer.Serialize(new
            {
                probe,
                capability = request.Capability.ToString(),
                providerAttempted = false,
                method = "saved-model-connection-test"
            }, Json)
        };

        db.AiRequestLogs.Add(row);
        try
        {
            // Durable idempotency placeholder wins before any credential is decrypted or network starts.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await FindAsync(owner, request.ClientRequestId, ct);
            if (winner is null) throw;
            return Replay(winner, fingerprint);
        }

        if (configurationError is not null)
            return await FinishAsync(row, profile, probe, request.Capability,
                    capabilityMismatch ? AiConnectionTestStatus.CapabilityMismatch : AiConnectionTestStatus.Blocked,
                null, row.ErrorCode, configurationError, providerAttempted: false);

        string key;
        try
        {
            key = secrets.Unprotect(profile.ApiKeySecret);
            if (string.IsNullOrEmpty(key))
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Blocked, null, "configuration_unavailable",
                    "模型凭据不可用，请重新保存后再测试", providerAttempted: false);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return await FinishAsync(row, profile, probe, request.Capability,
                AiConnectionTestStatus.Blocked, null, "configuration_unavailable",
                "模型凭据无法读取，请重新保存后再测试", providerAttempted: false);
        }

        var isVision = request.Capability == AiModelCapability.Vision;
        var isAsr = request.Capability == AiModelCapability.Transcription;
        if (!gate.TryEnter(vision: isVision, asr: isAsr))
            return await FinishAsync(row, profile, probe, request.Capability,
                AiConnectionTestStatus.Blocked, null, "busy",
                "当前并发已达上限，本次测试未发送服务商；请稍后明确重试", providerAttempted: false);

        AiUsageLimitLease? usageLease = null;
        var providerAttempted = false;
        try
        {
            if (usageLimits is not null && profile.ProviderConnectionId is { } connectionId)
            {
                var decision = await usageLimits.TryBeginAsync(connectionId, row.CorrelationId, PurposeKey,
                    TimeSpan.FromSeconds(profile.TimeoutSeconds + 60), ct);
                if (!decision.Allowed)
                    return await FinishAsync(row, profile, probe, request.Capability,
                        AiConnectionTestStatus.Blocked, null,
                        decision.ErrorCode ?? "usage_limit_blocked",
                        decision.Message ?? "AI用量限制拒绝本次测试",
                        providerAttempted: false);
                usageLease = decision.Lease;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            using var wire = new AiTextWireCapture();
            try
            {
                if (isAsr)
                {
                    var asrPreset = presets.Resolve(profile.AsrPresetId);
                    asrPreset.ValidateModel(profile.Model);
                    var endpoint = asrPreset.NormalizeEndpoint(profile.BaseUrl);
                    var audio = new AiAsrInput("connection-test.mp3", "mp3", ConnectionTestMedia.Audio,
                        ConnectionTestMedia.AudioSha256);
                    var connection = new AiModelConnection(endpoint, profile.Model, key,
                        profile.TimeoutSeconds, AsrTestOutputTokens);
                    providerAttempted = true;
                    var result = await asrPreset.TranscribeAsync(connection, audio, "auto", linked.Token);
                    var accepted = TranscriptMatches(result.Text);
                    row.ResponseText = Safe(result.Text, key, out var asrRedacted);
                    row.ResponseRawJson = SafeJson(ValidJson(result.RawJson), key, out var asrRawRedacted);
                    row.ContentRedacted = asrRedacted || asrRawRedacted;
                    row.PromptTokens = Count(result.InputTokens);
                    row.CompletionTokens = Count(result.OutputTokens);
                    row.TotalTokens = Count(result.TotalTokens);
                    return accepted
                        ? await FinishAsync(row, profile, probe, request.Capability,
                            AiConnectionTestStatus.Succeeded,
                            Preview(row.ResponseText), null,
                            "语音转写测试成功：服务商返回了与内置语音样例匹配的可读转写。",
                            providerAttempted)
                        : await FinishAsync(row, profile, probe, request.Capability,
                            AiConnectionTestStatus.Failed, null, "asr_probe_mismatch",
                            "服务商已响应，但转写未包含内置语音样例的可核对文字；本次不计为通过，请核对ASR预设、模型、语言与账号权限。",
                            providerAttempted);
                }

                using var client = clients.Create(new AiModelConnection(profile.BaseUrl, profile.Model, key,
                    profile.TimeoutSeconds, ChatTestOutputTokens));
                var message = isVision
                    ? new ChatMessage(ChatRole.User,
                    [
                        new TextContent("Reply with only OK. This is a connection-test image."),
                        new DataContent(ConnectionTestMedia.Png, "image/png")
                    ])
                    : new ChatMessage(ChatRole.User, "Reply with only OK.");
                var response = await client.Client.GetResponseAsync([message],
                    new ChatOptions { MaxOutputTokens = ChatTestOutputTokens }, linked.Token);
                providerAttempted = wire.ProviderAttempted;
                row.ResponseText = Safe(response.Text, key, out var replyRedacted);
                var raw = response.RawRepresentation is OpenAI.Chat.ChatCompletion completion
                    ? System.ClientModel.Primitives.ModelReaderWriter.Write(completion).ToString()
                    : JsonSerializer.Serialize(new
                    {
                        response.ResponseId,
                        response.ModelId,
                        response.FinishReason,
                        response.Messages,
                        response.Usage
                    }, Json);
                row.ResponseRawJson = SafeJson(raw, key, out var rawRedacted);
                row.ContentRedacted = replyRedacted || rawRedacted;
                if (wire.ResponseBody is { } responseBody)
                {
                    row.ResponseRawJson = SafeJson(ValidJson(responseBody), key, out var wireRedacted);
                    row.ContentRedacted = row.ContentRedacted == true || wireRedacted;
                }
                row.FinishReason = response.FinishReason?.Value;
                row.PromptTokens = Count(response.Usage?.InputTokenCount);
                row.CompletionTokens = Count(response.Usage?.OutputTokenCount);
                row.TotalTokens = Count(response.Usage?.TotalTokenCount);
                return string.IsNullOrWhiteSpace(row.ResponseText)
                    ? await FinishAsync(row, profile, probe, request.Capability,
                        AiConnectionTestStatus.Failed, null, "empty_response",
                        "服务商已响应，但没有返回可读文本；请核对模型能力、内容过滤和服务商状态。",
                        providerAttempted)
                    : await FinishAsync(row, profile, probe, request.Capability,
                        AiConnectionTestStatus.Succeeded, Preview(row.ResponseText), null,
                        isVision
                            ? "连接与最小图片请求可用，不代表真实图片识别质量；服务商已接受图片并返回响应。"
                            : "文本测试成功：服务商已返回最小真实响应。",
                        providerAttempted);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.TimedOut, null, "timeout",
                    $"测试超时：{profile.TimeoutSeconds} 秒内未收到服务商响应；结果未确认，服务商仍可能计费。",
                    providerAttempted || wire.ProviderAttempted);
            }
            catch (OperationCanceledException)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Cancelled, null, "cancelled",
                    "测试已取消；如果服务商已收到请求，仍可能产生费用。",
                    providerAttempted || wire.ProviderAttempted);
            }
            catch (ClientResultException error)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Failed, null, HttpErrorCode(error.Status),
                    HttpErrorMessage(error.Status), providerAttempted: true);
            }
            catch (AiAsrProviderHttpException error)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Failed, null, HttpErrorCode(error.StatusCode),
                    HttpErrorMessage(error.StatusCode), providerAttempted: true);
            }
            catch (DomainException error) when (error.ErrorCode is 50401 or 50402)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.TimedOut, null, "timeout", error.Message,
                    providerAttempted || wire.ProviderAttempted, key);
            }
            catch (DomainException error) when (error.ErrorCode == 40001)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Failed, null, "asr_configuration_invalid", error.Message,
                    providerAttempted: false, secret: key);
            }
            catch (AiProviderCircuitOpenException error)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Blocked, null, "circuit_open", error.Message,
                    providerAttempted: false, secret: key);
            }
            catch (HttpRequestException)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Failed, null, "connection_failed",
                    "无法连接服务商；请核对API地址、TLS/网络、模型标识和访问权限。",
                    providerAttempted || wire.ProviderAttempted);
            }
            catch (Exception)
            {
                return await FinishAsync(row, profile, probe, request.Capability,
                    AiConnectionTestStatus.Failed, null, "invalid_response",
                    "服务商响应无法解析或测试请求未被接受；请核对协议、模型能力和API根地址。",
                    providerAttempted || wire.ProviderAttempted);
            }
        }
        finally
        {
            gate.Exit(vision: isVision, asr: isAsr);
            if (usageLease is not null)
            {
                try { await usageLimits!.EndAsync(usageLease, CancellationToken.None); }
                catch { /* The durable lease TTL still expires. */ }
            }
        }
    }

    private static (string? Error, bool CapabilityMismatch) Preflight(AiModelProfileEntity profile,
        bool runtimeEnabled, AiModelCapability capability)
    {
        if (!runtimeEnabled)
            return ("SDK直连尚未启用，请先在模型设置中启用", false);
        if (!profile.Enabled)
            return ("模型配置已停用，请先启用后再测试", false);
        if (!AiModelCapabilityFlags.Has(profile.Capabilities, capability))
            return ($"当前配置未声明“{CapabilityName(capability)}”能力，未发送服务商请求，也未产生测试费用。", true);
        if (profile.AsrPresetId is not null && profile.Capabilities != AiModelCapabilityFlags.Transcription)
            return ("ASR配置不得与文字/视觉能力混用，请另建独立配置", false);
        if (capability == AiModelCapability.Transcription &&
            profile.Capabilities != AiModelCapabilityFlags.Transcription)
            return ("语音转写测试必须使用独立的 ASR 配置，不能与文字/视觉能力混用", false);
        if (capability != AiModelCapability.Transcription &&
            AiModelCapabilityFlags.Has(profile.Capabilities, AiModelCapability.Transcription))
            return ("语音转写配置不能用于文字或视觉测试", false);
        if (capability == AiModelCapability.Transcription && string.IsNullOrWhiteSpace(profile.AsrPresetId))
            return ("ASR配置缺少接入预设，请编辑并重新保存后再测试", false);
        return (null, false);
    }

    private async Task<AiConnectionTestResultDto> FinishAsync(AiRequestLogEntity row,
        AiModelProfileEntity profile, string probe, AiModelCapability capability,
        AiConnectionTestStatus status, string? preview, string? errorCode, string? errorMessage,
        bool providerAttempted, string? secret = null)
    {
        row.Status = status switch
        {
            AiConnectionTestStatus.Succeeded => "succeeded",
            AiConnectionTestStatus.TimedOut => "timed_out",
            AiConnectionTestStatus.Cancelled => "cancelled",
            AiConnectionTestStatus.Blocked or AiConnectionTestStatus.CapabilityMismatch => "blocked",
            _ => "failed"
        };
        row.ErrorCode = status == AiConnectionTestStatus.Succeeded ? null : errorCode;
        if (status == AiConnectionTestStatus.Succeeded)
            row.ErrorMessage = null;
        else
        {
            var redacted = AiRedactor.RedactPlainText(errorMessage);
            row.ErrorMessage = string.IsNullOrEmpty(secret) ? redacted : Safe(redacted, secret, out _);
        }
        row.FinishedAt = DateTimeOffset.UtcNow;
        row.DurationMs = Math.Max(0, (long)(row.FinishedAt.Value - row.StartedAt).TotalMilliseconds);
        row.OutputChars = row.ResponseText?.Length ?? 0;
        row.OutputHash = Hash(row.ResponseText ?? row.ResponseRawJson);
        if (row.ResponseRawJson.Length > 512 * 1024)
            row.ResponseRawJson = JsonSerializer.Serialize(new
            {
                truncated = true,
                note = "connection-test response retained only as preview"
            }, Json);
        row.MetadataJson = JsonSerializer.Serialize(new
        {
            probe,
            capability = capability.ToString(),
            providerAttempted,
            method = "saved-model-connection-test",
            responsePreviewChars = preview?.Length ?? 0
        }, Json);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await db.SaveChangesAsync(budget.Token);
        }
        catch (Exception error) when (error is DbUpdateException or OperationCanceledException)
        {
            throw new DomainException(50301, "测试已受理但结果记录未能确认保存；请先核对记录，不要自动重复调用");
        }
        return new AiConnectionTestResultDto(row.ClientRequestId!.Value, profile.Id, profile.Revision,
            capability, status, probe, preview, row.ErrorCode, row.ErrorMessage, row.Id,
            row.DurationMs ?? 0, false, row.FinishedAt.Value, providerAttempted);
    }

    private AiConnectionTestResultDto Replay(AiRequestLogEntity row, string fingerprint)
    {
        if (row.Purpose != PurposeKey || row.InputHash != fingerprint)
            throw new DomainException(40901, "同一测试请求标识已绑定另一份模型或能力，原记录保留");
        var providerAttempted = false;
        var probe = "text-minimal";
        var capability = AiModelCapability.Text;
        try
        {
            using var metadata = JsonDocument.Parse(row.MetadataJson);
            if (metadata.RootElement.TryGetProperty("providerAttempted", out var attempted))
                providerAttempted = attempted.ValueKind == JsonValueKind.True;
            if (metadata.RootElement.TryGetProperty("probe", out var value) && value.ValueKind == JsonValueKind.String)
                probe = value.GetString() ?? probe;
            if (metadata.RootElement.TryGetProperty("capability", out var capabilityValue) &&
                capabilityValue.ValueKind == JsonValueKind.String &&
                Enum.TryParse<AiModelCapability>(capabilityValue.GetString(), out var parsed) &&
                Enum.IsDefined(parsed))
                capability = parsed;
        }
        catch (JsonException) { }
        var status = StatusOf(row.Status, row.ErrorCode);
        return new AiConnectionTestResultDto(row.ClientRequestId!.Value, row.ModelProfileId!.Value,
            row.ModelRevision ?? Guid.Empty, capability, status, probe, Preview(row.ResponseText),
            row.ErrorCode, row.ErrorMessage, row.Id, row.DurationMs ?? 0, true,
            row.FinishedAt ?? row.StartedAt, providerAttempted);
    }

    public async Task<AiConnectionTestResultDto?> GetLatestAsync(Guid modelId, Guid revision,
        AiModelCapability capability, CancellationToken ct)
    {
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var rows = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.Purpose == PurposeKey && x.ModelProfileId == modelId &&
                x.ModelRevision == revision && x.ClientRequestId != null)
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Take(64).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (Metadata(row.MetadataJson).Capability != capability) continue;
            return ToResult(row);
        }
        return null;
    }

    public async Task<IReadOnlyList<AiConnectionTestSummaryDto>> ListLatestAsync(CancellationToken ct)
    {
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var rows = await db.AiRequestLogs.AsNoTracking()
            .Where(x => x.Purpose == PurposeKey && x.ModelProfileId != null &&
                x.ModelRevision != null && x.ClientRequestId != null)
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Take(2048)
            .Select(x => new
            {
                x.Id, x.ModelProfileId, x.ModelRevision, x.Status, x.ErrorCode,
                x.StartedAt, x.FinishedAt, x.DurationMs, x.MetadataJson, x.ClientRequestId
            })
            .ToListAsync(ct);
        var seen = new HashSet<(Guid, AiModelCapability)>();
        var result = new List<AiConnectionTestSummaryDto>();
        foreach (var row in rows)
        {
            var metadata = Metadata(row.MetadataJson);
            if (!Enum.IsDefined(metadata.Capability)) continue;
            if (!seen.Add((row.ModelProfileId!.Value, metadata.Capability))) continue;
            result.Add(new(row.ModelProfileId.Value, row.ModelRevision!.Value, metadata.Capability,
                StatusOf(row.Status, row.ErrorCode), row.FinishedAt ?? row.StartedAt,
                metadata.ProviderAttempted, row.Id));
        }
        return result;
    }

    private static AiConnectionTestResultDto ToResult(AiRequestLogEntity row)
    {
        var metadata = Metadata(row.MetadataJson);
        return new(row.ClientRequestId!.Value, row.ModelProfileId!.Value,
            row.ModelRevision ?? Guid.Empty, metadata.Capability, StatusOf(row.Status, row.ErrorCode),
            metadata.Probe, Preview(row.ResponseText), row.ErrorCode, row.ErrorMessage, row.Id,
            row.DurationMs ?? 0, true, row.FinishedAt ?? row.StartedAt, metadata.ProviderAttempted);
    }

    private static (AiModelCapability Capability, string Probe, bool ProviderAttempted) Metadata(string json)
    {
        var capability = AiModelCapability.Text;
        var probe = "text-minimal";
        var attempted = false;
        try
        {
            using var metadata = JsonDocument.Parse(json);
            if (metadata.RootElement.TryGetProperty("capability", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Enum.TryParse<AiModelCapability>(value.GetString(), out var parsed) &&
                Enum.IsDefined(parsed))
                capability = parsed;
            if (metadata.RootElement.TryGetProperty("probe", out var probeValue) &&
                probeValue.ValueKind == JsonValueKind.String)
                probe = probeValue.GetString() ?? probe;
            if (metadata.RootElement.TryGetProperty("providerAttempted", out var attemptedValue))
                attempted = attemptedValue.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { }
        return (capability, probe, attempted);
    }

    private static AiConnectionTestStatus StatusOf(string status, string? errorCode) => status switch
    {
        "succeeded" => AiConnectionTestStatus.Succeeded,
        "timed_out" => AiConnectionTestStatus.TimedOut,
        "blocked" when errorCode == "capability_mismatch" => AiConnectionTestStatus.CapabilityMismatch,
        "blocked" => AiConnectionTestStatus.Blocked,
        "running" => AiConnectionTestStatus.Running,
        "cancelled" => AiConnectionTestStatus.Cancelled,
        "incomplete" => AiConnectionTestStatus.Unknown,
        _ => AiConnectionTestStatus.Failed
    };

    private Task<AiRequestLogEntity?> FindAsync(Guid owner, Guid requestId, CancellationToken ct) =>
        db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == owner &&
            x.ClientRequestId == requestId, ct);

    private static string ProbeName(AiModelCapability capability) => capability switch
    {
        AiModelCapability.Text => "text-minimal",
        AiModelCapability.Vision => "vision-32x32-png",
        AiModelCapability.Transcription => "asr-speech-mp3",
        _ => "unknown"
    };

    private static string CapabilityName(AiModelCapability capability) => capability switch
    {
        AiModelCapability.Text => "文本",
        AiModelCapability.Vision => "视觉",
        AiModelCapability.Transcription => "语音转写",
        _ => "未知"
    };

    private static string RequestSnapshot(AiModelCapability capability) => capability switch
    {
        AiModelCapability.Text => JsonSerializer.Serialize(new[]
        {
            new { role = "user", content = "Reply with only OK." }
        }, Json),
        AiModelCapability.Vision => JsonSerializer.Serialize(new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "Reply with only OK. This is a connection-test image." },
                    new { type = "image_url", image_url = new { url = "pim-connection-test:red-32x32-png" } }
                }
            }
        }, Json),
        AiModelCapability.Transcription => JsonSerializer.Serialize(new[]
        {
            new
            {
                kind = "connection-test-audio",
                name = "connection-test.mp3",
                format = "mp3",
                sha256 = ConnectionTestMedia.AudioSha256,
                bytes = ConnectionTestMedia.Audio.Length
            }
        }, Json),
        _ => "[]"
    };

    private static bool TranscriptMatches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.ToLowerInvariant();
        return (normalized.Contains("connect", StringComparison.Ordinal) &&
                    normalized.Contains("test", StringComparison.Ordinal)) ||
                (normalized.Contains("连接", StringComparison.Ordinal) &&
                    normalized.Contains("测试", StringComparison.Ordinal));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Preview(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= PreviewChars ? trimmed : trimmed[..PreviewChars];
    }

    private static string HttpErrorCode(int status) => status switch
    {
        401 or 403 => "auth_failed",
        404 => "model_not_found",
        429 => "rate_limited",
        400 or 422 => "provider_rejected",
        >= 500 and <= 599 => "provider_unavailable",
        _ => "provider_http_error"
    };

    private static string HttpErrorMessage(int status) => status switch
    {
        401 or 403 => $"服务商鉴权失败（HTTP {status}），请核对 API Key 与模型权限。",
        404 => "服务商未找到模型或接口（HTTP 404），请核对模型标识与 API 根地址。",
        429 => "服务商限流（HTTP 429），请稍后明确重试。",
        400 or 422 => $"服务商拒绝了测试请求（HTTP {status}），请核对模型能力、地址与协议。",
        >= 500 and <= 599 => $"服务商暂时不可用（HTTP {status}），请稍后明确重试。",
        _ => $"服务商返回 HTTP {status}。"
    };

    private static DomainException Invalid(string message) => new(40001, message);

    internal static class ConnectionTestMedia
    {
        // 32x32 opaque red PNG; a synthetic fixture, never user material.
        internal static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR42mO4o6FBU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAIahsD0l4r5QAAAAAElFTkSuQmCC");
        // 1.3s synthetic English speech ("Connection test.") generated once with FFmpeg's
        // libflite source; the expected words let a 2xx response with empty/unrelated text fail.
        internal static readonly byte[] Audio = Convert.FromBase64String(
            "SUQzBAAAAAAAIlRTU0UAAAAOAAADTGF2ZjYyLjMuMTAwAAAAAAAAAAAAAAD/4zjAAAAAAAAAAAAASW5mbwAAAA8AAAAVAAAMqAAcHBwcJycnJyczMzMzMz4+Pj4+SUlJSVVVVVVVYGBgYGBsbGxsbHd3d3eCgoKCgo6Ojo6OmZmZmZmkpKSksLCwsLC7u7u7u8fHx8fH0tLS0t3d3d3d6enp6en09PT09P////8AAAAATGF2YzYyLjExAAAAAAAAAAAAAAAAJARRAAAAAAAADKilnanpAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/4yjEABaowjhcYYZkCWFEwAvXr17QCAo6dK4jvLFixYsuvXnZPaCI8YTJ3d3aIgiv2GAABxQYlC59+EQ/KYJ5PbvlBPLn/63zgObJ/5PBCoMVg/n4nPwx/6z59RwPoynBDuEGUDHggoMKqViDJgBpkjZM5RI7Dm2+yijgtnEHkfnhRUcfBtRduZRw9S62C5v/4yjEKBnhBkQCelBk1u9RcPX3QoOW87Ab6T2+j6Mn9qlh/O1vQXfZaLzhfFcmWvQBTlLVJvL0KmRj4qNCrkuRcwXWcIlnVWosYt3xaotVAAAQZewRgoIld3APz1dE+bm4pOv1516tp7BLlP5/P9e/Nfft+X//35O955f+T8GUifl5i/9Hrt9adK7N5NtGvQn/4yjEQxS7jlg2SES8vNrgqHGF0E7qnU6cT9MhNQiTABebU0xkDQECJnBgH/jQHPLuxL1IEUahA75wbsoY3m+/v5zfHe6dGHq+v2/9H8SnvL6W/0Zvftf0/b1/+p7ki1Kna2v01aKspTo3q25BY9SR8VB1YqfWAx4+CAQurT2pkQWWrXZbqAAO74tn4MI4eJv/4yjEcxjDokw0egokZWvRW/flvUR9a9H+z/6v//td/Zo+GMNZx2B2mvkiFiq02m0Wm2Wq1Wu2ubO7Lebj8aQJR+TMsaGlzj3P8zh5Ow0Sdl7cTnQtoV8cXQFiTgt51kkivzQRj8uCo0HsICPBsJSeIRiEo5HkdWROq4ERRv1UpUgIUcbUpowkiEMpfH8yw5b/4yjEkwvINoG/QhAAzrj7zDjqxrZyXEGfsU6EszSJiXS7yBXLbtQLttmv1BW+4Gt/jhRSGm69TiKV8O0+E4jDugZcNQKd48v/mHH370pp5EwzoY0LK6Zl95V4u4GmlwZJIDZe7pD2KaXWqUp/73373/8TTLaE233itNw4skXDjM6h1iOlqR5TEOzBAT8mX17/4yjE5jlzwrZZj3gAvc7/I6uyCItVjsoHDr3W841FHYbdKDLsv5Jo27LXn6dq+8TlR+HezdFRx+TNKa899+A4rKotYpb9bvj6g6qul+Wymok326bgq8o6at6iGEERRaBKHuJAVPRzyPgUgt6d0iOpOmVnS/qtri+EZavhUS1gsVaupNMs5pFkEZotDiaXNYv/4yjEgygrVqwB2EAAMgaMSBFbGC4462QfSXNRNcmqPCVlwiY0Qlq+6ailoAIDREACkw03K9OA7h3u389pQ4QYBB0fHwpWEcouxkND17t4dQmhLGBWTKSeRxG+LmTsb7CL4TM4hcJEgSknydLmMyRKFuLiYRwivnnRUrqRSQbj1JSdKZfuUHR/ExSUictMear/4yjEZS7UDrYseg0YLOWLHHioqDYwkRhoNw/MBXR6puMlJREVGTTh50iEl5ffieJ+Y9+9/u+YmFSpIIq3hJZeZvpq65tW+a7m/eaauo6u4qTLBE3rl4T614S6Bbss0LOMJZrVgAABoD8AcWKBBypLn807ruaGnA1S3jGYUFJI7E8KjWuEEaNWVyViUNcAcjr/4yjELCWTzr41T0ABVarWVhZUUXKNum6MylBcLXExa81xdCxwsq6jBZrZVpmv/iVX+dVr+edVWmGhyDY5iha4uNfqGa/X2b/9deWaV///+G4Zv4b/1qGba1Wvbv/Xba/X+G//+ob4FjlFVVVFRVQoKSoTeBR3AVVT9071p62//MQTaRJz//wFp+72//3pnyb/4yjEGCJTuqgBhlgBVlYeR9aia///vfT30+DVF0tc3///PrJnyYqbjoOPNYuuWUVmn///4BAJhw+bvZVmhIJgIIAJk21KaoamD/////HWuwdZvde+GPs1JxOXeP5MIg2F6Gj//////7//hjD7733yiUXNJSSgHhaRx7P6hYMV/5uBu12TKXbH1n8q2yB99Az/4yjEER48AsDLwTgBzvMZKf/////+n2p63rf/8xv26vbdlY09ENNUmeWOGRoNxEOGoyD8VkwcEhaJcfHwfnE43MLIIg1F7muOx80gxYkaNyxQaiWLDCYPBqKCxQipzDLqcqCcslzV0ONLmnOYcYehpcC7g/b/+IOW762dEtHNb0KgsENpO7YvqlC9iH047gr/4yjEGxuZusjKYEbqDED4cEUtkgnn44IuOzM7ffs2/P3/gSAZzet4KWkBL4O7ICEkR3cOLMgRJEAEjBUMC/dq/j+OChE3PiIKLDqDqzq5Iq/brbqqg1YKblKPJfJLU8TVzSRCtaqLwAEHc7DtXmAeV7RVYpQ1TNJGc4LtUwZtPH7c9UqppHorp/Bexf/WCyv/4yjELxzRmspfTxgA7dt2tb+Nsx6r0suMGVYxw5/6qv6l7M2wYCAssdTniJ46HYlOljySXgrnSx4Sg1EXLA0/XEIaxKeBUFXYiPfGnfjzv4iV7eyuSaX3pTD44BGhfeVrmFqWU7voD+KZlv8+YfqMRCcXIICA7D2IIIKyEKSJoVIH1YqyyF1YfIMw+3mhxFH/4yjEPixDuqABmJgBUpJAQjEpk6K0FaDsImOv63/TWyBbTQK5cPF8yMDIZcXOM32v2zEiZfIoVEj6lrQjmk2TpHF1JVFM3N//+mgovzcwWospGxMFwuIE+Zl80Jw0JN1kUMrf/+ZksTZPmKajI8p6DqZApkOKxec0ZqZmy3nljef//qH+d3jjALEL3N2dM+v/4yjEEB1h4sgB2EAAGXNbrtn3ha++vN5Ku/zd8WM6uqamnnReac+zjVkEv+8YDdPrErffj6n6R6/afjWh/1KhwUpUlHAQJFqJoWWaWqMsjFkUuc85YzScjwxaHHoEDVCU1AQWtCTvVzuzLd1oVSgKP9SFwgAcj9YBdwpAmKWf/1RSS5K9jS2qxih3/qhUS5H/4yjEHRwTAs20SMTMpE2FTMpdcBE+cYyFDwMKczcNKh/AqwlwzPS2Zrm3RpNC72DAUxtH0Qxj5grOhUt9U+R/Xzf7Gf0N6lL6sWuDOUkyiZRr5RjaPWp0CAMVcXjbgtpVKdlkdbcIUf5yZ1VAGXsTdKkWEulGyjcJpZVI0KGGeo5PP0pZHyvMl4dJWOlPOxT/4yjELxzKArpZSRgAKEK0ssfNeUqRsyw/lzI5ciQ8s+Vih6ktgOyp6BEDgUiNxgbFg3YK/9TRVYoLonr07qp4eKoNRC9RkQAY5YCZJUeBHNcqAZwaERiUYcCIjadOzT8m7vRI4sPC2qo01CzAIYmnyed3M6ZPFVq8vpNy4WzFrBbv//y/H6mBjGigmJFzPsv/4yjEPiy7epW5jHgAdGfbx8Y1/+WwgCMOhjVRkTkgSr6t7WtChW+cfWP/+uT0RjJnafUyw2qZYvFaqvXuYuK6hf/////zXZ0+uIrin2xCnScN48LbgwYsK0sWDMxVRX//////8gcBWEvTquWNY1OyP2OR4cb05tMUb1x/8/Oa1bVbK+AAGh1sbDhKgTSJUyn/4yjEDhHwUmwXyRgA1YNyYcqPBUaIg6HSQsik9trlt8KuQsk2CxoOuCtyNFv03//1akYa3s5iU0LTsuv6KEwlW4c0RbFqsW2wAqrFYIhSwZGR9iU9FEqPCSgIKeZnmkdgqGqnnjomDvwaqiUFXVP8tK79Q//+DQcwaiWS5bLflXf5Ys8O2+JRh7/rOyv53ln/4yjESRIwckAUYYwgTEFNRVVVVUxBTUUzLjEwMSAoYmV0YSAzKVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVX/4yjEfAAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVU=");
        internal static readonly string AudioSha256 =
            Convert.ToHexString(SHA256.HashData(Audio)).ToLowerInvariant();
    }
}
