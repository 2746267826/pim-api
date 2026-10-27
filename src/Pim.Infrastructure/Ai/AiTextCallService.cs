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
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace Pim.Infrastructure.Ai;

public sealed class AiTextCallService(PimDbContext db, ISecretProtector secrets, IAiProfileChatClientFactory clients,
    ICurrentUserService currentUser, AiTextCallGate gate)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiTextCallResultDto> GetAsync(Guid clientRequestId, CancellationToken ct)
    {
        var user = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var row = await FindAsync(user, clientRequestId, ct) ?? throw new DomainException(40401, "尚未找到本次调用记录");
        return Result(row, true);
    }

    public async Task<AiTextCallResultDto> CallAsync(AiTextCallRequest request, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (request.ExpectedUserId != userId) throw new DomainException(40301, "会话已改变，输入未发送；请重新核对账号后再调用");
        if (request.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Prompt) ||
            request.Prompt.Length + (request.SystemPrompt?.Length ?? 0) > 200_000 || request.SystemPrompt?.Length > 32_000)
            throw new DomainException(40001, "需要有效请求标识和正文；系统提示最多32000字符，总输入最多200000字符");
        var fingerprint = Hash(JsonSerializer.Serialize(new { request.ModelId, request.SystemPrompt, request.Prompt }, Json));
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var old = await FindAsync(userId, request.ClientRequestId, ct);
        if (old is not null) return Replay(old, fingerprint);

        var config = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var selected = request.ModelId ?? config?.TextModelId;
        var profile = selected is { } id ? await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new(ChatRole.System, request.SystemPrompt));
        messages.Add(new(ChatRole.User, request.Prompt));
        var finalMessages = messages.Select(m => new { role = m.Role.Value, content = m.Text }).ToArray();
        string? key = null; string? configurationError = null;
        if (config?.Enabled != true) configurationError = "SDK直连尚未启用，请在模型设置中启用";
        else if (profile is null) configurationError = "未选择有效文本模型";
        else if (!profile.Enabled || (profile.Capabilities & 1) == 0) configurationError = "模型未启用或未声明文本能力";
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
            Module = "system", Purpose = "ai.text-call", SourceObjectType = "ai-model",
            SourceObjectId = selected?.ToString() ?? "default", Provider = "openai-compatible",
            Model = profile?.Model ?? "未选择", ModelProfileId = profile?.Id, ModelProfileName = profile?.Name,
            ModelRevision = profile?.Revision, ModelEndpoint = profile?.BaseUrl,
            CorrelationId = Guid.NewGuid().ToString("N"), Status = configurationError is null ? "running" : "blocked",
            AttemptNumber = 1, MaxAttempts = 1, StartedAt = started,
            ExecutionExpiresAt = started.AddSeconds((profile?.TimeoutSeconds ?? 60) + 60),
            RequestMessagesJson = capturedMessages, InputChars = request.Prompt.Length + (request.SystemPrompt?.Length ?? 0),
            RequestPayloadJson = JsonSerializer.Serialize(new { model = profile?.Model, maxOutputTokens = profile?.MaxOutputTokens, stream = false, recordFormat = "sdk-text-v1" }, Json),
            ContentRedacted = requestRedacted, ErrorMessage = configurationError,
            ErrorCode = configurationError is null ? null : "configuration_unavailable",
            FinishedAt = configurationError is null ? null : started,
            MetadataJson = "{\"recordFormat\":\"sdk-text-v1\",\"transportRetries\":0}"
        };
        db.AiRequestLogs.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await FindAsync(userId, request.ClientRequestId, ct);
            if (winner is null) throw;
            return Replay(winner, fingerprint);
        }
        if (configurationError is not null) return Result(row, false);
        if (!gate.TryEnter())
        { row.Status = "blocked"; row.ErrorCode = "busy"; row.ErrorMessage = "同时最多4个SDK调用，请稍后发起新调用"; return await FinishAsync(row); }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile!.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        using var wire = new AiTextWireCapture();
        try
        {
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
        { row.Status = "failed"; row.ErrorCode = "provider_error"; row.ErrorMessage = "模型连接、格式或响应大小异常；本次未自动重试，请核对调用记录"; }
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
                row.MetadataJson = JsonSerializer.Serialize(new { recordFormat = "sdk-text-v1", transportRetries = 0,
                    requestBodyCaptured = wire.RequestBody is not null, responseBodyComplete = wire.ResponseBody is not null,
                    responseTooLarge = wire.ResponseTooLarge }, Json);
            }
            finally { gate.Exit(); }
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
        { throw new DomainException(50301, "调用已执行但结果记录未能确认保存；请先核对记录，不要自动重复调用"); }
        return Result(row, false);
    }

    private Task<AiRequestLogEntity?> FindAsync(Guid user, Guid request, CancellationToken ct) =>
        db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user && x.ClientRequestId == request, ct);
    private static AiTextCallResultDto Replay(AiRequestLogEntity row, string fingerprint)
    {
        if (row.InputHash != fingerprint) throw new DomainException(40901, "同一请求标识已绑定另一份输入，原记录保留");
        return Result(row, true);
    }
    private static AiTextCallResultDto Result(AiRequestLogEntity row, bool replayed) => new(row.Id, row.Status switch
    {
        "succeeded" => AiRequestStatus.Succeeded, "running" => AiRequestStatus.Running, "blocked" => AiRequestStatus.Blocked,
        "timed_out" => AiRequestStatus.TimedOut, "cancelled" => AiRequestStatus.Cancelled,
        "incomplete" => AiRequestStatus.Incomplete, _ => AiRequestStatus.Failed
    }, row.ResponseText, row.ErrorMessage, replayed);
    private static int? Count(long? value) => value is null or < 0 or > int.MaxValue ? null : (int)value.Value;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Safe(string? value, string? key, out bool redacted)
    {
        var result = value ?? "";
        if (!string.IsNullOrEmpty(key))
        {
            result = result.Replace(key, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
            var encoded = JsonEncodedText.Encode(key).ToString();
            if (encoded != key) result = result.Replace(encoded, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
        }
        redacted = result != (value ?? ""); return result;
    }
    private static string ValidJson(string body)
    { try { using var json = JsonDocument.Parse(body); return body; } catch (JsonException) { return JsonSerializer.Serialize(new { body }, Json); } }

    private static string SafeJson(string value, string? key, out bool redacted)
    {
        if (string.IsNullOrEmpty(key) || !value.Contains(key, StringComparison.Ordinal) &&
            !value.Contains(JsonEncodedText.Encode(key).ToString(), StringComparison.Ordinal))
        { redacted = false; return value; }
        var changed = false;
        using var doc = JsonDocument.Parse(value); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        foreach (var property in element.EnumerateObject())
                        { writer.WritePropertyName(Safe(property.Name, key, out var propertyRedacted)); changed |= propertyRedacted; Write(property.Value); }
                        writer.WriteEndObject(); break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item); writer.WriteEndArray(); break;
                    case JsonValueKind.String:
                        writer.WriteStringValue(Safe(element.GetString(), key, out var stringRedacted)); changed |= stringRedacted; break;
                    default:
                        if (element.GetRawText() == key) { writer.WriteStringValue("[REDACTED_CREDENTIAL]"); changed = true; }
                        else element.WriteTo(writer); break;
                }
            }
            Write(doc.RootElement);
        }
        redacted = changed; return changed ? Encoding.UTF8.GetString(stream.ToArray()) : value;
    }
}
