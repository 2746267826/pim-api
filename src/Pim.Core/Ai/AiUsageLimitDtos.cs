namespace Pim.Core.Ai;

public static class AiUsageLimitPeriods
{
    public const string Day = "day";
    public const string Month = "month";

    public static readonly string[] All = [Day, Month];

    public static bool IsKnown(string? value) => value is Day or Month;
}

public static class AiUsageLimitLevels
{
    public const string Warning = "warning";
    public const string Reached = "reached";
}

public static class AiUsageLimitStatuses
{
    public const string Unknown = "unknown";
    public const string Unsupported = "unsupported";
    public const string Normal = "normal";
    public const string Warning = "warning";
    public const string Reached = "reached";
}

public sealed record AiProviderConnectionDto(
    Guid Id,
    Guid Revision,
    string Name,
    int ModelCount,
    AiUsageLimitRuleDto Limit,
    DateTimeOffset UpdatedAt);

public sealed record SaveAiProviderConnectionRequest(string Name, Guid? ExpectedRevision = null);

public sealed record AiUsageLimitRuleDto(
    Guid ConnectionId,
    Guid Revision,
    bool Enabled,
    string Period,
    int WarningPercent,
    long? TokenLimit,
    decimal? CostLimit,
    string? Currency,
    string TimeZoneId);

public sealed record SaveAiUsageLimitRuleRequest(
    Guid ExpectedRevision,
    bool Enabled,
    string Period,
    int WarningPercent,
    long? TokenLimit,
    decimal? CostLimit,
    string? Currency,
    string TimeZoneId);

public sealed record AiUsageLimitUsageDto(
    Guid ConnectionId,
    string ConnectionName,
    string Period,
    string CycleKey,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZoneId,
    int RequestCount,
    int RunningRequestCount,
    int IncompleteRequestCount,
    int TokenReportedRequestCount,
    int TokenUnknownRequestCount,
    long? TotalTokens,
    int CostKnownRequestCount,
    int CostUnknownRequestCount,
    decimal? EstimatedCost,
    string? Currency,
    long? TokenLimit,
    decimal? CostLimit,
    int WarningPercent,
    string TokenStatus,
    string CostStatus,
    string Status,
    IReadOnlyList<string> CoverageNotes);

public sealed record AiUsageLimitSnapshotDto(
    long? TotalTokens,
    long? TokenLimit,
    decimal? EstimatedCost,
    decimal? CostLimit,
    string? Currency,
    int RequestCount,
    int TokenUnknownRequestCount,
    int CostUnknownRequestCount,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record AiUsageReminderDto(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    string Period,
    string CycleKey,
    string Level,
    int WarningPercent,
    string TimeZoneId,
    IReadOnlyList<string> Metrics,
    AiUsageLimitSnapshotDto Snapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Read);

public sealed record AiUsageLimitLease(Guid Id, Guid ConnectionId, string RequestKey);

public sealed record AiUsageLimitDecision(
    bool Allowed,
    string? ErrorCode = null,
    string? Message = null,
    AiUsageLimitLease? Lease = null)
{
    public static AiUsageLimitDecision Allow(AiUsageLimitLease? lease = null) => new(true, Lease: lease);
    public static AiUsageLimitDecision Deny(string code, string message) => new(false, code, message);
}
