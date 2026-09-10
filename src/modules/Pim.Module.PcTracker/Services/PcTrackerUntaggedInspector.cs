using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class PcTrackerUntaggedInspector : IDataQualityInspector
{
    private const int WarningBacklogThreshold = 50;
    private readonly PimDbContext _db;
    private readonly ILogger<PcTrackerUntaggedInspector> _logger;

    public PcTrackerUntaggedInspector(PimDbContext db, ILogger<PcTrackerUntaggedInspector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string CheckName => "untagged_queue";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        int pendingCount;
        try
        {
            pendingCount = await _db.Set<ActivityClassificationSuggestionEntity>()
                .AsNoTracking()
                .CountAsync(s => s.Status == "pending", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query activity classification suggestions for untagged backlog inspection");
            return new DataQualityInspectionResult(
                CheckName,
                false,
                1,
                $"Database error querying untagged classification suggestions: {ex.Message}");
        }

        try
        {
            PimMetrics.UntaggedRecordsBacklog.Set(pendingCount);
        }
        catch { }

        var isHealthy = pendingCount < WarningBacklogThreshold;
        var message = isHealthy
            ? $"Untagged classification queue is within threshold ({pendingCount} pending, limit: {WarningBacklogThreshold})."
            : $"Untagged classification queue is backlogged ({pendingCount} pending > threshold {WarningBacklogThreshold}).";

        var details = new Dictionary<string, string>
        {
            ["pendingCount"] = pendingCount.ToString(),
            ["warningThreshold"] = WarningBacklogThreshold.ToString()
        };

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            isHealthy ? 0 : pendingCount,
            message,
            details);
    }
}
