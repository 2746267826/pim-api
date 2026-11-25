using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using static Pim.Infrastructure.Ai.AiCallAudit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace Pim.Infrastructure.Ai;

public sealed class AiTextCallService(PimDbContext db, ISecretProtector secrets, IAiProfileChatClientFactory clients,
    ICurrentUserService currentUser, AiTextCallGate gate, AiVisionMaterialService? visionMaterials = null,
    AiUsageLimitService? usageLimits = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int MaxAudioContextBytes = 8 * 1024 * 1024;
    private sealed record OwnerCallScope(string SourceObjectType, string SourceObjectId, string Purpose);

    public async Task<AiTextCallResultDto> GetAsync(Guid clientRequestId, CancellationToken ct)
    {
        var user = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(user, clientRequestId, ct) ?? throw new DomainException(40401, "尚未找到本次调用记录");
        return Result(row, true);
    }

    public Task<AiTextCallResultDto> CallAsync(AiTextCallRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        return CallCoreAsync(owner, request, null, null, null, null, ct);
    }

    public Task<AiTextCallResultDto> CallVisionAsync(AiVisionCallRequest request, CancellationToken ct)
    {
        if (request.ExpectedUserId != currentUser.UserId) throw new DomainException(40301, "会话已改变，图片未发送；请重新核对账号");
        if (request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请先核对本次图片、模型与目标地址，并明确同意发送图片");
        return CallCoreAsync(currentUser.UserId!.Value,
            new(request.ClientRequestId, request.ExpectedUserId, request.Prompt, request.SystemPrompt, request.ModelId),
            AiVisionMaterialService.Decode(request), null, request.Consent, null, ct);
    }

    // Background callers must supply the durable owner from their persisted task. This never impersonates an HTTP user.
    internal async Task<AiTextCallResultDto> CallForOwnerAsync(Guid owner, AiTextCallRequest request,
        AiVisionDestinationConsent destination, string sourceObjectType, string sourceObjectId, string purpose,
        CancellationToken ct)
    {
        var scope = RequireOwnerCallScope(sourceObjectType, sourceObjectId, purpose);
        await RequireActiveOwnerAsync(owner, ct);
        return await CallCoreAsync(owner, request, null, null, destination, scope, ct);
    }

    /// <summary>
    /// Generation-model call with the exact raw audio slices admitted by the note task. The
    /// caller must have already fixed owner, note/version, recording, file and hash identity.
    /// </summary>
    internal async Task<AiTextCallResultDto> CallWithAudioForOwnerAsync(Guid owner, AiTextCallRequest request,
        IReadOnlyList<AiAudioContextInput> audio, AiVisionDestinationConsent destination,
        string sourceObjectType, string sourceObjectId, string purpose, CancellationToken ct)
    {
        var scope = RequireOwnerCallScope(sourceObjectType, sourceObjectId, purpose);
        await RequireActiveOwnerAsync(owner, ct);
        ValidateAudioContext(owner, audio);
        return await CallCoreAsync(owner, request, null, audio, destination, scope, ct);
    }

    internal async Task<AiTextCallResultDto> CallVisionForOwnerAsync(Guid owner, AiVisionCallRequest request,
        string sourceObjectType, string sourceObjectId, string purpose, CancellationToken ct)
    {
        var scope = RequireOwnerCallScope(sourceObjectType, sourceObjectId, purpose);
        await RequireActiveOwnerAsync(owner, ct);
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "会话已改变，图片未发送；请重新核对账号");
        if (request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请先核对本次图片、模型与目标地址，并明确同意发送图片");
        return await CallCoreAsync(owner,
            new(request.ClientRequestId, request.ExpectedUserId, request.Prompt, request.SystemPrompt, request.ModelId),
            AiVisionMaterialService.Decode(request), null, request.Consent, scope, ct);
    }

    private async Task<AiTextCallResultDto> CallCoreAsync(Guid userId, AiTextCallRequest request,
        IReadOnlyList<AiVisionInput>? images, IReadOnlyList<AiAudioContextInput>? audio,
        AiVisionDestinationConsent? consent, OwnerCallScope? scope,
        CancellationToken ct)
    {
        var vision = images is not null;
        var audioMode = audio is not null;
        var recordFormat = audioMode ? "sdk-audio-v1" : vision ? "sdk-vision-v1" : "sdk-text-v1";
        if (request.ExpectedUserId != userId) throw new DomainException(40301, "会话已改变，输入未发送；请重新核对账号后再调用");
        if (request.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Prompt) ||
            request.Prompt.Length + (request.SystemPrompt?.Length ?? 0) > 200_000 || request.SystemPrompt?.Length > 32_000)
            throw new DomainException(40001, "需要有效请求标识和正文；系统提示最多32000字符，总输入最多200000字符");
        var fingerprint = Fingerprint(request, images, audio, consent, scope);
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(userId, request.ClientRequestId, ct);
        if (old is not null) return Replay(old, fingerprint);

        var config = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var selected = request.ModelId ?? (vision ? config?.VisionModelId : config?.TextModelId);
        var profile = selected is { } id ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        // Consent is an assertion against server configuration, never a client-controlled routing override.
        // No file upload or model transport occurs if the displayed model/address/revision has changed.
        if ((vision || audioMode || scope is not null) && (consent?.Confirmed != true || profile is null || consent.ModelId != profile.Id ||
            consent.ModelRevision != profile.Revision || !string.Equals(consent.Endpoint, profile.BaseUrl, StringComparison.Ordinal)))
            throw new DomainException(40901, scope is null
                ? "模型或目标地址已变化；图片未发送，请刷新并重新核对同意"
                : "模型或目标地址已变化；材料未发送，请刷新并重新核对同意");
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new(ChatRole.System, request.SystemPrompt));
        messages.Add(new(ChatRole.User, request.Prompt));
        if (audioMode)
            foreach (var item in audio!)
                messages[^1].Contents.Add(new DataContent(item.Bytes, item.ContentType));
        var finalMessages = messages.Select(m => new { role = m.Role.Value, content = m.Text }).ToArray();
        string? key = null; string? configurationError = null;
        if (config?.Enabled != true) configurationError = "SDK直连尚未启用，请在模型设置中启用";
        else if (profile is null) configurationError = audioMode ? "未选择有效音频上下文模型"
            : vision ? "未选择有效视觉模型" : "未选择有效文本模型";
        else if (!profile.Enabled || (profile.Capabilities & (vision ? 2 : 1)) == 0) configurationError = "模型未启用或未声明对应能力";
        else if (vision && visionMaterials is null) configurationError = "图片材料存储尚不可用";
        else
        {
            try { key = secrets.Unprotect(profile.ApiKeySecret); if (string.IsNullOrEmpty(key)) configurationError = "模型凭据不可用，请重新保存"; }
            catch (Exception error) when (error is CryptographicException or FormatException) { configurationError = "模型凭据无法读取，请重新保存"; }
        }
        var capturedMessages = SafeJson(JsonSerializer.Serialize(finalMessages, Json), key, out var requestRedacted);
        var started = DateTimeOffset.UtcNow;
        var row = new AiRequestLogEntity
        {
            UserId = userId, ClientRequestId = request.ClientRequestId, InputHash = fingerprint,
            Module = scope is null ? "system" : "notes",
            Purpose = scope?.Purpose ?? (vision ? "ai.vision-call" : "ai.text-call"),
            SourceObjectType = scope?.SourceObjectType ?? "ai-model",
            SourceObjectId = scope?.SourceObjectId ?? selected?.ToString() ?? "default",
            Provider = "openai-compatible",
            Model = profile?.Model ?? "未选择", ModelProfileId = profile?.Id, ModelProfileName = profile?.Name,
            ProviderConnectionId = profile?.ProviderConnectionId,
            ModelRevision = profile?.Revision, ModelEndpoint = profile?.BaseUrl,
            CorrelationId = Guid.NewGuid().ToString("N"), Status = configurationError is null ? "running" : "blocked",
            AttemptNumber = 1, MaxAttempts = 1, StartedAt = started, InitiatedAt = started,
            ExecutionExpiresAt = started.AddSeconds((profile?.TimeoutSeconds ?? 60) + 60),
            RequestMessagesJson = capturedMessages, InputChars = request.Prompt.Length + (request.SystemPrompt?.Length ?? 0),
            RequestPayloadJson = JsonSerializer.Serialize(new { model = profile?.Model,
                maxOutputTokens = profile?.MaxOutputTokens, stream = false, recordFormat,
                audioContextSegments = audio?.Count }, Json),
            ContentRedacted = requestRedacted, ErrorMessage = configurationError,
            ErrorCode = configurationError is null ? null : "configuration_unavailable",
            FinishedAt = configurationError is null ? null : started,
            MetadataJson = JsonSerializer.Serialize(new { recordFormat, transportRetries = 0, requestBodyCaptured = false,
                providerAttempted = configurationError is not null ? false : (bool?)null,
                imageEncoding = vision ? "sha256-reference" : null,
                audioEncoding = audioMode ? "sha256-reference" : null,
                audioInputsReferenced = audio?.Count,
                audioInputsSha256 = audio?.Select(x => x.SourceSha256).ToArray(),
                audioContextOwnerId = audio?.FirstOrDefault()?.OwnerId,
                audioContextNoteId = audio?.FirstOrDefault()?.NoteId,
                audioContextSourceVersionId = audio?.FirstOrDefault()?.SourceVersionId,
                destinationConsent = consent }, Json)
        };
        var imageRows = vision ? AiVisionMaterialService.Describe(row.Id, images!) : [];
        if (vision)
        {
            var content = new List<object> { new { type = "text", text = request.Prompt } };
            content.AddRange(imageRows.Select(x => (object)new { type = "image_url", image_url = new { url = $"pim-ai-image:{x.Id:D}" } }));
            var snapshot = new List<object>();
            if (!string.IsNullOrEmpty(request.SystemPrompt)) snapshot.Add(new { role = "system", content = request.SystemPrompt });
            snapshot.Add(new { role = "user", content });
            row.RequestMessagesJson = SafeJson(JsonSerializer.Serialize(snapshot, Json), key, out var redacted);
            row.ContentRedacted = row.ContentRedacted == true || redacted;
            db.AiCallImages.AddRange(imageRows);
        }
        if (audioMode)
        {
            var auditKey = key;
            if (auditKey is null && profile is not null)
            {
                try { auditKey = secrets.Unprotect(profile.ApiKeySecret); }
                catch (Exception error) when (error is CryptographicException or FormatException) { }
            }
            var content = new List<object> { new { type = "text", text = request.Prompt } };
            content.AddRange(audio!.Select((_, index) => (object)new
            {
                type = "input_audio",
                input_audio = new { data = $"pim-ai-audio:{index + 1:D5}", format = "wav" }
            }));
            var snapshot = new List<object>();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
                snapshot.Add(new { role = "system", content = request.SystemPrompt });
            snapshot.Add(new { role = "user", content });
            row.RequestMessagesJson = SafeJson(JsonSerializer.Serialize(snapshot, Json), auditKey,
                out var audioRedacted);
            row.ContentRedacted = row.ContentRedacted == true || audioRedacted;
        }
        db.AiRequestLogs.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            foreach (var image in imageRows) db.Entry(image).State = EntityState.Detached;
            var winner = await FindAsync(userId, request.ClientRequestId, ct);
            if (winner is null) throw;
            return Replay(winner, fingerprint);
        }
        if (configurationError is not null) return Result(row, false);
        var activeProfile = profile!;

        AiUsageLimitLease? usageLease = null;
        var gateEntered = false;
        try
        {
            if (!gate.TryEnter(vision))
            {
                row.Status = "blocked";
                row.ErrorCode = "busy";
                row.ErrorMessage = "同时最多4个SDK调用，其中视觉最多2个，请稍后发起新调用";
                return await FinishAsync(row);
            }
            gateEntered = true;

            if (usageLimits is not null && profile?.ProviderConnectionId is { } connectionId)
            {
                var leaseTtl = TimeSpan.FromSeconds(activeProfile.TimeoutSeconds + 60);
                var decision = await usageLimits.TryBeginAsync(connectionId, row.CorrelationId,
                    row.Purpose, leaseTtl, ct);
                if (!decision.Allowed)
                {
                    row.Status = "blocked";
                    row.ErrorCode = decision.ErrorCode ?? "usage_limit_blocked";
                    row.ErrorMessage = decision.Message ?? "AI用量限制拒绝本次调用";
                    return await FinishAsync(row);
                }
                usageLease = decision.Lease;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(activeProfile.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            using var wire = new AiTextWireCapture(audio: audio);
            var materialsReady = !vision;
            try
            {
                if (vision)
                {
                    await visionMaterials!.StoreForOwnerAsync(userId, imageRows, images!, linked.Token);
                    materialsReady = true;
                    wire.SetImages(imageRows.Select((x, index) => new AiVisionAuditImage(x.Id,
                        $"data:{images![index].ContentType};base64,{Convert.ToBase64String(images[index].Bytes)}")).ToArray());
                    foreach (var image in images!) messages[^1].Contents.Add(new DataContent(image.Bytes, image.ContentType));
                }
                using var client = clients.Create(new(activeProfile.BaseUrl, activeProfile.Model, key!,
                    activeProfile.TimeoutSeconds, activeProfile.MaxOutputTokens));
                var response = await client.Client.GetResponseAsync(messages,
                    new ChatOptions { MaxOutputTokens = activeProfile.MaxOutputTokens }, linked.Token);
                row.ResponseText = Safe(response.Text, key, out var replyRedacted);
                var raw = response.RawRepresentation is ChatCompletion completion ? ModelReaderWriter.Write(completion).ToString()
                    : JsonSerializer.Serialize(new { response.ResponseId, response.ModelId, response.FinishReason, response.Messages, response.Usage }, Json);
                row.ResponseRawJson = SafeJson(raw, key, out var rawRedacted);
                row.ContentRedacted = row.ContentRedacted == true || replyRedacted || rawRedacted;
                row.FinishReason = response.FinishReason?.Value;
                row.PromptTokens = Count(response.Usage?.InputTokenCount); row.CompletionTokens = Count(response.Usage?.OutputTokenCount);
                row.TotalTokens = Count(response.Usage?.TotalTokenCount);
                // Missing usage/cost stays unknown; no invented token counts, prices or currency.
                if (response.FinishReason == ChatFinishReason.Stop && !string.IsNullOrEmpty(response.Text)) row.Status = "succeeded";
                else
                { row.Status = "incomplete"; row.ErrorCode = "incomplete_output"; row.ErrorMessage = "模型未正常结束或未返回文本，请核对实际回复和结束原因"; }
            }
            catch (OperationCanceledException)
            {
                row.Status = ct.IsCancellationRequested ? "cancelled" : "timed_out";
                row.ErrorCode = row.Status; row.ErrorMessage = ct.IsCancellationRequested ? "调用已取消，上游是否计费以其记录为准" : "模型调用超时，结果未确认；请核对记录后再发起新调用";
                if (!materialsReady) row.ErrorMessage = "图片材料保存已取消或超时，本次未发送模型";
            }
            catch (ClientResultException error)
            {
                row.Status = "failed"; row.ErrorCode = "upstream_http_" + error.Status;
                row.ErrorMessage = "模型服务返回HTTP " + error.Status + "，请核对配置或稍后发起新调用";
                var body = error.GetRawResponse()?.Content?.ToString();
                if (!string.IsNullOrEmpty(body))
                {
                    row.ResponseRawJson = SafeJson(ValidJson(body), key, out var redacted);
                    row.ContentRedacted = row.ContentRedacted == true || redacted;
                }
            }
            catch (Exception)
            { row.Status = "failed"; row.ErrorCode = materialsReady ? "provider_error" : "image_prepare_failed";
                row.ErrorMessage = materialsReady ? "模型连接、格式或响应大小异常；本次未自动重试，请核对调用记录" : "图片材料未能完整保存，本次未发送模型，请核对文件来源和调用记录"; }
            finally
            {
                if (wire.RequestBody is { } sent)
                {
                    row.RequestPayloadJson = SafeJson(sent, key, out var redacted);
                    using var parsed = JsonDocument.Parse(sent);
                    var messagesRedacted = false;
                    if (parsed.RootElement.TryGetProperty("messages", out var actual))
                        row.RequestMessagesJson = SafeJson(actual.GetRawText(), key, out messagesRedacted);
                    row.ContentRedacted = row.ContentRedacted == true || redacted || messagesRedacted;
                }
                if (wire.ResponseBody is { } received)
                {
                    row.ResponseRawJson = SafeJson(ValidJson(received), key, out var redacted);
                    row.ContentRedacted = row.ContentRedacted == true || redacted;
                }
                if (wire.ResponseTooLarge)
                { row.Status = "failed"; row.ErrorCode = "response_too_large"; row.ErrorMessage = "响应超过4MiB上限，未保存为完整结果"; }
                if (wire.RequestRejected)
                {
                    row.Status = "failed";
                    row.ErrorCode = audioMode ? "audio_manifest_mismatch" : "image_manifest_mismatch";
                    row.ErrorMessage = audioMode
                        ? "SDK音频清单与固定材料不一致，本次未发送模型"
                        : "SDK图片清单与固定材料不一致，本次未发送模型";
                }
                row.MetadataJson = JsonSerializer.Serialize(new { recordFormat, transportRetries = 0,
                    requestBodyCaptured = wire.RequestBody is not null, responseBodyComplete = wire.ResponseBody is not null,
                    responseTooLarge = wire.ResponseTooLarge, providerAttempted = wire.ProviderAttempted,
                    imageEncoding = vision ? "sha256-reference" : null, destinationConsent = consent,
                    imageInputsStored = vision ? imageRows.All(x => x.ObjectKey is not null) : (bool?)null,
                    audioEncoding = audioMode ? "sha256-reference" : null,
                    audioInputsReferenced = audio?.Count,
                    audioInputsSha256 = audio?.Select(x => x.SourceSha256).ToArray(),
                    audioContextOwnerId = audio?.FirstOrDefault()?.OwnerId,
                    audioContextNoteId = audio?.FirstOrDefault()?.NoteId,
                    audioContextSourceVersionId = audio?.FirstOrDefault()?.SourceVersionId }, Json);
            }
            try
            {
                return await FinishAsync(row);
            }
            finally
            {
                if (usageLease is not null)
                {
                    try { await usageLimits!.EndAsync(usageLease, CancellationToken.None); }
                    catch { /* The durable TTL still expires; never mask the actual call outcome. */ }
                }
            }
        }
        finally
        {
            if (gateEntered) gate.Exit(vision);
        }
    }

    private async Task<AiTextCallResultDto> FinishAsync(AiRequestLogEntity row)
    {
        row.FinishedAt = DateTimeOffset.UtcNow; row.DurationMs = (long)(row.FinishedAt.Value - row.StartedAt).TotalMilliseconds;
        row.OutputChars = row.ResponseText?.Length ?? 0; row.OutputHash = Hash(row.ResponseText ?? row.ResponseRawJson);
        // Persist cancellation/error outcomes even after the browser request was aborted.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(budget.Token); }
        catch (Exception error) when (error is DbUpdateException or OperationCanceledException)
        { throw new DomainException(50301, "请求已受理但结果记录未能确认保存；请先核对记录，不要自动重复调用"); }
        return Result(row, false);
    }

    private Task<AiRequestLogEntity?> FindAsync(Guid user, Guid request, CancellationToken ct) =>
        db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user && x.ClientRequestId == request, ct);

    private static string Fingerprint(AiTextCallRequest request, IReadOnlyList<AiVisionInput>? images,
        IReadOnlyList<AiAudioContextInput>? audio, AiVisionDestinationConsent? consent, OwnerCallScope? scope)
    {
        var vision = images is not null;
        var audioMode = audio is not null;
        if (scope is null)
        {
            // Existing HTTP text request IDs keep their original fingerprint.
            return Hash(audioMode ? JsonSerializer.Serialize(new { kind = "audio", request.ModelId, request.SystemPrompt,
                request.Prompt, consent, audio = AudioFingerprint(audio!) }, Json)
                : vision ? JsonSerializer.Serialize(new { kind = "vision", request.ModelId, request.SystemPrompt, request.Prompt,
                consent, images = images!.Select(x => new { x.Name, x.ContentType, x.Sha256, size = x.Bytes.Length }) }, Json)
                : JsonSerializer.Serialize(new { request.ModelId, request.SystemPrompt, request.Prompt }, Json));
        }
        return Hash(audioMode ? JsonSerializer.Serialize(new { kind = "audio", sourceObjectType = scope.SourceObjectType,
            sourceObjectId = scope.SourceObjectId, purpose = scope.Purpose, request.ModelId, request.SystemPrompt,
            request.Prompt, consent, audio = AudioFingerprint(audio!) }, Json)
            : vision ? JsonSerializer.Serialize(new { kind = "vision", sourceObjectType = scope.SourceObjectType,
            sourceObjectId = scope.SourceObjectId, purpose = scope.Purpose, request.ModelId, request.SystemPrompt, request.Prompt,
            consent, images = images!.Select(x => new { x.Name, x.ContentType, x.Sha256, size = x.Bytes.Length }) }, Json)
            : JsonSerializer.Serialize(new { kind = "text", sourceObjectType = scope.SourceObjectType,
                sourceObjectId = scope.SourceObjectId, purpose = scope.Purpose, request.ModelId, request.SystemPrompt,
                request.Prompt, consent }, Json));
    }

    internal static object[] AudioFingerprint(IReadOnlyList<AiAudioContextInput> audio) =>
        audio.Select(x => (object)new
        {
            x.Name,
            x.ContentType,
            x.SourceSha256,
            size = x.SizeBytes,
            x.OwnerId,
            x.NoteId,
            x.SourceVersionId,
            x.RecordingId,
            x.RecordingSegment,
            x.ChunkIndex,
            x.FileIndex,
            x.SourceOffsetSamples,
            x.Samples,
            x.SourcePath
        }).ToArray();

    private static void ValidateAudioContext(Guid owner, IReadOnlyList<AiAudioContextInput> audio)
    {
        if (audio.Count == 0 || audio.Count > 5_000)
            throw new DomainException(40001, "原始录音上下文片段数量无效");
        if (audio[0].NoteId == Guid.Empty || audio[0].SourceVersionId == Guid.Empty ||
            audio.Any(x => x.OwnerId != owner || x.NoteId != audio[0].NoteId ||
                x.SourceVersionId != audio[0].SourceVersionId))
            throw new DomainException(40001, "原始录音上下文没有绑定同一账号、笔记和固定材料版本；未发送模型");
        long total = 0;
        foreach (var item in audio)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 ||
                item.Name.Any(char.IsControl) || item.Name.Contains('/') || item.Name.Contains('\\') ||
                item.ContentType != "audio/wav" || item.SizeBytes <= 0 ||
                item.Bytes.Length != item.SizeBytes || item.Samples <= 0 ||
                item.OwnerId == Guid.Empty || item.NoteId == Guid.Empty ||
                item.SourceVersionId == Guid.Empty ||
                item.RecordingId == Guid.Empty || item.RecordingSegment <= 0 ||
                item.ChunkIndex <= 0 || item.FileIndex < 0 || item.SourceOffsetSamples < 0 ||
                string.IsNullOrWhiteSpace(item.SourcePath) ||
                !IsDigest(item.SourceSha256) ||
                !IsDigest(item.Sha256) ||
                !string.Equals(item.Sha256, Digest(item.Bytes), StringComparison.Ordinal))
                throw new DomainException(40001, "原始录音上下文身份或摘要无效；未发送模型");
            total = checked(total + item.SizeBytes);
            if (total > MaxAudioContextBytes)
                throw new DomainException(41301, "原始录音上下文超过单次调用字节上限；未发送模型");
        }
    }

    private async Task RequireActiveOwnerAsync(Guid owner, CancellationToken ct)
    {
        if (owner == Guid.Empty || !await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "发起账号已停用，派生任务未发送");
    }

    private static OwnerCallScope RequireOwnerCallScope(string sourceObjectType, string sourceObjectId, string purpose)
    {
        if (string.IsNullOrWhiteSpace(sourceObjectType) || sourceObjectType.Length > 128 || sourceObjectType.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(sourceObjectId) || sourceObjectId.Length > 256 || sourceObjectId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128 || purpose.Any(char.IsControl))
            throw new DomainException(40001, "派生调用来源或用途无效");
        return new(sourceObjectType, sourceObjectId, purpose);
    }

    private static AiTextCallResultDto Replay(AiRequestLogEntity row, string fingerprint)
    {
        if (row.InputHash != fingerprint) throw new DomainException(40901, "同一请求标识已绑定另一份输入，原记录保留");
        return Result(row, true);
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsDigest(string value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

}
