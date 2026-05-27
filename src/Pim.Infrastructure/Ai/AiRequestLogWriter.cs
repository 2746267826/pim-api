using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

public sealed record AiRequestLogWriteModel(
    Guid? UserId,
    string Module,
    string Purpose,
    string SourceObjectType,
    string SourceObjectId,
    string Provider,
    string Model,
    string? LiteLlmRequestId,
    string CorrelationId,
    AiRequestStatus Status,
    int AttemptNumber,
    int MaxAttempts,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string RequestMessagesJson,
    string RequestPayloadJson,
    string ResponseRawJson,
    string? ResponseText,
    string? ParsedOutputJson,
    string? SchemaName,
    string? SchemaVersion,
    string? SchemaJsonSnapshot,
    string SchemaValidationErrorsJson,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    decimal? EstimatedCost,
    string? Currency,
    string? ErrorCode,
    string? ErrorMessage,
    string MetadataJson);

public interface IAiRequestLogWriter
{
    Task<Guid> WriteAsync(AiRequestLogWriteModel model, CancellationToken ct = default);
}

public sealed class AiRequestLogWriter(PimDbContext db) : IAiRequestLogWriter
{
    public async Task<Guid> WriteAsync(AiRequestLogWriteModel model, CancellationToken ct = default)
    {
        var redactedMessages = AiRedactor.RedactJson(model.RequestMessagesJson);
        var redactedPayload = AiRedactor.RedactJson(model.RequestPayloadJson);
        var redactedResponseRaw = AiRedactor.RedactJson(model.ResponseRawJson);
        var redactedMetadata = AiRedactor.RedactJson(model.MetadataJson);
        var redactedResponseText = RedactPlainText(model.ResponseText);
        var input = redactedMessages + redactedPayload;
        var output = (redactedResponseText ?? string.Empty) + redactedResponseRaw;

        var entity = new AiRequestLogEntity
        {
            UserId = model.UserId,
            Module = model.Module,
            Purpose = model.Purpose,
            SourceObjectType = model.SourceObjectType,
            SourceObjectId = model.SourceObjectId,
            Provider = model.Provider,
            Model = model.Model,
            LiteLlmRequestId = model.LiteLlmRequestId,
            CorrelationId = model.CorrelationId,
            Status = ToStorageStatus(model.Status),
            AttemptNumber = model.AttemptNumber,
            MaxAttempts = model.MaxAttempts,
            StartedAt = model.StartedAt,
            FinishedAt = model.FinishedAt,
            DurationMs = (long)(model.FinishedAt - model.StartedAt).TotalMilliseconds,
            RequestMessagesJson = redactedMessages,
            RequestPayloadJson = redactedPayload,
            ResponseRawJson = redactedResponseRaw,
            ResponseText = redactedResponseText,
            ParsedOutputJson = model.ParsedOutputJson,
            SchemaName = model.SchemaName,
            SchemaVersion = model.SchemaVersion,
            SchemaJsonSnapshot = model.SchemaJsonSnapshot,
            SchemaValidationErrorsJson = model.SchemaValidationErrorsJson,
            PromptTokens = model.PromptTokens,
            CompletionTokens = model.CompletionTokens,
            TotalTokens = model.TotalTokens,
            EstimatedCost = model.EstimatedCost,
            Currency = model.Currency,
            InputChars = input.Length,
            OutputChars = output.Length,
            InputHash = Sha256(input),
            OutputHash = Sha256(output),
            ErrorCode = model.ErrorCode,
            ErrorMessage = model.ErrorMessage,
            MetadataJson = redactedMetadata
        };

        db.AiRequestLogs.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    private static string? RedactPlainText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var redactedJsonString = AiRedactor.RedactJson(JsonSerializer.Serialize(value));
        return JsonSerializer.Deserialize<string>(redactedJsonString);
    }

    private static string ToStorageStatus(AiRequestStatus status) => status switch
    {
        AiRequestStatus.Succeeded => "succeeded",
        AiRequestStatus.Failed => "failed",
        AiRequestStatus.Blocked => "blocked",
        AiRequestStatus.TimedOut => "timed_out",
        AiRequestStatus.FailedValidation => "failed_validation",
        _ => "failed"
    };

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
