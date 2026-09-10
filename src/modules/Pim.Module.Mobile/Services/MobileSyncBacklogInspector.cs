using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileSyncBacklogInspector : IDataQualityInspector
{
    private static readonly TimeSpan OverdueThreshold = TimeSpan.FromMinutes(30);
    private static readonly string[] ActiveStatuses = ["pending", "processing", "syncing"];

    private readonly PimDbContext _db;
    private readonly ILogger<MobileSyncBacklogInspector> _logger;

    public MobileSyncBacklogInspector(PimDbContext db, ILogger<MobileSyncBacklogInspector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string CheckName => "sync_backlog";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var overdueCutoff = now - OverdueThreshold;
        List<MobileSyncBatchEntity> overdueBatches;

        try
        {
            overdueBatches = await _db.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .Where(b => ActiveStatuses.Contains(b.Status) && b.CreatedAt <= overdueCutoff)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query mobile sync batches for backlog inspection");
            return new DataQualityInspectionResult(
                CheckName,
                false,
                1,
                $"Database error querying mobile sync batches: {ex.Message}");
        }

        var backlogCount = overdueBatches.Count;
        try
        {
            PimMetrics.SyncBatchBacklog.Set(backlogCount);
        }
        catch { }

        var isHealthy = backlogCount == 0;
        var message = isHealthy
            ? "No overdue mobile sync batches found."
            : $"{backlogCount} mobile sync batch(es) overdue (>30 minutes in progress/pending).";

        var details = new Dictionary<string, string>
        {
            ["overdueCount"] = backlogCount.ToString(),
            ["thresholdMinutes"] = OverdueThreshold.TotalMinutes.ToString()
        };

        if (overdueBatches.Count > 0)
        {
            var oldest = overdueBatches.OrderBy(b => b.CreatedAt).First();
            details["oldestBatchId"] = oldest.BatchId;
            details["oldestBatchCreatedAt"] = oldest.CreatedAt.ToString("O");
            details["oldestBatchDeviceId"] = oldest.DeviceId;
        }

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            backlogCount,
            message,
            details);
    }
}
