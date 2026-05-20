using System.Text.Json.Serialization;

namespace Pim.Module.PcTracker.DTOs;

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

public record CategorySummary(
    string CategoryName,
    string Color,
    double Share,
    int KeyPresses,
    int TotalClicks
);

public record AppCategoryRule(
    Guid Id,
    string AppPattern,
    string CategoryName,
    string Color,
    int Priority,
    bool IsBuiltin
);

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

public record DetailQueryResponse(
    List<Dictionary<string, object>> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

public record PcDetailRecord(
    string RecordType,
    string Start,
    string? End,
    double? DurationSeconds,
    string DeviceId,
    string? AppName,
    string? DisplayName,
    string? CategoryName,
    string? Title,
    int? KeyPresses,
    int? TotalClicks,
    double? MouseDistance,
    double? ScrollDistance,
    Dictionary<string, int>? KeyCounts,
    object? Raw
);

public record TypedDetailQueryResponse(
    List<PcDetailRecord> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

public record SaveCategoryRequest(
    string AppPattern,
    string CategoryName,
    string Color,
    int Priority
);

public record HeatmapGridResponse(
    List<List<HeatmapBucket>> Grid,
    string Dimension,
    double MaxKeyCount
);

public record AwInfoDto(
    string? Hostname,
    string? Version,
    bool Testing,
    string? DeviceId
);

public record AwBucketDto(
    string Id,
    string? Name,
    string Type,
    string Client,
    string Hostname,
    string? Created,
    string? LastUpdated,
    Dictionary<string, object>? Data
);

public record CompleteAwEventEntry(
    long SourceEventId,
    string Timestamp,
    double Duration,
    Dictionary<string, object>? Data
);

public record CompleteAwUploadRequest(
    string PimDeviceId,
    AwInfoDto? AwInfo,
    AwBucketDto Bucket,
    List<CompleteAwEventEntry> Events
);

public record KeystatsSampleUploadRequest(
    string PimDeviceId,
    string SampledAt,
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
    [property: JsonPropertyName("peakKPS")]
    int PeakKps,
    [property: JsonPropertyName("peakCPS")]
    int PeakCps,
    [property: JsonPropertyName("formattedMouseDistance")]
    string? FormattedMouseDistance,
    [property: JsonPropertyName("formattedScrollDistance")]
    string? FormattedScrollDistance,
    Dictionary<string, AppStatEntry>? AppStats
);
