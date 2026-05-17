namespace Pim.Module.PcTracker.DTOs;

// POST /api/v1/pc/keystats/upload
public record KeystatsUploadRequest(
    string DeviceId,
    string Date,
    int KeyPresses,
    Dictionary<string, int>? KeyPressCounts,
    int LeftClicks,
    int RightClicks,
    int MiddleClicks,
    int SideBackClicks,
    int SideForwardClicks,
    double MouseDistance,
    double ScrollDistance,
    int PeakKps,
    int PeakCps,
    Dictionary<string, AppStatEntry>? AppStats
);

public record AppStatEntry(
    string AppName,
    string DisplayName,
    int KeyPresses,
    int LeftClicks,
    int RightClicks,
    int MiddleClicks,
    int SideBackClicks,
    int SideForwardClicks,
    double ScrollDistance
);

// POST /api/v1/pc/aw/upload
public record AwEventsUploadRequest(
    string DeviceId,
    List<AwEventEntry> Events
);

public record AwEventEntry(
    string Timestamp,
    double Duration,
    string EventType,
    string? AppName,
    string? WindowTitle,
    string? AfkStatus
);

// GET /api/v1/pc/summary
public record PcSummaryResponse(
    KeystatsSummary? Keystats,
    List<HeatmapBucket> Heatmap,
    List<AppRankingItem> AppRanking,
    List<TimelineItem> Timeline,
    List<WorkSessionItem> Sessions,
    DerivedMetrics? Metrics,
    List<CategorySummary> Categories
);

public record KeystatsSummary(
    string Date,
    int KeyPresses,
    int TotalClicks,
    int LeftClicks,
    int RightClicks,
    int MiddleClicks,
    int SideBackClicks,
    int SideForwardClicks,
    double MouseDistance,
    double ScrollDistance,
    int PeakKps,
    int PeakCps,
    List<KeyCountItem> TopKeys
);

public record KeyCountItem(string KeyName, int Count, double Share);

public record HeatmapBucket(
    string Start,
    string End,
    int Hour,
    int ActiveMinutes,
    int TotalEvents,
    int IntensityScore
);

public record AppRankingItem(
    string AppName,
    string DisplayName,
    int KeyPresses,
    int TotalClicks,
    double ScrollDistance,
    double Share
);

public record TimelineItem(
    string Start,
    string End,
    double DurationMinutes,
    string AppName,
    string? WindowTitle
);

public record WorkSessionItem(
    string Start,
    string End,
    double DurationMinutes,
    string MainApp,
    int AppSwitchCount
);

// 衍生指标
public record DerivedMetrics(
    string TotalRecordedDuration,
    string ActiveInputDuration,
    string IdleDuration,
    int SessionCount,
    int ActiveAppCount,
    int TotalKeyPresses,
    int TotalClicks,
    int AppSwitchCount,
    double SwitchFrequency,
    string MostFocusedApp,
    double KeyClickRatio
);

// 分类汇总
public record CategorySummary(
    string CategoryName,
    string Color,
    double Share,
    int KeyPresses,
    int TotalClicks
);

// 应用分类规则
public record AppCategoryRule(
    Guid Id,
    string AppPattern,
    string CategoryName,
    string Color,
    int Priority,
    bool IsBuiltin
);

// 详情查询参数
public record DetailQueryParams(
    string? DateFrom,
    string? DateTo,
    string? Dimension,
    string? DeviceId,
    string? AppName,
    string? CategoryName,
    string? KeyName,
    string? EventType,
    string? SortBy,
    string? SortDir,
    int Page,
    int PageSize
);

// 详情查询结果
public record DetailQueryResponse(
    List<Dictionary<string, object>> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

// 分类规则保存
public record SaveCategoryRequest(
    string AppPattern,
    string CategoryName,
    string Color,
    int Priority
);

// 热力图响应（扩展，支持多行网格）
public record HeatmapGridResponse(
    List<List<HeatmapBucket>> Grid,
    string Dimension,
    double MaxKeyCount
);
