using System;
using System.Collections.Generic;

namespace Pim.Core.Invariants;

/// <summary>
/// S1 (INV-P16): 事件时间区间模型
/// </summary>
public sealed class EventTimeSpan
{
    public string EventId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

/// <summary>
/// S2 (INV-P17): 超长事件活动证据模型（三态判定输入）
/// </summary>
public sealed class LongEventCandidate
{
    public string? EventId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long Keystrokes { get; set; }
    public long MouseClicks { get; set; }
    public bool IsMediaActive { get; set; }
    public bool IsAudible { get; set; }
    public bool IsGapOrOffline { get; set; }
    public string? AppName { get; set; }
}

/// <summary>
/// S3 (INV-P18): 原始活动事件模型（用于聚合计算前的三态过滤与重叠区间合并）
/// </summary>
public sealed class RawActivityEvent
{
    public string DeviceId { get; set; } = string.Empty;
    public string BusinessDate { get; set; } = string.Empty; // YYYY-MM-DD
    public DateTime Timestamp { get; set; }
    public double DurationSeconds { get; set; }
    public string EventType { get; set; } = string.Empty; // window, web-page, idle, gap, etc.
    public bool IsIdle { get; set; }
    public bool IsMediaActive { get; set; }
    public bool Audible { get; set; }
    public double InputDensityPerMinute { get; set; } = 0.0;
    public string? AppName { get; set; }
    public string? EventId { get; set; }
}

/// <summary>
/// S3 (INV-P18): 单日活跃时长记录（仅包含操作活跃与观看活跃时长，经过区间合并去重与三态过滤）
/// </summary>
public sealed class DailyActiveDuration
{
    public string Date { get; set; } = string.Empty; // YYYY-MM-DD
    public string DeviceId { get; set; } = string.Empty;
    public double ActiveDurationSeconds { get; set; }
    public double MergedActiveSeconds { get; set; }
    public double OverlapRemovedSeconds { get; set; }
    public double IdleSeconds { get; set; }
    public double GapSeconds { get; set; }
    public double SuspectedUnclosedSeconds { get; set; }
}

/// <summary>
/// S4 (INV-C18): 业务去重键记录模型
/// </summary>
public sealed class BusinessRecordKey
{
    public string Domain { get; set; } = string.Empty; // Location, Mobile, Pc
    public string DeviceId { get; set; } = string.Empty;
    public string UniqueKey { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }

    public static BusinessRecordKey ForLocation(string deviceId, DateTime timestamp, double lat, double lon) =>
        new()
        {
            Domain = "Location",
            DeviceId = deviceId,
            UniqueKey = $"{timestamp:O}:{lat:F6}:{lon:F6}",
            Timestamp = timestamp
        };

    /// <summary>
    /// 手机端业务键。维度必须与库层唯一约束**一致**
    /// （<c>IX_mobile_usage_events_user_id_device_id_package_name_event_ty...</c>：
    /// <c>(user_id, device_id, package_name, event_type, event_timestamp_utc, class_name)</c>）。
    ///
    /// WO-ISSUES-396-400-20261007 REQ-3 / issue #399：旧键只有 <c>(package, timestamp, eventType)</c>，
    /// 漏掉 <c>class_name</c> 与 <c>user_id</c>，于是"同一毫秒切换的两个 Activity"被判成重复 ——
    /// 生产 219 条 S4 违规全部来自这一类（200 组 / 419 行，200/200 组的 class_name 互不相同）。
    /// 同 <c>user_id</c> 且同 <c>class_name</c> 的真实重复仍然会被这条键判出。
    /// </summary>
    public static BusinessRecordKey ForMobile(string deviceId, string packageName, DateTime timestamp, string eventType, string? className, string? userId) =>
        new()
        {
            Domain = "Mobile",
            DeviceId = deviceId,
            UniqueKey = BuildMobileUniqueKey(deviceId, packageName, timestamp, eventType, className, userId),
            Timestamp = timestamp
        };

    /// <summary>
    /// 手机端业务键的拼装：维度与库层唯一索引一致，且**不会因为字段内容含分隔符而撞键**。
    ///
    /// 不能直接用 <c>":"</c> 拼接：<c>timestamp:O</c> 本身含冒号，device / package / class_name
    /// 又都是自由文本 —— 例如 (device="D:P", package="PKG") 与 (device="D", package="P:PKG")
    /// 拼出来完全相同，两个互不相干的重复组会被并成一组，重复条数多算一条。
    /// 这里改用**长度前缀**编码：每个字段编码成 <c>{长度}:{值}</c>，空串是 <c>0:</c>，
    /// NULL 单独编码成 <c>!</c>（长度前缀一定以数字开头，因此 <c>!</c> 与任何值编码都不冲突）。
    ///
    /// NULL 必须与空串区分开：库层唯一索引里多个 NULL 互不相等、而空串互相相等，两者不是同一语义。
    /// </summary>
    private static string BuildMobileUniqueKey(
        string deviceId,
        string packageName,
        DateTime timestamp,
        string eventType,
        string? className,
        string? userId)
        => string.Concat(
            EncodePart(userId),
            EncodePart(deviceId),
            EncodePart(packageName),
            EncodePart(eventType),
            EncodePart(timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            EncodePart(className));

    private static string EncodePart(string? value)
        => value is null ? "!" : $"{value.Length}:{value}";

    public static BusinessRecordKey ForPc(string deviceId, DateTime timestamp, double duration, string eventType, string? appName, string? browser, string? instanceId) =>
        new()
        {
            Domain = "Pc",
            DeviceId = deviceId,
            UniqueKey = $"{timestamp:O}:{duration}:{eventType}:{appName}:{browser}:{instanceId}",
            Timestamp = timestamp
        };
}

/// <summary>
/// S5 (INV-P19): 事件时钟校验模型
/// </summary>
public sealed class ClockEventItem
{
    public string EventId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public DateTime EventTime { get; set; }
    public DateTime ServerReceivedTime { get; set; }
}

/// <summary>
/// S6 (INV-P20): 设备下线声明与上传滞后采样
/// </summary>
/// <summary>
/// 设备"我下线了"的自我声明。
///
/// 语义有两种形态，判据必须都支持：
///   * **区间声明**：客户端明确给出离线的起止（如计划离线窗口），Start &lt; End；
///   * **时点声明**：客户端只在上报里带了一个"我正要下线"的时刻（心跳/退出钩子），
///     此时 Start == End，表示"设备在该时刻声明即将停止出数"。
///
/// 判据用 StartTime 落在待解释空档附近来认定覆盖，**不要求 EndTime 延伸到空档末尾**
/// （时点声明没有终止信息）；但也不得把一次声明当成"此后永久离线"——
/// 实测有一次 exit 声明 7 秒后设备就恢复了，若按永久处理会掩盖之后所有真实断档。
/// </summary>
public sealed class OfflineDeclaration
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Reason { get; set; } = string.Empty; // sleep, shutdown, planned_offline
}

public sealed class UploadLagSample
{
    /// <summary>
    /// 该条事件**可以上传的时刻** —— 事件区间结束时刻（切片写完后才可能被上传）。
    ///
    /// S6 的上传滞后必须以此为准（WO-ISSUES-396-400-20261007 REQ-2 / issue #397）：
    /// 以区间**起点**为基准会把"切片自身的采集时长"算成链路延迟，
    /// 30 分钟切片因此必然贴线（生产实测 p99 = 30.0 分钟，而命中样本在区间结束后 22 秒就完成了上传）。
    /// </summary>
    public DateTime UploadableAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 该样本是否为**系统合成的"缺数据"标记**（gap/离线补报）而不是真实采集事件。
    /// 合成 gap 事件的 timestamp 是断档起点、created_at 是重启后补传时刻，
    /// 两者之差恒等于断档时长，**不代表上传链路延迟**，必须排除出 S6 的滞后统计
    /// （实测：含 gap 时 p99 = 425.9 分钟，排除后 p99 = 19.2 分钟）。
    /// </summary>
    public bool IsSyntheticGap { get; set; }
}

public sealed class DeviceActivityTrace
{
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 该设备产生的**事件区间**（起点 + 时长）。空档判定按"上一段结束 → 下一段开始"计算，
    /// 而不是"起点减起点" —— 判据说的是"设备**停止出数**必须自己有交代"，
    /// 停止出数发生在事件结束时刻，不是下一条事件的起点。
    /// </summary>
    public IReadOnlyList<(DateTime StartTime, DateTime EndTime)> EventIntervals { get; set; }
        = Array.Empty<(DateTime, DateTime)>();

    public IReadOnlyList<OfflineDeclaration> Declarations { get; set; } = Array.Empty<OfflineDeclaration>();

    /// <summary>
    /// 上传滞后采样。系统合成的 gap 事件必须标记 <see cref="UploadLagSample.IsSyntheticGap"/>，
    /// 否则会把"断档时长"误当成"链路延迟"计入 p99。
    /// </summary>
    public IReadOnlyList<UploadLagSample> UploadLagSamples { get; set; } = Array.Empty<UploadLagSample>();
}

/// <summary>
/// S7 (INV-P21): 时间轴断档检验区间
/// </summary>
public sealed class TimelineInterval
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsGap { get; set; }
    public string? EventType { get; set; }
}

/// <summary>
/// S8 (INV-C19): 日界三层样本（支持仅数据字段层、或三层完整验证）
/// </summary>
public sealed class DayBoundarySample
{
    public DateTime EventTimeUtc { get; set; }
    public string DataFieldDateBucket { get; set; } = string.Empty; // 数据字段的日期桶 (如 pc_tracker_events.date)
    public string? QueryWindowDate { get; set; }                   // 按日接口的查询窗口 (若未覆盖可为 null)
    public string? PageDisplayDate { get; set; }                   // 页面展示的业务日 (若未覆盖可为 null)
    public string? EventId { get; set; }
    public string? TableName { get; set; }
}

/// <summary>
/// S9 (INV-C20): 覆盖率信号检查模型
/// </summary>
public sealed class CoverageSignalReport
{
    public string DeviceId { get; set; } = string.Empty;
    public double OnlineDurationSeconds { get; set; }
    public double ValidDataDurationSeconds { get; set; }
    public string ReportedStatus { get; set; } = string.Empty; // Normal, Healthy, Warning, Error
    public bool IsDataInsufficientForDenominator { get; set; } = false;
    public string? DenominatorBasisNote { get; set; }
    public IReadOnlyList<string> GapBreakdown { get; set; } = Array.Empty<string>();
}

/// <summary>
/// S10 (INV-C21): 后台任务运行记录
/// </summary>
public sealed class BackgroundTaskRun
{
    public string TaskName { get; set; } = string.Empty;
    public DateTime ExecutedAt { get; set; }
    public int ProcessedCount { get; set; }
    public int OutputCount { get; set; }
    public int AvailableDataCount { get; set; }
}

/// <summary>
/// S11 (INV-M21): 批次同步状态记录
/// </summary>
public sealed class BatchSyncStatusRecord
{
    public string BatchId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AcceptedCount { get; set; }
    public int FailedCount { get; set; }
    public int RejectedCount { get; set; }
    /// <summary>重复/无需处理而被跳过的条目数（#243）；"只含跳过条目"的批次不是空转批次。</summary>
    public int SkippedCount { get; set; }
    /// <summary>批次窗口起点（业务时间，UTC）。考核线分档以此为界；缺省 MinValue 一律视为历史欠账。</summary>
    public DateTime WindowStartUtc { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// S12 (INV-M22): 派生表状态
/// </summary>
public sealed class DerivedTableStatus
{
    public string TableName { get; set; } = string.Empty;
    public int SourceDataCountLast24H { get; set; }
    public int DerivedRowCount { get; set; }
    public bool IsExplicitOnlineCalculation { get; set; }
    public string? DocumentationNote { get; set; }
}

/// <summary>
/// S13 (INV-P22): 采集心跳/事件。
/// <para>
/// <see cref="Timestamp"/> + <see cref="DurationSeconds"/> 描述该实例在采集流中**占用**的时间区间；
/// 判据按区间是否真实重叠来判断"多实例并发采集"。若 <see cref="DurationSeconds"/> 为 0
/// （旧调用方只提供瞬时心跳），判据退化为按时刻先后判断交接是否重叠 —— 见
/// <see cref="DataReliabilityInvariants.CheckS13_SingleInstance"/>。
/// </para>
/// </summary>
public sealed class CollectionHeartbeat
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long SessionId { get; set; }
    public double PhaseOffsetSeconds { get; set; }
    public string? InstanceId { get; set; }

    /// <summary>
    /// 该心跳事件覆盖的时长（秒）。用于判定不同实例的采集区间是否真实重叠：
    /// 只有重叠才构成"多实例并发采集"；提前退出、下一个实例立刻接管属于正常交接。
    /// 0 表示未提供时长（按瞬时点处理）。
    /// </summary>
    public double DurationSeconds { get; set; }
}
