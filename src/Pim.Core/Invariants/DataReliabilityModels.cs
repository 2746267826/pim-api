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

    public static BusinessRecordKey ForMobile(string deviceId, string packageName, DateTime timestamp, string eventType) =>
        new()
        {
            Domain = "Mobile",
            DeviceId = deviceId,
            UniqueKey = $"{packageName}:{timestamp:O}:{eventType}",
            Timestamp = timestamp
        };

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
public sealed class OfflineDeclaration
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Reason { get; set; } = string.Empty; // sleep, shutdown, planned_offline
}

public sealed class UploadLagSample
{
    public DateTime EventTime { get; set; }
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
    /// <summary>批次窗口起点（业务时间，UTC）。T4 新增/存量分档以此为界；缺省 MinValue 一律视为存量。</summary>
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
