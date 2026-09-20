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
    string RawJson,
    string? ClientItemKey = null)
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
    string RawJson,
    string? ClientItemKey = null);

public sealed record MobileUsageSummaryDto(
    string PackageName,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    long TotalTimeForegroundMs,
    DateTimeOffset? LastTimeUsedUtc,
    string SourceKind,
    string RawJson,
    string? ClientItemKey = null)
{
    public long TotalTimeVisibleMs => TotalTimeForegroundMs;
}

public sealed record MobileIngestItemResult(
    string ClientItemKey,
    string EntityType,
    string Outcome,
    string Code,
    string Message);

public sealed record MobileUsageIngestResult(
    string BatchId,
    int AcceptedCount,
    int SkippedCount,
    int RejectedCount,
    int FailedCount,
    IReadOnlyList<MobileIngestItemResult> ItemResults)
{
    public MobileUsageIngestResult(
        string batchId,
        int acceptedCount,
        int skippedCount,
        int rejectedCount,
        int failedCount)
        : this(batchId, acceptedCount, skippedCount, rejectedCount, failedCount, [])
    {
    }

    public MobileUsageIngestResult(string batchId, int acceptedCount, int failedCount)
        : this(batchId, acceptedCount, 0, 0, failedCount, [])
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

public sealed record MobileLocationPointsUploadRequest(
    IReadOnlyList<MobileLocationPointRequest> Points);

/// <summary>
/// 批量定位点上传结果（#246）。逐条返回结果，让客户端积压时能一次补传并把
/// "已接受 / 重复 / 被拒"映射回本地队列，而不是一次请求一个点。
/// </summary>
public sealed record MobileLocationPointsUploadResult(
    int AcceptedCount,
    int SkippedCount,
    int RejectedCount,
    IReadOnlyList<MobileIngestItemResult> ItemResults);

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

/// <summary>
/// 手机端 timeline 查询（#330）。分页参数只属于本接口，因此独立于
/// <see cref="MobileSummaryQuery"/> —— 汇总接口不接受分页，声明了却静默忽略
/// 比不声明更坏。
/// </summary>
public sealed record MobileTimelineQuery(
    string? DeviceId,
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// timeline 分页口径（#330）。上限存在的意义是保护服务端内存与响应体大小，
/// 而<b>不是</b>静默丢数据：任何被截断的页都会在
/// <see cref="MobileTimelineResponse.Truncated"/> / <c>HasMore</c> / <c>TotalCount</c>
/// 上如实声明，调用方据此翻页即可取回全天数据。
/// </summary>
public static class MobileTimelinePagination
{
    /// <summary>
    /// 默认每页条数。旧实现是硬编码 <c>Take(500)</c>（#330 实测单日 2049 条只返回 500 条）。
    /// <para>
    /// 取值依据为生产镜像（<c>pim_test</c>）实测的「单设备 × 单业务日」会话量：
    /// 峰值 2922 条、第 2 名 2785 条；5000 可在留有余量的前提下覆盖该量级，
    /// 使按设备查询的默认请求不再截断。跨设备（不传 <c>deviceId</c>）单日峰值 7863 条，
    /// 超出部分由分页标记显式暴露，调用方翻页读取。
    /// </para>
    /// </summary>
    public const int DefaultPageSize = 5000;

    /// <summary>
    /// 每页条数硬上限（调用方显式传参时夹紧到该值）。取值需覆盖实测最重的
    /// fallback 汇总日（单设备单日 30444 条），使调用方必要时能一页取完，
    /// 同时仍为单次响应体大小设定上界。
    /// </summary>
    public const int MaxPageSize = 50000;

    /// <summary>
    /// 单次请求允许读取的最大偏移量。
    /// <para>
    /// 合并流按时间排序，要在「会话 + fallback 汇总」的交错流里定位第 N 页，
    /// 必须至少读过该页之前的行，因此读取成本天然是 O(skip)（这是偏移分页的固有代价，
    /// 不是实现缺陷）。若不设上限，一个超大 <c>page</c> 就能让单次请求物化整段历史
    /// （接口允许不传 <c>date</c>，此时范围是全部历史）。
    /// </para>
    /// <para>
    /// 20 万行已远超任何真实单日窗口（实测单日峰值：会话 7863、汇总 30444），
    /// 因此正常调用永远不会触碰该上限；确实需要更大窗口的调用方应按日期分段请求。
    /// 超过该上限时接口返回明确错误，而不是静默读取巨量数据或返回错误的分页内容。
    /// </para>
    /// </summary>
    public const long MaxReadableOffset = 200_000;

    /// <summary>页码从 1 起算；缺失或小于 1 一律视为第 1 页。</summary>
    public static int ClampPage(int? page) => page is null or < 1 ? 1 : page.Value;

    /// <summary>每页条数夹紧到 [1, <see cref="MaxPageSize"/>]，缺失时取默认值。</summary>
    public static int ClampPageSize(int? pageSize)
        => Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
}

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
    int RejectedItemCount,
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

/// <summary>
/// 手机端时间线（#330）。<see cref="Sessions"/> / <see cref="FallbackSummaries"/> /
/// <see cref="Items"/> 三者都只包含<b>当前页</b>的数据，且描述的是同一批行：
/// 会话与 fallback 汇总按时间合并成一条流后统一分页，因此
/// <c>Items</c> 全天有序、逐页拼接不会乱序，另两个数组只是按来源做的切分。
/// <para>
/// 分页与截断字段为向后兼容的新增字段：旧客户端只读 <c>sessions</c> / <c>items</c>
/// 仍能正常工作（不传 <c>page</c> 时默认返回第一页，且默认页大小已覆盖绝大多数业务日）；
/// 新调用方必须检查 <see cref="HasMore"/> / <see cref="Truncated"/>，不能假设拿到的是全天数据。
/// </para>
/// </summary>
/// <param name="TotalCount">合并流（sessions + fallbackSummaries）的总条数，分页以此为基准。</param>
/// <param name="SessionTotalCount">仅 sessions 的总条数。</param>
/// <param name="FallbackTotalCount">仅 fallbackSummaries 的总条数。</param>
/// <param name="HasMore">后面是否还有下一页。与 <see cref="Truncated"/> 同义。</param>
/// <param name="Truncated">
/// 本页是否因分页上限而丢弃了数据。含义与 <see cref="HasMore"/> 一致，
/// 但对调用方更直白：true 表示「你看到的不是全部」。
/// </param>
public sealed record MobileTimelineResponse(
    string Date,
    string? DeviceId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MobileTimelineItemDto> Sessions,
    IReadOnlyList<MobileTimelineItemDto> FallbackSummaries,
    IReadOnlyList<MobileTimelineItemDto> Items,
    int Page = 1,
    int PageSize = MobileTimelinePagination.DefaultPageSize,
    int TotalCount = 0,
    int SessionTotalCount = 0,
    int FallbackTotalCount = 0,
    bool HasMore = false,
    bool Truncated = false);

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

/// <summary>
/// 待补应用元数据的包清单（#245）。用于让客户端/回填任务知道"该补哪些包"，
/// 而不是只拿到一个口径漂移的数字。
/// </summary>
public sealed record MobileMissingAppMetadataResponse(
    string? DeviceId,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    int MissingPackageCount,
    IReadOnlyList<MissingAppMetadataPackageDto> Packages);

public sealed record MissingAppMetadataPackageDto(
    string PackageName,
    int EventCount,
    DateTimeOffset? LastUsedAtUtc,
    long ForegroundMs);
