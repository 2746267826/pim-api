namespace Pim.Module.Mobile.DTOs;

public static class MobileAnalyticsDefaults
{
    public const string DefaultTimezone = "Asia/Shanghai";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int DefaultShortEventThresholdSeconds = 1;

    public static IReadOnlyList<string> LifeCategories => MobileLifeCategories.All;
}

public static class MobileLifeCategories
{
    public const string Social = "社交通讯";
    public const string ShortVideoEntertainment = "短视频/娱乐";
    public const string Game = "游戏";
    public const string MusicAudio = "音乐/音频";
    public const string ReadingNews = "阅读/资讯";
    public const string Learning = "学习";
    public const string WorkProductivity = "工作/生产力";
    public const string ToolsSystem = "工具/系统";
    public const string BrowserSearch = "浏览器/搜索";
    public const string TravelMaps = "出行/地图";
    public const string ShoppingFood = "购物/外卖";
    public const string FinancePayment = "金融/支付";
    public const string HealthFitness = "健康/运动";
    public const string CameraCreation = "相机/创作";
    public const string LifeServices = "生活服务";
    public const string Uncategorized = "未分类";

    public static readonly string[] All =
    [
        Social,
        ShortVideoEntertainment,
        Game,
        MusicAudio,
        ReadingNews,
        Learning,
        WorkProductivity,
        ToolsSystem,
        BrowserSearch,
        TravelMaps,
        ShoppingFood,
        FinancePayment,
        HealthFitness,
        CameraCreation,
        LifeServices,
        Uncategorized
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
