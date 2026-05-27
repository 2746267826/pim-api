using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai;

public sealed class AiGateway(
    IOptions<AiOptions> options,
    IAiChatClientFactory chatClientFactory,
    IAiSchemaRegistry schemaRegistry,
    IAiRequestLogWriter logWriter) : IAiGateway
{
    public async Task<AiResult> CompleteAsync(AiGatewayRequest request, CancellationToken ct = default)
    {
        var ai = options.Value;
        var model = request.Model ?? ai.DefaultModel;
        var maxAttempts = Math.Min(request.EffectiveMaxAttempts, ai.MaxAttemptsPerRequest);
        var correlationId = Guid.NewGuid().ToString("N");

        if (!ai.Enabled)
        {
            var logId = await WriteLogAsync(
                request,
                model,
                correlationId,
                AiRequestStatus.Blocked,
                1,
                maxAttempts,
                JsonSerializer.Serialize(request.Messages),
                "{}",
                "{}",
                null,
                null,
                null,
                "disabled",
                "AI is disabled.",
                ct);
            return new AiResult(AiRequestStatus.Blocked, null, null, [], new AiTokenUsage(null, null, null, null, null), logId, "AI is disabled.");
        }

        var schema = ResolveSchema(request);
        IReadOnlyList<ChatMessage> currentMessages = ToChatMessages(request.Messages, schema is not null);
        Guid? lastLogId = null;
        IReadOnlyList<string> lastValidationErrors = [];

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                var maxOutputTokens = request.MaxOutputTokens ?? ai.MaxOutputTokensPerRequest;
                var client = chatClientFactory.Create(model);
                var response = await client.GetResponseAsync(currentMessages, new ChatOptions
                {
                    MaxOutputTokens = maxOutputTokens
                }, ct);
                var finished = DateTimeOffset.UtcNow;
                var text = response.Text ?? string.Empty;
                var usage = ExtractUsage(response);
                var rawJson = JsonSerializer.Serialize(response);
                var payloadJson = JsonSerializer.Serialize(new { model, maxOutputTokens, attempt });

                if (schema is not null)
                {
                    var validation = AiSchemaValidator.Validate(text, schema.JsonSchema);
                    if (!validation.IsValid)
                    {
                        lastValidationErrors = validation.Errors;
                        lastLogId = await logWriter.WriteAsync(CreateLogModel(
                            request,
                            model,
                            correlationId,
                            AiRequestStatus.FailedValidation,
                            attempt,
                            maxAttempts,
                            started,
                            finished,
                            JsonSerializer.Serialize(currentMessages),
                            payloadJson,
                            rawJson,
                            text,
                            null,
                            schema,
                            validation.Errors,
                            usage,
                            "schema_validation_failed",
                            "AI response failed schema validation."), ct);

                        if (attempt < maxAttempts)
                        {
                            currentMessages = CreateRepairMessages(text, validation.Errors, schema.JsonSchema);
                            continue;
                        }

                        return AiResult.FailedValidation(lastLogId, validation.Errors);
                    }

                    lastLogId = await logWriter.WriteAsync(CreateLogModel(
                        request,
                        model,
                        correlationId,
                        AiRequestStatus.Succeeded,
                        attempt,
                        maxAttempts,
                        started,
                        finished,
                        JsonSerializer.Serialize(currentMessages),
                        payloadJson,
                        rawJson,
                        text,
                        validation.ParsedOutputJson,
                        schema,
                        [],
                        usage,
                        null,
                        null), ct);
                    return new AiResult(AiRequestStatus.Succeeded, text, validation.ParsedOutputJson, [], usage, lastLogId, null);
                }

                lastLogId = await logWriter.WriteAsync(CreateLogModel(
                    request,
                    model,
                    correlationId,
                    AiRequestStatus.Succeeded,
                    attempt,
                    maxAttempts,
                    started,
                    finished,
                    JsonSerializer.Serialize(currentMessages),
                    payloadJson,
                    rawJson,
                    text,
                    null,
                    null,
                    [],
                    usage,
                    null,
                    null), ct);
                return new AiResult(AiRequestStatus.Succeeded, text, null, [], usage, lastLogId, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                lastLogId = await WriteLogAsync(
                    request,
                    model,
                    correlationId,
                    AiRequestStatus.TimedOut,
                    attempt,
                    maxAttempts,
                    JsonSerializer.Serialize(currentMessages),
                    "{}",
                    "{}",
                    null,
                    null,
                    schema,
                    "timed_out",
                    ex.Message,
                    ct);
                return new AiResult(AiRequestStatus.TimedOut, null, null, [], new AiTokenUsage(null, null, null, null, null), lastLogId, "AI request timed out.");
            }
            catch (Exception ex) when (IsProviderException(ex))
            {
                lastLogId = await WriteLogAsync(
                    request,
                    model,
                    correlationId,
                    AiRequestStatus.Failed,
                    attempt,
                    maxAttempts,
                    JsonSerializer.Serialize(currentMessages),
                    "{}",
                    "{}",
                    null,
                    null,
                    schema,
                    "provider_unavailable",
                    ex.Message,
                    ct);
                return new AiResult(AiRequestStatus.Failed, null, null, [], new AiTokenUsage(null, null, null, null, null), lastLogId, "AI provider is unavailable.");
            }
        }

        return AiResult.FailedValidation(lastLogId, lastValidationErrors);
    }

    private AiSchemaDefinition? ResolveSchema(AiGatewayRequest request)
    {
        if (request.SchemaName is null || request.SchemaVersion is null)
        {
            return null;
        }

        return schemaRegistry.Get(request.SchemaName, request.SchemaVersion)
            ?? throw new InvalidOperationException($"AI schema '{request.SchemaName}' version '{request.SchemaVersion}' is not registered.");
    }

    private static IReadOnlyList<ChatMessage> ToChatMessages(IReadOnlyList<AiMessage> messages, bool structured)
    {
        var converted = messages.Select(message => new ChatMessage(ToChatRole(message.Role), message.Content)).ToList();
        if (structured)
        {
            converted.Insert(0, new ChatMessage(ChatRole.System, "Return only JSON. Do not wrap JSON in Markdown."));
        }

        return converted;
    }

    private static ChatRole ToChatRole(AiMessageRole role) => role switch
    {
        AiMessageRole.System => ChatRole.System,
        AiMessageRole.Assistant => ChatRole.Assistant,
        _ => ChatRole.User
    };

    private static IReadOnlyList<ChatMessage> CreateRepairMessages(string failedJson, IReadOnlyList<string> errors, string schemaJson)
        =>
        [
            new ChatMessage(ChatRole.System, "Fix only the JSON so it validates against the schema. Return only corrected JSON."),
            new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new { failedJson, errors, schema = schemaJson }))
        ];

    private static AiTokenUsage ExtractUsage(ChatResponse response)
    {
        return new AiTokenUsage(
            ToNullableInt(response.Usage?.InputTokenCount),
            ToNullableInt(response.Usage?.OutputTokenCount),
            ToNullableInt(response.Usage?.TotalTokenCount),
            null,
            null);
    }

    private static int? ToNullableInt(long? value)
    {
        if (value is null)
        {
            return null;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private AiRequestLogWriteModel CreateLogModel(
        AiGatewayRequest request,
        string model,
        string correlationId,
        AiRequestStatus status,
        int attempt,
        int maxAttempts,
        DateTimeOffset started,
        DateTimeOffset finished,
        string messagesJson,
        string payloadJson,
        string rawJson,
        string? responseText,
        string? parsedJson,
        AiSchemaDefinition? schema,
        IReadOnlyList<string> validationErrors,
        AiTokenUsage usage,
        string? errorCode,
        string? errorMessage)
    {
        return new AiRequestLogWriteModel(
            UserId: null,
            request.Module,
            request.Purpose,
            request.SourceObjectType,
            request.SourceObjectId,
            options.Value.Provider,
            model,
            LiteLlmRequestId: null,
            correlationId,
            status,
            attempt,
            maxAttempts,
            started,
            finished,
            messagesJson,
            payloadJson,
            rawJson,
            responseText,
            parsedJson,
            schema?.Name,
            schema?.Version,
            schema?.JsonSchema,
            JsonSerializer.Serialize(validationErrors),
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            usage.EstimatedCost,
            usage.Currency,
            errorCode,
            errorMessage,
            JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>()));
    }

    private async Task<Guid> WriteLogAsync(
        AiGatewayRequest request,
        string model,
        string correlationId,
        AiRequestStatus status,
        int attempt,
        int maxAttempts,
        string messagesJson,
        string payloadJson,
        string rawJson,
        string? responseText,
        string? parsedJson,
        AiSchemaDefinition? schema,
        string? errorCode,
        string? errorMessage,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await logWriter.WriteAsync(new AiRequestLogWriteModel(
            null,
            request.Module,
            request.Purpose,
            request.SourceObjectType,
            request.SourceObjectId,
            options.Value.Provider,
            model,
            null,
            correlationId,
            status,
            attempt,
            maxAttempts,
            now,
            now,
            messagesJson,
            payloadJson,
            rawJson,
            responseText,
            parsedJson,
            schema?.Name,
            schema?.Version,
            schema?.JsonSchema,
            "[]",
            null,
            null,
            null,
            null,
            null,
            errorCode,
            errorMessage,
            JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>())), ct);
    }

    private static bool IsProviderException(Exception ex)
    {
        return ex is HttpRequestException
            or System.ClientModel.ClientResultException
            or IOException
            or TaskCanceledException;
    }
}
