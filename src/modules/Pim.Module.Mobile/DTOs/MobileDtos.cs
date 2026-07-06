using Pim.Core.Operations;

namespace Pim.Module.Mobile.DTOs;

public sealed record MobileDeviceRegisterRequest(
    string DeviceId,
    string? AndroidIdHash,
    string DisplayName,
    string Manufacturer,
    string Brand,
    string Model,
    string AndroidVersion,
    int SdkInt,
    string AppVersion,
    string MetadataJson)
{
    public string DeviceHash => AndroidIdHash ?? string.Empty;
    public string OsVersion => AndroidVersion;
    public int ApiLevel => SdkInt;
}

public sealed record MobileDeviceDto(
    Guid Id,
    string DeviceId,
    string? AndroidIdHash,
    string DisplayName,
    string Manufacturer,
    string Brand,
    string Model,
    string AndroidVersion,
    int SdkInt,
    string AppVersion,
    string MetadataJson,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastSyncAt,
    bool IsActive)
{
    public MobileDeviceDto(
        Guid id,
        string deviceId,
        string? deviceHash,
        string displayName,
        string manufacturer,
        string brand,
        string model,
        string osVersion,
        int apiLevel,
        string appVersion,
        string metadataJson,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset lastSeenAtUtc)
        : this(
            id,
            deviceId,
            deviceHash,
            displayName,
            manufacturer,
            brand,
            model,
            osVersion,
            apiLevel,
            appVersion,
            metadataJson,
            registeredAtUtc,
            lastSeenAtUtc,
            null,
            null,
            true)
    {
    }

    public string? DeviceHash => AndroidIdHash;
    public string OsVersion => AndroidVersion;
    public int ApiLevel => SdkInt;
}

public sealed record MobileGapRequest(
    string DeviceId,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    string CapabilityJson)
{
    public string CapabilitiesJson => CapabilityJson;
}

public sealed record MobileGapWindowDto(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Reason,
    string SourcePreference)
{
    public string SignalsJson => SourcePreference;
}

public sealed record MobileGapResponse(
    DateTimeOffset MaxBackfillStartUtc,
    IReadOnlyList<MobileGapWindowDto> Windows);

public sealed record MobileUsageEventsUploadRequest(
    string DeviceId,
    string ClientBatchId,
    DateTimeOffset SourceWindowStartUtc,
    DateTimeOffset SourceWindowEndUtc,
    IReadOnlyList<MobileAppMetadataDto> Apps,
    IReadOnlyList<MobileUsageEventDto> Events,
    IReadOnlyList<MobileUsageSummaryDto> FallbackSummaries)
{
    public string BatchId => ClientBatchId;
    public DateTimeOffset WindowStartUtc => SourceWindowStartUtc;
    public DateTimeOffset WindowEndUtc => SourceWindowEndUtc;
    public IReadOnlyList<MobileUsageSummaryDto> Summaries => FallbackSummaries;
}

public sealed record MobileAppMetadataDto(
    string PackageName,
    string DisplayName,
    string? VersionName,
    long VersionCode,
    bool IsSystemApp,
    string? CategoryName,
    string? InstallerPackageName,
    DateTimeOffset? FirstInstallTimeUtc,
    DateTimeOffset? LastUpdateTimeUtc,
    string RawJson)
{
    public string? Category => CategoryName;
    public string? InstallerPackage => InstallerPackageName;
}

public sealed record MobileUsageEventDto(
    string PackageName,
    string EventType,
    DateTimeOffset EventTimestampUtc,
    string? ClassName,
    DateTimeOffset CollectedAtUtc,
    string RawJson);

public sealed record MobileUsageSummaryDto(
    string PackageName,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    long TotalTimeForegroundMs,
    DateTimeOffset? LastTimeUsedUtc,
    string SourceKind,
    string RawJson)
{
    public long TotalTimeVisibleMs => TotalTimeForegroundMs;
}

public sealed record MobileUsageIngestResult(
    string BatchId,
    int AcceptedCount,
    int SkippedCount,
    int RejectedCount,
    int FailedCount)
{
    public MobileUsageIngestResult(string batchId, int acceptedCount, int failedCount)
        : this(batchId, acceptedCount, 0, 0, failedCount)
    {
    }
}

public sealed record MobileLocationPointRequest(
    string DeviceId,
    DateTimeOffset RecordedAtUtc,
    double Latitude,
    double Longitude,
    double HorizontalAccuracyMeters,
    string Provider,
    string SourceKind,
    double? AltitudeMeters,
    double? VerticalAccuracyMeters,
    double? SpeedMetersPerSecond,
    double? SpeedAccuracyMetersPerSecond,
    double? BearingDegrees,
    double? BearingAccuracyDegrees,
    bool IsAutoSubmitted,
    string RawJson)
{
    public string Source => SourceKind;
    public bool IsMock => false;
}

public sealed record MobileLocationPointDto(
    Guid Id,
    string DeviceId,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset SubmittedAtUtc,
    double Latitude,
    double Longitude,
    double HorizontalAccuracyMeters,
    string Provider,
    string SourceKind,
    double? AltitudeMeters,
    double? VerticalAccuracyMeters,
    double? SpeedMetersPerSecond,
    double? SpeedAccuracyMetersPerSecond,
    double? BearingDegrees,
    double? BearingAccuracyDegrees,
    bool IsAutoSubmitted,
    string Quality,
    string RawJson)
{
    public MobileLocationPointDto(
        Guid id,
        string deviceId,
        DateTimeOffset recordedAtUtc,
        double latitude,
        double longitude,
        double horizontalAccuracyMeters,
        string provider,
        string sourceKind,
        string quality)
        : this(
            id,
            deviceId,
            recordedAtUtc,
            recordedAtUtc,
            latitude,
            longitude,
            horizontalAccuracyMeters,
            provider,
            sourceKind,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            quality,
            "{}")
    {
    }

    public string Source => SourceKind;
}

public sealed record MobileSummaryQuery(
    string? DeviceId,
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc);

public sealed record MobileAppUsageSummaryDto(
    string PackageName,
    string DisplayName,
    string? CategoryName,
    long ForegroundSeconds,
    int SessionCount,
    int LaunchCount,
    DateTimeOffset? LastUsedAt,
    string Source,
    double Share);

public sealed record MobileSyncBatchSummaryDto(
    Guid Id,
    string DeviceId,
    string ClientBatchId,
    DateTimeOffset SourceWindowStartUtc,
    DateTimeOffset SourceWindowEndUtc,
    DateTimeOffset SubmittedAtUtc,
    string Status,
    int AcceptedEventCount,
    int SkippedEventCount,
    int AcceptedLocationCount,
    int RejectedLocationCount,
    string? ErrorMessage);

public sealed record MobileUsageSummaryResponse(
    string Date,
    string? DeviceId,
    DateTimeOffset GeneratedAt,
    long TotalForegroundSeconds,
    long FallbackForegroundSeconds,
    int AppSwitchCount,
    int AppsUsed,
    double Completeness,
    DateTimeOffset? LastSyncAt,
    IReadOnlyList<MobileAppUsageSummaryDto> AppRanking,
    IReadOnlyList<MobileSyncBatchSummaryDto> SyncBatches,
    int QualityIssueCount);

public sealed record MobileTimelineItemDto(
    string Id,
    string Kind,
    string DeviceId,
    string PackageName,
    string DisplayName,
    DateTimeOffset Start,
    DateTimeOffset? End,
    long DurationSeconds,
    string Source,
    double Confidence,
    string Reason);

public sealed record MobileTimelineResponse(
    string Date,
    string? DeviceId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MobileTimelineItemDto> Sessions,
    IReadOnlyList<MobileTimelineItemDto> FallbackSummaries,
    IReadOnlyList<MobileTimelineItemDto> Items);

public sealed record MobileLocationHistoryResponse(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? DeviceId,
    double MaxAccuracyMeters,
    IReadOnlyList<MobileLocationPointDto> Points);

public sealed record MobileQualityResponse(
    PimHealthStatus OverallStatus,
    string Label,
    string Message,
    DateTimeOffset CheckedAt,
    IReadOnlyList<MobileQualityComponentDto> Components,
    IReadOnlyList<MobileQualityIssueDto> Issues,
    IReadOnlyList<string> NextSteps)
{
    public MobileQualityResponse(
        PimHealthStatus overallStatus,
        DateTimeOffset checkedAt,
        IReadOnlyList<MobileQualityComponentDto> components,
        IReadOnlyList<MobileQualityIssueDto> issues)
        : this(
            overallStatus,
            "Android 采集正常",
            "移动端同步、定位和应用使用采集诊断可用。",
            checkedAt,
            components,
            issues,
            Array.Empty<string>())
    {
    }
}

public sealed record MobileQualityComponentDto(
    string Key,
    string Name,
    PimHealthStatus Status,
    string Message,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, string> Details)
{
    public MobileQualityComponentDto(
        string key,
        string name,
        PimHealthStatus status,
        string message,
        IReadOnlyDictionary<string, string> details)
        : this(key, name, status, message, DateTimeOffset.UtcNow, details)
    {
    }
}

public sealed record MobileQualityIssueDto(
    string Code,
    PimHealthStatus Severity,
    string ComponentKey,
    string Message,
    string? NextStep);
