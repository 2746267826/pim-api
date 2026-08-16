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
    public static readonly TimeSpan OnlineDaemonAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DegradedDaemonAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AbnormalDaemonAge = TimeSpan.FromMinutes(15);

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

        var planned = heartbeat.PlannedOfflineAt is not null
            && heartbeat.PlannedOfflineAt >= heartbeat.ReceivedAt;
        if (planned)
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