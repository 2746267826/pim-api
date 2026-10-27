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
    ICurrentUserService currentUser, AiTextCallGate gate, AiVisionMaterialService? visionMaterials = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiTextCallResultDto> GetAsync(Guid clientRequestId, CancellationToken ct)
    {
        var user = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(user, clientRequestId, ct) ?? throw new DomainException(40401, "尚未找到本次调用记录");
        return Result(row, true);
    }

    public Task<AiTextCallResultDto> CallAsync(AiTextCallRequest request, CancellationToken ct) => CallCoreAsync(request, null, null, ct);

    public Task<AiTextCallResultDto> CallVisionAsync(AiVisionCallRequest request, CancellationToken ct)
    {
        if (request.ExpectedUserId != currentUser.UserId) throw new DomainException(40301, "会话已改变，图片未发送；请重新核对账号");
        if (request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请先核对本次图片、模型与目标地址，并明确同意发送图片");
        return CallCoreAsync(new(request.ClientRequestId, request.ExpectedUserId, request.Prompt, request.SystemPrompt, request.ModelId),
            AiVisionMaterialService.Decode(request), request.Consent, ct);
    }

    private async Task<AiTextCallResultDto> CallCoreAsync(AiTextCallRequest request, IReadOnlyList<AiVisionInput>? images,
        AiVisionDestinationConsent? consent, CancellationToken ct)
    {
        var vision = images is not null;
        var recordFormat = vision ? "sdk-vision-v1" : "sdk-text-v1";
        var userId = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (request.ExpectedUserId != userId) throw new DomainException(40301, "会话已改变，输入未发送；请重新核对账号后再调用");
        if (request.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Prompt) ||
            request.Prompt.Length + (request.SystemPrompt?.Length ?? 0) > 200_000 || request.SystemPrompt?.Length > 32_000)
            throw new DomainException(40001, "需要有效请求标识和正文；系统提示最多32000字符，总输入最多200000字符");
        // Existing text request IDs keep their original fingerprint. Vision binds consent to an immutable destination.
        var fingerprint = Hash(vision ? JsonSerializer.Serialize(new { kind = "vision", request.ModelId, request.SystemPrompt, request.Prompt,
            consent, images = images!.Select(x => new { x.Name, x.ContentType, x.Sha256, size = x.Bytes.Length }) }, Json)
            : JsonSerializer.Serialize(new { request.ModelId, request.SystemPrompt, request.Prompt }, Json));
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(userId, request.ClientRequestId, ct);
        if (old is not null) return Replay(old, fingerprint);

        var config = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var selected = request.ModelId ?? (vision ? config?.VisionModelId : config?.TextModelId);
        var profile = selected is { } id ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        // Consent is an assertion against server configuration, never a client-controlled routing override.
        // No file upload or model transport occurs if the displayed model/address/revision has changed.
        if (vision && (consent?.Confirmed != true || profile is null || consent.ModelId != profile.Id ||
            consent.ModelRevision != profile.Revision || !string.Equals(consent.Endpoint, profile.BaseUrl, StringComparison.Ordinal)))
            throw new DomainException(40901, "模型或目标地址已变化；图片未发送，请刷新并重新核对同意");
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new(ChatRole.System, request.SystemPrompt));
        messages.Add(new(ChatRole.User, request.Prompt));
        var finalMessages = messages.Select(m => new { role = m.Role.Value, content = m.Text }).ToArray();
        string? key = null; string? configurationError = null;
        if (config?.Enabled != true) configurationError = "SDK直连尚未启用，请在模型设置中启用";
        else if (profile is null) configurationError = vision ? "未选择有效视觉模型" : "未选择有效文本模型";
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
            Module = "system", Purpose = vision ? "ai.vision-call" : "ai.text-call", SourceObjectType = "ai-model",
            SourceObjectId = selected?.ToString() ?? "default", Provider = "openai-compatible",
            Model = profile?.Model ?? "未选择", ModelProfileId = profile?.Id, ModelProfileName = profile?.Name,
            ModelRevision = profile?.Revision, ModelEndpoint = profile?.BaseUrl,
            CorrelationId = Guid.NewGuid().ToString("N"), Status = configurationError is null ? "running" : "blocked",
            AttemptNumber = 1, MaxAttempts = 1, StartedAt = started,
            ExecutionExpiresAt = started.AddSeconds((profile?.TimeoutSeconds ?? 60) + 60),
            RequestMessagesJson = capturedMessages, InputChars = request.Prompt.Length + (request.SystemPrompt?.Length ?? 0),
            RequestPayloadJson = JsonSerializer.Serialize(new { model = profile?.Model, maxOutputTokens = profile?.MaxOutputTokens, stream = false, recordFormat }, Json),
            ContentRedacted = requestRedacted, ErrorMessage = configurationError,
            ErrorCode = configurationError is null ? null : "configuration_unavailable",
            FinishedAt = configurationError is null ? null : started,
            MetadataJson = JsonSerializer.Serialize(new { recordFormat, transportRetries = 0, requestBodyCaptured = false,
                providerAttempted = configurationError is not null ? false : (bool?)null,
                imageEncoding = vision ? "sha256-reference" : null, destinationConsent = consent }, Json)
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
        if (!gate.TryEnter(vision))
        { row.Status = "blocked"; row.ErrorCode = "busy"; row.ErrorMessage = "同时最多4个SDK调用，其中视觉最多2个，请稍后发起新调用"; return await FinishAsync(row); }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile!.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        using var wire = new AiTextWireCapture();
        var materialsReady = !vision;
        try
        {
            if (vision)
            {
                await visionMaterials!.StoreAsync(userId, imageRows, images!, linked.Token);
                materialsReady = true;
                wire.SetImages(imageRows.Select((x, index) => new AiVisionAuditImage(x.Id,
                    $"data:{images![index].ContentType};base64,{Convert.ToBase64String(images[index].Bytes)}")).ToArray());
                foreach (var image in images!) messages[^1].Contents.Add(new DataContent(image.Bytes, image.ContentType));
            }
            using var client = clients.Create(new(profile.BaseUrl, profile.Model, key!, profile.TimeoutSeconds));
            var response = await client.Client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = profile.MaxOutputTokens }, linked.Token);
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
            try
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
                { row.Status = "failed"; row.ErrorCode = "image_manifest_mismatch"; row.ErrorMessage = "SDK图片清单与固定材料不一致，本次未发送模型"; }
                row.MetadataJson = JsonSerializer.Serialize(new { recordFormat, transportRetries = 0,
                    requestBodyCaptured = wire.RequestBody is not null, responseBodyComplete = wire.ResponseBody is not null,
                    responseTooLarge = wire.ResponseTooLarge, providerAttempted = wire.ProviderAttempted,
                    imageEncoding = vision ? "sha256-reference" : null, destinationConsent = consent,
                    imageInputsStored = vision ? imageRows.All(x => x.ObjectKey is not null) : (bool?)null }, Json);
            }
            finally { gate.Exit(vision); }
        }
        return await FinishAsync(row);
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
    private static AiTextCallResultDto Replay(AiRequestLogEntity row, string fingerprint)
    {
        if (row.InputHash != fingerprint) throw new DomainException(40901, "同一请求标识已绑定另一份输入，原记录保留");
        return Result(row, true);
    }

}
