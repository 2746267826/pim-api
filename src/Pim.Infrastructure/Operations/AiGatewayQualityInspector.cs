using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;

namespace Pim.Infrastructure.Operations;

public sealed class AiGatewayQualityInspector : IDataQualityInspector
{
    private const double MaxAcceptableErrorRate = 0.10; // 10%
    private const int MinRequestsForEvaluation = 5;
    private readonly PimDbContext _db;
    private readonly ILogger<AiGatewayQualityInspector> _logger;

    public AiGatewayQualityInspector(PimDbContext db, ILogger<AiGatewayQualityInspector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string CheckName => "ai_error_rate";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var windowStart = now.AddHours(-24);
        List<Data.Entities.AiRequestLogEntity> logs;
        try
        {
            logs = await _db.AiRequestLogs
                .AsNoTracking()
                .Where(l => l.StartedAt >= windowStart)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query AI request logs for inspection");
            return new DataQualityInspectionResult(
                CheckName,
                false,
                1,
                $"Database error querying AI request logs: {ex.Message}");
        }

        var total = logs.Count;
        if (total == 0)
        {
            try
            {
                PimMetrics.AiGatewayErrorRate24h.Set(0.0);
            }
            catch { }

            return new DataQualityInspectionResult(
                CheckName,
                true,
                0,
                "No AI requests in the last 24 hours.",
                new Dictionary<string, string> { ["totalRequests24h"] = "0", ["errorRate"] = "0%" });
        }

        var failed = logs.Count(l =>
            !string.Equals(l.Status, "Success", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(l.ErrorCode));

        var errorRate = (double)failed / total;
        try
        {
            PimMetrics.AiGatewayErrorRate24h.Set(errorRate);
        }
        catch { }

        var details = new Dictionary<string, string>
        {
            ["totalRequests24h"] = total.ToString(),
            ["failedRequests24h"] = failed.ToString(),
            ["errorRate"] = $"{errorRate * 100:F1}%"
        };

        var isElevated = total >= MinRequestsForEvaluation && errorRate > MaxAcceptableErrorRate;
        var isHealthy = !isElevated;

        var message = isHealthy
            ? $"AI gateway 24h error rate is normal ({errorRate * 100:F1}%, {failed}/{total})."
            : $"AI gateway 24h error rate is elevated: {errorRate * 100:F1}% ({failed}/{total} failed).";

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            isHealthy ? 0 : failed,
            message,
            details);
    }
}
