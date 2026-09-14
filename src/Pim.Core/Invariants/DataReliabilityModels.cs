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
}

public sealed class DeviceActivityTrace
{
    public string DeviceId { get; set; } = string.Empty;
    public IReadOnlyList<DateTime> EventTimes { get; set; } = Array.Empty<DateTime>();
    public IReadOnlyList<OfflineDeclaration> Declarations { get; set; } = Array.Empty<OfflineDeclaration>();
    public IReadOnlyList<(DateTime EventTime, DateTime CreatedAt)> UploadLagSamples { get; set; } = Array.Empty<(DateTime, DateTime)>();
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
/// S13 (INV-P22): 采集心跳/事件
/// </summary>
public sealed class CollectionHeartbeat
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long SessionId { get; set; }
    public double PhaseOffsetSeconds { get; set; }
    public string? InstanceId { get; set; }
}
