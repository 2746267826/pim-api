using Pim.Core.Operations;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

/// <summary>Windows 守护程序生命周期四态判定（含未接入态）。</summary>
public sealed record DaemonLifecycleState(
    string State,
    PimHealthStatus Status,
    string Message,
    string? PlannedOfflineAt,
    string? OfflineReason);

/// <summary>共享静态分类器：按心跳新鲜度 + planned 标记判定守护程序生命周期状态。</summary>
public static class DaemonLifecycleClassifier
{
    // degraded 区间用 OnlineDaemonAge/AbnormalDaemonAge 界定，无独立退化年龄常量。
    public static readonly TimeSpan OnlineDaemonAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AbnormalDaemonAge = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 该设备**此刻**是否处于"已声明的计划内离线"。
    ///
    /// 判据 = <c>planned_offline_at &gt;= received_at</c>：声明必须比最近一次普通心跳更"新"，
    /// 也就是说声明之后设备还没有报过到。等价说法：声明是设备留下的**最近一个**信号。
    ///
    /// 为什么必须有这条时效约束（WO-ISSUES-396-400-20261007 REQ-4 / #398）：
    /// 普通心跳不再清空 planned_offline_at（否则唤醒后声明会被吃掉，整段休眠被 S6 判成无声明空档），
    /// 声明因此会长期留在行里。若仍按"非空即计划内离线"处理，任何声明过一次的设备
    /// 都会永久被当成"正常离线"——心跳陈旧告警与尾部断档报告会被静默永久抑制。
    /// 设备唤醒后 <c>received_at</c> 必然晚于声明时刻，这里自动回到"不在计划内离线"。
    /// </summary>
    public static bool IsCurrentlyPlannedOffline(DaemonHeartbeatEntity? heartbeat)
        => heartbeat is not null
           && heartbeat.PlannedOfflineAt is { } planned
           && planned >= heartbeat.ReceivedAt;

    public static DaemonLifecycleState Classify(DaemonHeartbeatEntity? heartbeat, DateTimeOffset checkedAt)
    {
        if (heartbeat is null)
        {
            return new DaemonLifecycleState(
                "never-connected",
                PimHealthStatus.Unknown,
                "尚未收到 Windows 守护程序心跳。",
                null,
                null);
        }

        if (IsCurrentlyPlannedOffline(heartbeat))
        {
            return new DaemonLifecycleState(
                "planned-offline",
                PimHealthStatus.Healthy,
                "已关机/已休眠（正常）。",
                heartbeat.PlannedOfflineAt?.ToString("O"),
                heartbeat.OfflineReason);
        }

        var age = checkedAt - heartbeat.ReceivedAt;
        if (age < OnlineDaemonAge)
        {
            return new DaemonLifecycleState(
                "online",
                PimHealthStatus.Healthy,
                "Windows 守护程序在线。",
                null,
                null);
        }

        if (age < AbnormalDaemonAge)
        {
            return new DaemonLifecycleState(
                "degraded",
                PimHealthStatus.Warning,
                "Windows 守护程序心跳偏旧。",
                null,
                null);
        }

        return new DaemonLifecycleState(
            "abnormal-offline",
            PimHealthStatus.Warning,
            "Windows 守护程序连接异常（可能崩溃/断网）。",
            null,
            null);
    }
}