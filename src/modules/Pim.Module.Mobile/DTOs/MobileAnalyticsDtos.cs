namespace Pim.Module.Mobile.DTOs;

public static class MobileAnalyticsDefaults
{
    public const string DefaultTimezone = "Asia/Shanghai";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int DefaultShortEventThresholdSeconds = 1;

    public static IReadOnlyList<string> LifeCategories => MobileLifeCategories.All;
}

/// <summary>手机端生活分类与 PC 侧统一字典对齐（7 大类）。ToolsSystem 仅用于系统噪音，不进用户可选列表。</summary>
public static class MobileLifeCategories
{
    public const string ProgrammingTinkering = "编程/折腾";
    public const string Learning = "学习";
    public const string Video = "视频";
    public const string Chat = "聊天";
    public const string Documents = "文档";
    public const string Game = "游戏";
    public const string Other = "其他";

    /// <summary>系统噪音专用（保留），不进入用户可选分类。</summary>
    public const string ToolsSystem = "工具/系统";

    /// <summary>未分类语义对齐「其他」（默认兜底值）。</summary>
    public const string Uncategorized = Other;

    public static readonly string[] All =
    [
        ProgrammingTinkering,
        Learning,
        Video,
        Chat,
        Documents,
        Game,
        Other
    ];
}

public sealed record MobileAnalyticsRangeDto(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    string Timezone,
    string LocalStartDate,
    string LocalEndDate);

public sealed record MobileAnalyticsQueryRequest(
    DateTimeOffset? RangeStartUtc = null,
    DateTimeOffset? RangeEndUtc = null,
    string? Timezone = null,
    string? DeviceId = null,
    string? LifeCategory = null,
    string? PackageName = null,
    string? Source = null,
    bool? IncludeSystemNoise = null,
    int? MinDurationSeconds = null,
    string? Granularity = null,
    string? Cursor = null,
    int? Page = null,
    int? PageSize = null);

public sealed record MobileAnalyticsQueryContext(
    MobileAnalyticsRangeDto Range,
    string? DeviceId,
    string? LifeCategory,
    string? PackageName,
    string? Source,
    bool IncludeSystemNoise,
    int MinDurationSeconds,
    string Granularity,
    string? Cursor,
    int Page,
    int PageSize);

public sealed record MobileAnalyticsQualitySummaryDto(
    double UsageEventsCoverage,
    double FallbackShare,
    int MissingMetadataAppCount,
    double SystemNoiseShare,
    double ShortEventShare,
    int FailedOrPartialSyncBatchCount,
    DateTimeOffset? LastSyncAt,
    IReadOnlyList<string> QualityFlags);

public sealed record MobileGoalProgressDto(
    string Key,
    string Label,
    long LimitSeconds,
    long UsedSeconds,
    bool IsOverLimit,
    long RemainingSeconds);

public sealed record MobileAnomalyDto(
    string Code,
    string Severity,
    string Title,
    string Evidence,
    string DrilldownTarget);

public sealed record MobileSuggestionDto(
    string Code,
    string Text,
    string DrilldownTarget);

public sealed record MobileAnalyticsOverviewResponse(
    MobileAnalyticsRangeDto Range,
    DateTimeOffset GeneratedAt,
    bool IsStale,
    long TotalForegroundSeconds,
    long DailyAverageSeconds,
    double PreviousPeriodChange,
    string? HighestUseLocalDate,
    int? PeakLocalHour,
    int AppCount,
    int SwitchOrPickupCount,
    double Completeness,
    MobileAnalyticsQualitySummaryDto Quality,
    MobileGoalProgressDto? GoalProgress,
    IReadOnlyList<MobileAnomalyDto> Anomalies,
    IReadOnlyList<MobileSuggestionDto> Suggestions);

public sealed record MobileHeatmapBucketDto(
    DateTimeOffset BucketStartUtc,
    DateTimeOffset BucketEndUtc,
    string LocalDate,
    int LocalHour,
    string LifeCategory,
    long ForegroundSeconds,
    IReadOnlyList<string> QualityFlags);

public sealed record MobileAnalyticsChartPointDto(
    string Key,
    string Label,
    double Value,
    long? ForegroundSeconds,
    string? LifeCategory,
    string? PackageName,
    string? LocalDate,
    int? LocalHour);

public sealed record MobileAnalyticsChartDto(
    string Key,
    string Title,
    string ChartType,
    string Unit,
    IReadOnlyList<MobileAnalyticsChartPointDto> Points);

public sealed record MobileTimelineBlockAppDto(
    string PackageName,
    string DisplayName,
    long ForegroundSeconds);

public sealed record MobileTimelineBlockDto(
    string Id,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string LocalStart,
    string LocalEnd,
    string LifeCategory,
    long ForegroundSeconds,
    int SessionCount,
    int AppCount,
    IReadOnlyList<MobileTimelineBlockAppDto> TopApps,
    IReadOnlyList<string> QualityFlags,
    IReadOnlyDictionary<string, long>? SourceMix,
    bool IncludesSystemNoise);

public sealed record MobileTimelineBlockPageDto(
    IReadOnlyList<MobileTimelineBlockDto> Items,
    string? NextCursor,
    bool HasMore,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record MobileTimelineBlockSessionDto(
    string Id,
    string DeviceId,
    string PackageName,
    string DisplayName,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long DurationSeconds,
    string LifeCategory,
    string Source,
    double Confidence,
    IReadOnlyList<string> QualityFlags);

public sealed record MobileSessionEventDto(
    string Id,
    string SessionId,
    string DeviceId,
    string PackageName,
    string EventType,
    DateTimeOffset EventTimeUtc,
    string? ClassName,
    string RawJson);

public sealed record MobileAppCatalogOverrideDto(
    string PackageName,
    string? DisplayNameOverride,
    string LifeCategory,
    bool IsSystemNoise,
    bool HideShortEvents,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record MobileAppCatalogOverrideUpsertRequest(
    string PackageName,
    string? DisplayNameOverride,
    string LifeCategory,
    bool IsSystemNoise,
    bool HideShortEvents);

public sealed record MobileAppCategoryRuleDto(
    string Id,
    string RuleType,
    string Pattern,
    string LifeCategory,
    int Priority,
    bool IsEnabled,
    string? DisplayNameOverride = null,
    bool? IsSystemNoise = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record MobileAppCategoryRuleUpsertRequest(
    string RuleType,
    string Pattern,
    string LifeCategory,
    int Priority,
    bool IsEnabled,
    string? DisplayNameOverride = null,
    bool? IsSystemNoise = null);

public sealed record MobileUsageGoalDto(
    string Id,
    string Scope,
    string? PackageName,
    string? LifeCategory,
    string Label,
    long LimitSeconds,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MobileUsageGoalUpsertRequest(
    string Scope,
    string? PackageName,
    string? LifeCategory,
    string Label,
    long LimitSeconds,
    bool IsEnabled);
