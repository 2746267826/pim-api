using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;

namespace Pim.Infrastructure.Metrics;

/// <summary>
/// 周期性把数据库 / Hangfire 状态刷入 Prometheus 指标：
/// - 守护进程心跳新鲜度（pim_daemon_heartbeat_freshness_seconds）
/// - Hangfire 队列状态（pim_hangfire_jobs）
/// 每 30 秒一轮；单轮失败只记日志，不影响宿主。
/// </summary>
public sealed class MetricsRefreshService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetricsRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动稍作延迟，避开宿主初始化与迁移窗口
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metrics refresh iteration failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var hangfire = scope.ServiceProvider.GetRequiredService<IHangfireMonitoringClient>();

        var now = DateTimeOffset.UtcNow;
        var heartbeats = await db.DaemonHeartbeats
            .GroupBy(d => new { d.DeviceId, d.DaemonKind })
            .Select(g => new { g.Key.DeviceId, g.Key.DaemonKind, Last = g.Max(d => d.ReceivedAt) })
            .ToListAsync(ct);
        foreach (var hb in heartbeats)
        {
            PimMetrics.DaemonHeartbeatFreshness
                .WithLabels(hb.DeviceId, hb.DaemonKind)
                .Set(Math.Max(0, (now - hb.Last).TotalSeconds));
        }

        try
        {
            var snapshot = hangfire.GetSnapshot();
            PimMetrics.HangfireJobs.WithLabels("processing").Set(snapshot.Processing);
            PimMetrics.HangfireJobs.WithLabels("enqueued").Set(snapshot.Enqueued);
            PimMetrics.HangfireJobs.WithLabels("scheduled").Set(snapshot.Scheduled);
            PimMetrics.HangfireJobs.WithLabels("failed").Set(snapshot.Failed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Hangfire snapshot unavailable for metrics");
        }
    }
}
