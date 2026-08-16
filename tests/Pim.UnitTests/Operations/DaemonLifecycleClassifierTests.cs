using Pim.Core.Operations;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class DaemonLifecycleClassifierTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static DaemonHeartbeatEntity Heartbeat(
        DateTimeOffset? receivedAt = null,
        DateTimeOffset? plannedAt = null,
        string? reason = null)
        => new()
        {
            DeviceId = "pc-main",
            DaemonKind = "windows",
            ReceivedAt = receivedAt ?? FixedNow,
            PlannedOfflineAt = plannedAt,
            OfflineReason = reason
        };

    [Theory]
    // 4:59 / 5:00 / 14:59 / 15:00 边界
    [InlineData(-4.9, "online", "Healthy")]
    [InlineData(-5.0, "degraded", "Warning")]
    [InlineData(-14.9, "degraded", "Warning")]
    [InlineData(-15.0, "abnormal-offline", "Warning")]
    public void Classify_NoPlanned_ByAge(double minutesAgo, string state, string status)
    {
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddMinutes(minutesAgo));
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal(state, result.State);
        Assert.Equal(status, result.Status.ToString());
    }

    [Fact]
    public void Classify_Online_ReturnsHealthyAndMessage()
    {
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddMinutes(-3));
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal("online", result.State);
        Assert.Equal(PimHealthStatus.Healthy, result.Status);
        Assert.Equal("Windows 守护程序在线。", result.Message);
    }

    [Fact]
    public void Classify_Degraded_ReturnsWarningAndMessage()
    {
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddMinutes(-10));
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal("degraded", result.State);
        Assert.Equal(PimHealthStatus.Warning, result.Status);
        Assert.Equal("Windows 守护程序心跳偏旧。", result.Message);
    }

    [Fact]
    public void Classify_AbnormalOffline_ReturnsWarningAndMessage()
    {
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddMinutes(-20));
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal("abnormal-offline", result.State);
        Assert.Equal(PimHealthStatus.Warning, result.Status);
        Assert.Equal("Windows 守护程序连接异常（可能崩溃/断网）。", result.Message);
    }

    [Fact]
    public void Classify_PlannedOffline_BeatsAge()
    {
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddHours(-3), plannedAt: FixedNow.AddMinutes(-1), reason: "shutdown");
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal("planned-offline", result.State);
        Assert.Equal(PimHealthStatus.Healthy, result.Status);
        Assert.Equal("已关机/已休眠（正常）。", result.Message);
        Assert.Equal(FixedNow.AddMinutes(-1).ToString("O"), result.PlannedOfflineAt);
        Assert.Equal("shutdown", result.OfflineReason);
    }

    [Fact]
    public void Classify_StalePlanned_AfterNewerHeartbeat_TreatedByAge()
    {
        // planned_offline_at 早于最近心跳 → 计划离线已过期，回到年龄判定
        var heartbeat = Heartbeat(receivedAt: FixedNow.AddMinutes(-3), plannedAt: FixedNow.AddHours(-2));
        var result = DaemonLifecycleClassifier.Classify(heartbeat, FixedNow);
        Assert.Equal("online", result.State);
    }

    [Fact]
    public void Classify_NullHeartbeat_NeverConnected()
    {
        var result = DaemonLifecycleClassifier.Classify(null, FixedNow);
        Assert.Equal("never-connected", result.State);
        Assert.Equal(PimHealthStatus.Unknown, result.Status);
        Assert.Equal("尚未收到 Windows 守护程序心跳。", result.Message);
        Assert.Null(result.PlannedOfflineAt);
        Assert.Null(result.OfflineReason);
    }
}