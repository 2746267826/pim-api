using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

/// <summary>
/// MetricsRefreshService：把心跳新鲜度与 Hangfire 队列状态刷入 Prometheus 指标。
/// </summary>
public class MetricsRefreshServiceTests
{
    private sealed class FakeMonitoringClient(HangfireMonitoringSnapshot snapshot) : IHangfireMonitoringClient
    {
        public HangfireMonitoringSnapshot GetSnapshot() => snapshot;
    }

    private static MetricsRefreshService NewService(PimDbContext db, IHangfireMonitoringClient monitoring)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(monitoring);
        var provider = services.BuildServiceProvider();
        return new MetricsRefreshService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MetricsRefreshService>.Instance);
    }

    [Fact]
    public async Task RefreshOnce_SetsHeartbeatFreshnessGauge()
    {
        await using var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid()}").Options);
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "dev-1",
            DaemonKind = "windows",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "dev-2",
            DaemonKind = "android",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, new FakeMonitoringClient(new HangfireMonitoringSnapshot(1, 2, 3, 4)));
        await svc.RefreshOnceAsync(CancellationToken.None);

        var dev1 = PimMetrics.DaemonHeartbeatFreshness.WithLabels("dev-1", "windows").Value;
        var dev2 = PimMetrics.DaemonHeartbeatFreshness.WithLabels("dev-2", "android").Value;
        Assert.InRange(dev1, 290, 310);
        Assert.InRange(dev2, 1190, 1210);
    }

    [Fact]
    public async Task RefreshOnce_SetsHangfireGauges()
    {
        await using var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid()}").Options);
        var svc = NewService(db, new FakeMonitoringClient(new HangfireMonitoringSnapshot(
            Processing: 3, Enqueued: 42, Scheduled: 7, Failed: 2)));

        await svc.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(3, PimMetrics.HangfireJobs.WithLabels("processing").Value);
        Assert.Equal(42, PimMetrics.HangfireJobs.WithLabels("enqueued").Value);
        Assert.Equal(7, PimMetrics.HangfireJobs.WithLabels("scheduled").Value);
        Assert.Equal(2, PimMetrics.HangfireJobs.WithLabels("failed").Value);
    }

    [Fact]
    public async Task RefreshOnce_HangfireSnapshotThrows_DoesNotPropagate()
    {
        await using var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid()}").Options);
        var svc = NewService(db, new ThrowingMonitoringClient());

        // 不应抛出：单轮失败只记日志
        await svc.RefreshOnceAsync(CancellationToken.None);
    }

    private sealed class ThrowingMonitoringClient : IHangfireMonitoringClient
    {
        public HangfireMonitoringSnapshot GetSnapshot() => throw new InvalidOperationException("storage down");
    }
}
