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

/// <summary>
/// 两个 keystats 上传端点「契约标注为必填」的字段名清单（线上 camelCase 拼写）。
///
/// issue #419：契约给 <c>KeystatsUploadRequest</c> 标了 12 个必填、给
/// <c>KeystatsSampleUploadRequest</c> 标了 11 个，但服务端原先一个都不查 ——
/// 缺数字字段就静默存 0。这里把清单显式化，由端点做真校验；
/// <c>tests/Pim.UnitTests/Contract/OpenApiContractShapeTests</c> 把它与生成契约的
/// <c>required[]</c> 逐字对齐，防止再出现「契约比实现严」。
/// </summary>
public static class KeystatsRequiredFields
{
    /// <summary><c>POST /api/v1/pc/keystats/upload</c>：契约 required 的 12 项。</summary>
    public static readonly string[] Upload =
    [
        "deviceId",
        "date",
        "keyPresses",
        "leftClicks",
        "middleClicks",
        "mouseDistance",
        "peakCps",
        "peakKps",
        "rightClicks",
        "scrollDistance",
        "sideBackClicks",
        "sideForwardClicks"
    ];

    /// <summary>
    /// <c>POST /api/v1/pc/keystats/samples</c>：契约 required 的 13 项。
    ///
    /// 工单正文写的是「11 个必填」，那是**旧契约的漏标**：这两个属性原先被
    /// <c>JsonPropertyName("peakKPS"/"peakCPS")</c> 固定成大写，而 required 推导按
    /// C# 属性名的 camelCase（peakKps）去找 schema 键，对不上就被跳过。
    /// A2 去掉注解后键名对上了，非可空 <c>int</c> 按既有口径进 required → 13 项；
    /// 已与用户确认按重导出的契约走（两个端点口径一致，缺 peak 不再静默存 0）。
    /// </summary>
    public static readonly string[] Sample =
    [
        "pimDeviceId",
        "sampledAt",
        "date",
        "keyPresses",
        "leftClicks",
        "middleClicks",
        "mouseDistance",
        "peakCps",
        "peakKps",
        "rightClicks",
        "scrollDistance",
        "sideBackClicks",
        "sideForwardClicks"
    ];
}

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
)
{
    /// <summary>该响应覆盖的业务日（yyyy-MM-dd）。客户端不得自行推算（契约语义 §1）。</summary>
    public string BusinessDate { get; init; } = string.Empty;

    /// <summary>业务日时区（IANA，恒为 Asia/Shanghai）。</summary>
    public string BusinessTimeZone { get; init; } = Pim.Core.Common.BusinessDateEnvelope.TimeZoneId;

    /// <summary>业务日起算小时（恒为 4）：业务日 D = [D 04:00, D+1 04:00)。</summary>
    public int BusinessDayStartHour { get; init; } = Pim.Core.Common.BusinessDateEnvelope.StartHour;
}

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
    Dictionary<string, int> KeyPressCounts,
    List<KeyCountItem> TopKeys
);

public record KeyCountItem(string KeyName, int Count, double Share);

/// <summary>
/// 热力图的小时 / 天桶（<c>summary.heatmap</c>；<c>aw/heatmap</c> 已于 WO-PC-BACKEND-20261001 REQ-6 下线）。
/// <para>
/// <see cref="IntensityLevel"/> 是「活跃时长占桶时长比例」的 0–5 档整数，
/// 上界见 <see cref="IntensityMax"/>（WO-PC-BACKEND-20260930 REQ-3，原字段名 <c>intensityScore</c>）。
/// </para>
/// </summary>
public record HeatmapBucket(
    string Start,
    string End,
    int Hour,
    int ActiveMinutes,
    int TotalEvents,
    int IntensityLevel,
    int IntensityMax = 5
);

/// <summary>
/// <c>heatmap/grid</c> 的单元格。
/// <para>
/// WO-PC-BACKEND-20260930 REQ-3：原 <c>intensityScore</c> 实际是键盘原始计数，
/// 已改名为 <see cref="KeyPressCount"/>（上界为响应级 <c>maxKeyCount</c>）；
/// 强度档位统一为 <see cref="IntensityLevel"/>（0–5，上界 <see cref="IntensityMax"/>），
/// 与 <c>summary.heatmap</c>、<c>activity-analysis</c> 同量纲。
/// </para>
/// <para>
/// WO-PC-BACKEND-20261001 REQ-7（#380）：新增 <see cref="BusinessDay"/>（<c>yyyy-MM-dd</c>）。
/// 业务日自本地 04:00 起算，因此一个业务日的最后 4 个小时桶（本地 00:00–03:59）
/// 的 <see cref="Start"/> 落在**次日** UTC 时刻 —— 消费方按 +08:00 日历日推断会把它们归错天。
/// 该字段对 hour / day / month / year 四个维度一律返回，取值与同业务日的 day 桶一致。
/// </para>
/// </summary>
public record HeatmapGridCell(
    string Start,
    string End,
    int Hour,
    int ActiveMinutes,
    int TotalEvents,
    int IntensityLevel,
    int IntensityMax,
    int KeyPressCount,
    string BusinessDay
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
    string? WindowTitle,
    string CategoryName,
    string CategoryColor,
    string? ProjectTag,
    double ClassificationConfidence,
    string ClassificationSource,
    string ClassificationExplanation
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
    int PageSize,
    string? Domain = null,
    string? Title = null,
    string? Url = null,
    string? View = null
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
    object? Raw,
    string? Url = null,
    string? Domain = null,
    string? Path = null,
    bool IsLocalFile = false,
    string? BrowserAppName = null,
    string? BrowserWindowTitle = null,
    bool? Audible = null,
    bool? Incognito = null,
    int? TabCount = null,
    int AbsorbedShortEventsCount = 0,
    double AbsorbedDurationSeconds = 0,
    List<long>? SourceWebEventIds = null,
    List<long>? SourceWindowEventIds = null,
    string? CategoryColor = null,
    string? ProjectTag = null,
    double? ClassificationConfidence = null,
    string? ClassificationSource = null,
    string? ClassificationExplanation = null,
    string? BucketType = null,
    string? RecordKey = null,
    string? RecordKeyVersion = null,
    string? RecordKeyStability = null,
    List<string>? SourceBucketIds = null,
    string? SourceType = null,
    string? InterpretationVersion = null
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

/// <summary>
/// <c>heatmap/grid</c> 响应。
/// <para>
/// <see cref="MaxKeyCount"/> 是单元格 <see cref="HeatmapGridCell.KeyPressCount"/> 的**上界**
/// （区间内单日最大按键数，无数据时为 1），供前端色阶归一化使用（REQ-3）。
/// </para>
/// </summary>
public record HeatmapGridResponse(
    List<List<HeatmapGridCell>> Grid,
    string Dimension,
    double MaxKeyCount
);

public record AwInfoDto(
    string? Hostname,
    string? Version,
    bool Testing,
    [property: JsonPropertyName("device_id")]
    string? DeviceId
);

public record AwBucketDto(
    string Id,
    string? Name,
    string Type,
    string Client,
    string Hostname,
    string? Created,
    [property: JsonPropertyName("last_updated")]
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
    // 与 KeystatsUploadRequest 保持一致：走默认 camelCase → peakKps / peakCps。
    // 原先这里用 JsonPropertyName 固定成 peakKPS/peakCPS，同一语义两个端点两种拼写（issue #419）。
    // 切换不需要客户端同批上线：请求体字段名大小写不敏感（JsonSerializerDefaults.Web），
    // 旧拼写照样收 —— 见 PcKeystatsUploadValidationE2ETests 的两条大小写用例。
    int PeakKps,
    int PeakCps,
    [property: JsonPropertyName("formattedMouseDistance")]
    string? FormattedMouseDistance,
    [property: JsonPropertyName("formattedScrollDistance")]
    string? FormattedScrollDistance,
    Dictionary<string, AppStatEntry>? AppStats
);
