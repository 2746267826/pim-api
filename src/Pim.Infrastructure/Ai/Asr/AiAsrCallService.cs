using System.ClientModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using static Pim.Infrastructure.Ai.AiCallAudit;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class AiAsrCallService(PimDbContext db, ISecretProtector secrets, ICurrentUserService currentUser,
    AiTextCallGate gate, AiAsrPresetRegistry presets, AiAsrMaterialService materials)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<AiTextCallResultDto> CallAsync(AiAsrCallRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "会话已改变，音频未发送，请重新核对账号");
        if (request.ClientRequestId == Guid.Empty || request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请核对本次音频、ASR预设、模型和目标地址，并明确同意发送");
        var audio = AiAsrMaterialService.Decode(request.Audio);
        var fingerprint = Hash(JsonSerializer.Serialize(new { kind = "asr", request.ModelId, request.Language, request.Consent,
            audio.Name, audio.Format, audio.Sha256, size = audio.Bytes.Length }, Json));
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(owner, request.ClientRequestId, ct);
        if (old is not null) return Replay(old, fingerprint);
        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var modelId = request.ModelId ?? runtime?.TranscriptionModelId;
        var profile = modelId is { } id ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var preset = presets.Resolve(profile?.AsrPresetId); var description = preset.Descriptor;
        if (profile is null) throw new DomainException(40001, "请选择带有预设的ASR模型");
        preset.ValidateModel(profile.Model);
        var endpoint = preset.NormalizeEndpoint(profile.BaseUrl);
        var operation = AiAsrPresetRegistry.OperationEndpoint(endpoint, description);
        if (request.Consent.ModelId != profile.Id || request.Consent.ModelRevision != profile.Revision ||
            request.Consent.PresetId != description.Id || request.Consent.PresetVersion != description.Version ||
            request.Consent.Endpoint != operation)
            throw new DomainException(40901, "ASR预设、模型或目标地址已变化，音频未发送，请刷新并重新核对");
        if (request.Language is null || !description.Languages.Contains(request.Language) ||
            !description.Formats.Contains(audio.Format) || audio.Bytes.Length > description.MaxAudioBytes)
            throw new DomainException(40001, "语言、音频格式或大小不符合所选ASR预设");
        string? key = null; string? configurationError = null;
        if (runtime?.Enabled != true || !profile.Enabled || profile.Capabilities != 4)
            configurationError = "SDK未启用，或该配置不是已启用的独立ASR模型";
        else
        {
            try { key = secrets.Unprotect(profile.ApiKeySecret); if (string.IsNullOrEmpty(key)) configurationError = "ASR凭据不可用，请重新保存"; }
            catch (Exception error) when (error is CryptographicException or FormatException) { configurationError = "ASR凭据无法读取，请重新保存"; }
        }
        var now = DateTimeOffset.UtcNow;
        var row = new AiRequestLogEntity { UserId = owner, ClientRequestId = request.ClientRequestId, InputHash = fingerprint,
            Module = "system", Purpose = "ai.asr-call", SourceObjectType = "ai-model", SourceObjectId = profile.Id.ToString(),
            Provider = description.Id, Model = profile.Model, ModelProfileId = profile.Id, ModelProfileName = profile.Name,
            ModelRevision = profile.Revision, ModelEndpoint = operation, CorrelationId = Guid.NewGuid().ToString("N"),
            Status = configurationError is null ? "running" : "blocked", StartedAt = now, FinishedAt = configurationError is null ? null : now,
            ExecutionExpiresAt = now.AddSeconds(profile.TimeoutSeconds + 60), AttemptNumber = 1, MaxAttempts = 1,
            ErrorCode = configurationError is null ? null : "configuration_unavailable", ErrorMessage = configurationError };
        var storedAudio = AiAsrMaterialService.Describe(row.Id, audio);
        row.RequestMessagesJson = JsonSerializer.Serialize(new[] { new { kind = "audio-input", reference = $"pim-ai-audio:{storedAudio.Id:D}",
            audio.Format, audio.Sha256, sizeBytes = audio.Bytes.Length, language = request.Language } }, Json);
        row.RequestPayloadJson = JsonSerializer.Serialize(new { recordFormat = "sdk-asr-v1", presetId = description.Id, presetVersion = description.Version,
            model = profile.Model, endpoint = operation, language = request.Language, stream = false }, Json);
        row.MetadataJson = Metadata(configurationError is null ? null : false, false, false, null, false);
        db.AiRequestLogs.Add(row); db.AiCallAudio.Add(storedAudio);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached; db.Entry(storedAudio).State = EntityState.Detached;
            var winner = await FindAsync(owner, request.ClientRequestId, ct); if (winner is null) throw;
            return Replay(winner, fingerprint);
        }
        if (configurationError is not null) return Result(row, false);
        if (!gate.TryEnter(asr: true))
        { row.Status = "blocked"; row.ErrorCode = "busy"; row.ErrorMessage = "ASR并发已达上限，请稍后明确新建调用"; row.MetadataJson = Metadata(false, false, false, null, false); return await FinishAsync(row); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        using var wire = new AiTextWireCapture(); var prepared = false; double? reportedSeconds = null;
        try
        {
            await materials.StoreAsync(owner, storedAudio, audio, linked.Token); prepared = true;
            var connection = new AiModelConnection(endpoint, profile.Model, key!, profile.TimeoutSeconds);
            wire.SetAsrAudit(bytes => preset.AuditRequest(bytes, connection, audio, storedAudio.Id, request.Language));
            var response = await preset.TranscribeAsync(connection, audio, request.Language, linked.Token);
            row.ResponseText = Safe(response.Text, key, out var redacted);
            row.ResponseRawJson = SafeJson(ValidJson(response.RawJson), key, out var rawRedacted);
            row.ContentRedacted = redacted || rawRedacted; row.FinishReason = response.FinishReason;
            row.PromptTokens = Count(response.InputTokens); row.CompletionTokens = Count(response.OutputTokens); row.TotalTokens = Count(response.TotalTokens);
            reportedSeconds = response.ReportedSeconds;
            if (response.FinishReason == "stop" && response.HasTranscript) row.Status = "succeeded";
            else { row.Status = "incomplete"; row.ErrorCode = "incomplete_output"; row.ErrorMessage = "ASR未正常完成或缺少转写字段，请核对回复与结束原因"; }
        }
        catch (OperationCanceledException)
        { row.Status = ct.IsCancellationRequested ? "cancelled" : "timed_out"; row.ErrorCode = row.Status;
            row.ErrorMessage = prepared ? "ASR已取消或超时，上游结果未确认，请先核对记录" : "音频保存已取消或超时，本次未发送模型"; }
        catch (ClientResultException error)
        { row.Status = "failed"; row.ErrorCode = "upstream_http_" + error.Status; row.ErrorMessage = "ASR服务返回HTTP " + error.Status + "，本次未自动重试"; }
        catch (Exception)
        { row.Status = "failed"; row.ErrorCode = prepared ? "provider_error" : "audio_prepare_failed";
            row.ErrorMessage = prepared ? "ASR连接或响应异常，请核对记录；本次未自动重试" : "音频未能保存，本次未发送模型，请检查文件来源"; }
        finally
        {
            try
            {
                if (wire.RequestBody is { } sent)
                {
                    row.RequestPayloadJson = SafeJson(sent, key, out var redacted);
                    using var json = JsonDocument.Parse(sent); var messageRedacted = false;
                    if (json.RootElement.TryGetProperty("messages", out var messages)) row.RequestMessagesJson = SafeJson(messages.GetRawText(), key, out messageRedacted);
                    row.ContentRedacted = row.ContentRedacted == true || redacted || messageRedacted;
                }
                if (wire.ResponseBody is { } received)
                { row.ResponseRawJson = SafeJson(ValidJson(received), key, out var redacted); row.ContentRedacted = row.ContentRedacted == true || redacted; }
                if (wire.RequestRejected) { row.Status = "failed"; row.ErrorCode = "asr_request_mismatch"; row.ErrorMessage = "SDK请求与固定音频/模型/语言不一致，本次未发送"; }
                if (wire.ResponseTooLarge) { row.Status = "failed"; row.ErrorCode = "response_too_large"; row.ErrorMessage = "ASR响应超过4MiB，未保存为完整结果"; }
                row.MetadataJson = Metadata(wire.ProviderAttempted, wire.RequestBody is not null, wire.ResponseBody is not null, reportedSeconds, wire.ResponseTooLarge);
            }
            finally { gate.Exit(asr: true); }
        }
        return await FinishAsync(row);

        string Metadata(bool? attempted, bool requestCaptured, bool responseComplete, double? seconds, bool tooLarge) => JsonSerializer.Serialize(new {
            recordFormat = "sdk-asr-v1", asrPresetId = description.Id, asrPresetVersion = description.Version, language = request.Language,
            audioEncoding = "sha256-reference", audioStored = storedAudio.ObjectKey != null, providerAttempted = attempted,
            requestBodyCaptured = requestCaptured, responseBodyComplete = responseComplete, reportedAudioSeconds = seconds,
            responseTooLarge = tooLarge, transportRetries = 0, destinationConsent = request.Consent }, Json);
    }
    public async Task<AiTextCallResultDto> GetAsync(Guid requestId, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录"); await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(owner, requestId, ct);
        if (row is null || row.Purpose != "ai.asr-call") throw new DomainException(40401, "尚未找到本次ASR记录");
        return Result(row, true);
    }
    private Task<AiRequestLogEntity?> FindAsync(Guid owner, Guid id, CancellationToken ct) => db.AiRequestLogs.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UserId == owner && x.ClientRequestId == id, ct);
    private static AiTextCallResultDto Replay(AiRequestLogEntity row, string fingerprint)
    { if (row.InputHash != fingerprint || row.Purpose != "ai.asr-call") throw new DomainException(40901, "请求标识已绑定另一份输入，原记录保留"); return Result(row, true); }
    private async Task<AiTextCallResultDto> FinishAsync(AiRequestLogEntity row)
    {
        row.FinishedAt = DateTimeOffset.UtcNow; row.DurationMs = (long)(row.FinishedAt.Value - row.StartedAt).TotalMilliseconds;
        row.OutputChars = row.ResponseText?.Length ?? 0; row.OutputHash = Hash(row.ResponseText ?? row.ResponseRawJson);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(budget.Token); }
        catch (Exception error) when (error is DbUpdateException or OperationCanceledException)
        { throw new DomainException(50301, "ASR结果记录未能确认保存；请先核对记录，不要自动重发"); }
        return Result(row, false);
    }
}
