using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;

namespace Pim.Infrastructure.Operations;

public sealed class HeartbeatFreshnessInspector : IDataQualityInspector
{
    private const double StaleThresholdSeconds = 600.0; // 10 minutes
    private readonly PimDbContext _db;
    private readonly ILogger<HeartbeatFreshnessInspector> _logger;

    public HeartbeatFreshnessInspector(PimDbContext db, ILogger<HeartbeatFreshnessInspector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string CheckName => "heartbeat";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        List<Data.Entities.DaemonHeartbeatEntity> heartbeats;
        try
        {
            heartbeats = await _db.DaemonHeartbeats.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query daemon heartbeats for inspection");
            return new DataQualityInspectionResult(
                CheckName,
                false,
                1,
                $"Database error querying heartbeats: {ex.Message}");
        }

        if (heartbeats.Count == 0)
        {
            return new DataQualityInspectionResult(
                CheckName,
                true,
                0,
                "No registered daemon devices found.");
        }

        var latestPerDevice = heartbeats
            .GroupBy(h => h.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.ReceivedAt).First())
            .ToList();

        int staleCount = 0;
        var details = new Dictionary<string, string>();

        foreach (var hb in latestPerDevice)
        {
            var freshnessSeconds = Math.Max(0, (now - hb.ReceivedAt).TotalSeconds);
            try
            {
                PimMetrics.DaemonHeartbeatFreshness.WithLabels(hb.DeviceId, hb.DaemonKind).Set(freshnessSeconds);
            }
            catch
            {
                // metrics export best-effort
            }

            // 声明必须比最近心跳更新才算"此刻处于计划内离线"（见 DaemonLifecycleClassifier.IsCurrentlyPlannedOffline）。
            // 心跳不再清空 planned 标记，若沿用"非空即计划内离线"，任何声明过一次的设备都会永久豁免陈旧告警。
            var isPlannedOffline = DaemonLifecycleClassifier.IsCurrentlyPlannedOffline(hb);
            if (!isPlannedOffline && freshnessSeconds > StaleThresholdSeconds)
            {
                staleCount++;
                details[hb.DeviceId] = $"Stale for {freshnessSeconds / 60.0:F1} min (last received: {hb.ReceivedAt:u})";
            }
            else
            {
                details[hb.DeviceId] = isPlannedOffline ? "PlannedOffline" : $"Fresh ({freshnessSeconds:F0}s ago)";
            }
        }

        var isHealthy = staleCount == 0;
        var message = isHealthy
            ? $"All {latestPerDevice.Count} device heartbeat(s) are fresh or planned offline."
            : $"{staleCount} out of {latestPerDevice.Count} device(s) have stale heartbeats (>10 min).";

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            staleCount,
            message,
            details);
    }
}
