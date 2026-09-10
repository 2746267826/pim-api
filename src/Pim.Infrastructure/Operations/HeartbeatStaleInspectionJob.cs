using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 心跳停滞检测看门狗后台任务：定期检查 Windows/其它客户端守护进程的心跳新鲜度。
/// 若心跳超过 10 分钟未更新且非计划离线状态，判定为停滞并写入系统审计日志与指标警报。
/// </summary>
public sealed class HeartbeatStaleInspectionJob
{
    public const double StaleThresholdMinutes = 10.0;
    private readonly PimDbContext _db;
    private readonly IAuditLogService _auditLogs;
    private readonly ILogger<HeartbeatStaleInspectionJob> _logger;
    private readonly TimeProvider _timeProvider;

    public HeartbeatStaleInspectionJob(
        PimDbContext db,
        IAuditLogService auditLogs,
        ILogger<HeartbeatStaleInspectionJob> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _auditLogs = auditLogs;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        _logger.LogInformation("Starting HeartbeatStaleInspectionJob watchdog at {Now}", now);

        List<Data.Entities.DaemonHeartbeatEntity> heartbeats;
        try
        {
            heartbeats = await _db.DaemonHeartbeats.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load daemon heartbeats during stale inspection");
            return 0;
        }

        if (heartbeats.Count == 0)
        {
            _logger.LogInformation("No daemon heartbeats found. Stale inspection completed with 0 issues.");
            return 0;
        }

        var latestPerDevice = heartbeats
            .GroupBy(h => h.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.ReceivedAt).First())
            .ToList();

        int staleCount = 0;

        foreach (var hb in latestPerDevice)
        {
            var elapsedSeconds = Math.Max(0, (now - hb.ReceivedAt).TotalSeconds);
            try
            {
                PimMetrics.DaemonHeartbeatFreshness.WithLabels(hb.DeviceId, hb.DaemonKind).Set(elapsedSeconds);
            }
            catch
            {
                // metrics export best-effort
            }

            var isPlannedOffline = hb.PlannedOfflineAt.HasValue && hb.PlannedOfflineAt.Value <= now;
            if (isPlannedOffline)
            {
                continue;
            }

            var elapsedMinutes = elapsedSeconds / 60.0;
            if (elapsedMinutes > StaleThresholdMinutes)
            {
                staleCount++;
                _logger.LogWarning(
                    "Daemon heartbeat stale alert: Device {DeviceId} ({DaemonKind}) has not reported for {ElapsedMinutes:F1} min (threshold: {Threshold} min)",
                    hb.DeviceId,
                    hb.DaemonKind,
                    elapsedMinutes,
                    StaleThresholdMinutes);

                try
                {
                    PimMetrics.DataQualityIssuesTotal.WithLabels("heartbeat_stale", "warning").Inc();
                }
                catch { }

                try
                {
                    await _auditLogs.RecordAsync(new CreateAuditLogRequest(
                        UserId: null,
                        ActorType: AuditActorType.System,
                        Action: "HeartbeatStaleWatchdog",
                        ResourceType: "DaemonHeartbeat",
                        ResourceId: hb.DeviceId,
                        Source: "HeartbeatStaleInspectionJob",
                        Result: AuditResult.Failure,
                        IpAddress: null,
                        UserAgent: null,
                        CorrelationId: Guid.NewGuid().ToString("N"),
                        Metadata: new Dictionary<string, string>
                        {
                            ["deviceId"] = hb.DeviceId,
                            ["daemonKind"] = hb.DaemonKind,
                            ["lastReceivedAt"] = hb.ReceivedAt.ToString("O"),
                            ["staleMinutes"] = elapsedMinutes.ToString("F1"),
                            ["version"] = hb.Version
                        },
                        ErrorCode: 504,
                        ErrorMessage: $"Device heartbeat stale for {elapsedMinutes:F1} minutes (last: {hb.ReceivedAt:u})"),
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to record audit log for stale heartbeat device {DeviceId}", hb.DeviceId);
                }
            }
        }

        _logger.LogInformation(
            "HeartbeatStaleInspectionJob completed. Inspected {Total} devices, detected {StaleCount} stale heartbeats.",
            latestPerDevice.Count,
            staleCount);

        return staleCount;
    }
}
