using Pim.Core.Common;

namespace Pim.Core.Ai;

public sealed record AiMessage(AiMessageRole Role, string Content);

public sealed record AiGatewayRequest(
    string Module,
    string Purpose,
    string SourceObjectType,
    string SourceObjectId,
    IReadOnlyList<AiMessage> Messages,
    string? Model = null,
    string? SchemaName = null,
    string? SchemaVersion = null,
    int? MaxOutputTokens = null,
    int? MaxAttempts = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public int EffectiveMaxAttempts => Math.Clamp(MaxAttempts ?? 1, 1, 2);
}

public sealed record AiTokenUsage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    decimal? EstimatedCost,
    string? Currency);

public sealed record AiResult(
    AiRequestStatus Status,
    string? ResponseText,
    string? ParsedOutputJson,
    IReadOnlyList<string> SchemaValidationErrors,
    AiTokenUsage Usage,
    Guid? LogId,
    string? UserFacingError)
{
    public static AiResult FailedValidation(Guid? logId, IReadOnlyList<string> errors) =>
        new(
            AiRequestStatus.FailedValidation,
            ResponseText: null,
            ParsedOutputJson: null,
            SchemaValidationErrors: errors,
            Usage: new AiTokenUsage(null, null, null, null, null),
            LogId: logId,
            UserFacingError: "AI 响应不符合要求的格式，未生成建议。");
}

public sealed record AiSchemaDefinition(
    string Name,
    string Version,
    string JsonSchema,
    string Description);

public sealed record AiStatusDto(
    bool Enabled,
    string Provider,
    string BaseUrl,
    string DefaultModel,
    DateTimeOffset? LastHealthCheckAt,
    string? LastError,
    DateTimeOffset? RecentSuccessfulCallAt);

public sealed record AiRequestLogFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Module,
    string? Purpose,
    string? SourceObjectType,
    string? SourceObjectId,
    string? Model,
    AiRequestStatus? Status,
    Guid? UserId,
    int Page = 1,
    int PageSize = 50);

public sealed record AiRequestLogListItemDto(
    Guid Id,
    DateTimeOffset StartedAt,
    string Module,
    string Purpose,
    string Model,
    AiRequestStatus Status,
    int? TotalTokens,
    decimal? EstimatedCost,
    long? DurationMs,
    string SourceObjectType,
    string SourceObjectId,
    string? ErrorSummary);

public sealed record AiRequestLogDetailDto(
    Guid Id,
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
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    string RequestMessagesJson,
    string RequestPayloadJson,
    string ResponseRawJson,
    string? ResponseText,
    string? ParsedOutputJson,
    string? SchemaName,
    string? SchemaVersion,
    string? SchemaJsonSnapshot,
    string SchemaValidationErrorsJson,
    AiTokenUsage Usage,
    string? ErrorCode,
    string? ErrorMessage,
    string MetadataJson);

public sealed record AiUsageGroupDto(
    string GroupKey,
    int RequestCount,
    int SuccessCount,
    int FailureCount,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    decimal EstimatedCost);

public sealed record AiUsageSummaryDto(
    int RequestCount,
    int SuccessCount,
    int FailureCount,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    decimal EstimatedCost,
    IReadOnlyList<AiUsageGroupDto> ByModule,
    IReadOnlyList<AiUsageGroupDto> ByPurpose,
    IReadOnlyList<AiUsageGroupDto> ByModel,
    IReadOnlyList<AiUsageGroupDto> ByStatus);
