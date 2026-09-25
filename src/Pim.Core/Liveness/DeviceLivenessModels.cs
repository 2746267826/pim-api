using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pim.Core.Liveness;

/// <summary>
/// 取证事件类型（工单 WO-ANDROID-KEEPALIVE-20260923 §7.1 的数据契约，命名由实现方定）。
/// 设备端与「设备存活」页、存活摘要、体检数据项共用同一批取值。
/// </summary>
public static class ForensicEventTypes
{
    /// <summary>进程退出原因台账（REQ-1）。</summary>
    public const string ProcessExit = "process-exit";

    /// <summary>强停 / 设备重启（REQ-2）。</summary>
    public const string ForceStop = "force-stop";

    /// <summary>存活心跳（REQ-3）。</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>闹钟兑现（REQ-18）：预定时刻、实际时刻、延迟、结果。</summary>
    public const string AlarmFulfillment = "alarm-fulfillment";

    /// <summary>闹钟登记（AC-14.1：已授权状态下台账出现闹钟登记事件）。</summary>
    public const string AlarmRegistered = "alarm-registered";

    /// <summary>保活健康事件（REQ-21 红点双通道的依据）。</summary>
    public const string KeepAliveHealth = "keepalive-health";
}

/// <summary>
/// 存活证据来源。静默判定以心搏序列为主、同步批次到达为辅（REQ-13）。
/// 「缺口回补窗口」是数据补传语义，**不是**存活证据（AC-13.3），因此这里没有也不能有它的取值。
/// </summary>
public static class LivenessEvidenceSources
{
    /// <summary>设备写入的存活心跳。</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>同步批次到达服务端的时刻（辅助证据）。</summary>
    public const string SyncBatch = "sync-batch";
}

/// <summary>静默时段标色（AC-7.4）：≥30 分钟警告、≥1 小时严重、&lt;30 分钟不标记。</summary>
public static class SilenceSeverities
{
    public const string None = "none";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>存活判定口径常量（REQ-13）。</summary>
public static class DeviceLivenessRules
{
    /// <summary>静默判定线：单次静默 ≥30 分钟开始标色（AC-7.4）。夜间与白天同一条线（AC-13.2）。</summary>
    public const int SilenceWarningMinutes = 30;

    /// <summary>单次静默 ≥1 小时标红，并置「存在 ≥1 小时静默」标记（AC-7.4 / AC-11.1）。</summary>
    public const int SilenceCriticalMinutes = 60;

    /// <summary>
    /// 「按应有心跳」覆盖率的分母节奏（分钟）：直接取既有周期同步节奏（<c>MobileSyncScheduler</c> 的
    /// 15 分钟周期工作），不新引入轮询与自拟档位（AC-29.1 / AC-10.3）。
    /// </summary>
    public const int ExpectedHeartbeatIntervalMinutes = 15;

    /// <summary>按小时覆盖率的定义原文（页面上要能读到，AC-13.1）。</summary>
    public const string CoverageByHourDefinition =
        "按小时覆盖率：区间内出现过存活证据（心搏，或同步批次到达）的整点小时数 ÷ 区间内的小时数。";

    /// <summary>按应有心跳覆盖率的定义原文（页面上要能读到，AC-13.1）。</summary>
    public const string CoverageByExpectedHeartbeatDefinition =
        "按应有心跳覆盖率：区间内心搏条数 ÷ 应有心跳条数（区间分钟数 ÷ 15 分钟，向上取整），上限 100%。";

    /// <summary>无存活证据时的结论文案（AC-7.5 / AC-8.2 / AC-10.2 / AC-11.4）。</summary>
    public const string NoDataConclusion = "无数据/未上报：该区间内没有任何存活证据。";
}

/// <summary>一条存活证据（时刻 + 来源）。</summary>
public sealed record LivenessEvidence(DateTimeOffset AtUtc, string Source);

/// <summary>一段静默（无任何存活证据的连续时段，已按区间裁剪）。</summary>
public sealed record SilenceWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double Minutes,
    string Severity);

/// <summary>死因分布中的一项。未知原因带推断依据（AC-7.1 要求「未知 + 推断依据」）。</summary>
public sealed record LivenessCauseCount(string Cause, string Label, int Count, string? Inference = null);

/// <summary>
/// 单设备指定区间的存活摘要（REQ-11 / REQ-13 的统一数据结构）。
/// 覆盖率同时给出两个口径，并暴露分子/分母，页面与摘要可据此复算（AC-13.1）。
/// </summary>
public sealed record DeviceLivenessSummary(
    bool HasData,
    string Conclusion,
    double? CoverageByHour,
    double? CoverageByExpectedHeartbeat,
    int ObservedHours,
    int TotalHours,
    int ObservedHeartbeats,
    int ExpectedHeartbeats,
    int ExpectedHeartbeatIntervalMinutes,
    int LongestSilenceMinutes,
    DateTimeOffset? LongestSilenceStartUtc,
    DateTimeOffset? LongestSilenceEndUtc,
    string LongestSilenceSeverity,
    bool HasSilenceOverOneHour,
    IReadOnlyList<SilenceWindow> Silences,
    IReadOnlyList<LivenessCauseCount> Causes,
    DateTimeOffset? LastEventAtUtc,
    string CoverageByHourDefinition,
    string CoverageByExpectedHeartbeatDefinition);

/// <summary>体检输出中的「设备存活」数据项（REQ-10）。**刻意不含红/黄/绿档位字段**（AC-10.3）。</summary>
public sealed record DeviceLivenessInspectionItem(
    string DeviceId,
    string DisplayName,
    string DeviceKind,
    DeviceLivenessSummary Summary);

/// <summary>
/// 「设备存活」数据项提供方：由 Mobile 模块实现，体检服务经 DI 可选消费。
/// 体检不因缺该实现而失败，只在该项缺席时给出明确的空态（AC-10.2）。
/// </summary>
public interface IDeviceLivenessInspectionProvider
{
    Task<IReadOnlyList<DeviceLivenessInspectionItem>> GetLivenessForInspectionAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default);
}
