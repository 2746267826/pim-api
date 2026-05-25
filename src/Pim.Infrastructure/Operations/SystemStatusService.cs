using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Operations;

public sealed class SystemStatusService : ISystemStatusService
{
    private static readonly TimeSpan WarningDaemonAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CriticalDaemonAge = TimeSpan.FromMinutes(60);

    private readonly PimDbContext _db;
    private readonly IBackgroundJobStatusService _backgroundJobs;

    public SystemStatusService(PimDbContext db, IBackgroundJobStatusService backgroundJobs)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
    }

    public async Task<SystemStatusSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(ct);
        return detail.Summary;
    }

    public async Task<SystemStatusDetailDto> GetDetailAsync(CancellationToken ct = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var components = new List<StatusComponentDto>
        {
            new(
                "api",
                "API",
                StatusComponentKind.Api,
                PimHealthStatus.Healthy,
                "API process is running.",
                checkedAt,
                new Dictionary<string, string>())
        };

        components.Add(await BuildDatabaseComponentAsync(checkedAt, ct));
        components.Add(await BuildWindowsDaemonComponentAsync(checkedAt, ct));
        components.Add(await BuildBackgroundJobsComponentAsync(ct));

        var status = components
            .OrderByDescending(c => GetSeverityRank(c.Status))
            .First()
            .Status;
        var summary = new SystemStatusSummaryDto(
            status,
            GetLabel(status),
            GetMessage(status),
            checkedAt);

        var nextSteps = components
            .Where(c => c.Status is PimHealthStatus.Warning or PimHealthStatus.Critical)
            .Select(c => c.Message)
            .ToList();

        return new SystemStatusDetailDto(summary, components, nextSteps);
    }

    private async Task<StatusComponentDto> BuildDatabaseComponentAsync(DateTimeOffset checkedAt, CancellationToken ct)
    {
        try
        {
            if (_db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
            {
                await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            }

            return new StatusComponentDto(
                "database",
                "Database",
                StatusComponentKind.Database,
                PimHealthStatus.Healthy,
                "Database is reachable.",
                checkedAt,
                new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            return new StatusComponentDto(
                "database",
                "Database",
                StatusComponentKind.Database,
                PimHealthStatus.Critical,
                "Database is unavailable.",
                checkedAt,
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
        }
    }

    private async Task<StatusComponentDto> BuildWindowsDaemonComponentAsync(DateTimeOffset checkedAt, CancellationToken ct)
    {
        var heartbeat = await _db.DaemonHeartbeats
            .AsNoTracking()
            .Where(d => d.DaemonKind == "windows")
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (heartbeat is null)
        {
            return new StatusComponentDto(
                "windows-daemon",
                "Windows daemon",
                StatusComponentKind.Daemon,
                PimHealthStatus.Unknown,
                "Windows daemon heartbeat has not been received.",
                checkedAt,
                new Dictionary<string, string>());
        }

        var age = checkedAt - heartbeat.ReceivedAt;
        var status = age >= CriticalDaemonAge
            ? PimHealthStatus.Critical
            : age >= WarningDaemonAge
                ? PimHealthStatus.Warning
                : PimHealthStatus.Healthy;

        var message = status switch
        {
            PimHealthStatus.Critical => "Windows daemon heartbeat is stale.",
            PimHealthStatus.Warning => "Windows daemon heartbeat is old.",
            _ => "Windows daemon heartbeat is recent."
        };

        return new StatusComponentDto(
            "windows-daemon",
            "Windows daemon",
            StatusComponentKind.Daemon,
            status,
            message,
            checkedAt,
            new Dictionary<string, string>
            {
                ["deviceId"] = heartbeat.DeviceId,
                ["version"] = heartbeat.Version,
                ["receivedAt"] = heartbeat.ReceivedAt.ToString("O"),
                ["activityWatch"] = heartbeat.ActivityWatchState,
                ["keyStats"] = heartbeat.KeyStatsState
            });
    }

    private async Task<StatusComponentDto> BuildBackgroundJobsComponentAsync(CancellationToken ct)
    {
        var summary = await _backgroundJobs.GetSummaryAsync(ct);

        return new StatusComponentDto(
            "background-jobs",
            "Background jobs",
            StatusComponentKind.BackgroundJobs,
            summary.Status,
            summary.Message,
            summary.CheckedAt,
            new Dictionary<string, string>
            {
                ["processing"] = summary.Processing.ToString(),
                ["enqueued"] = summary.Enqueued.ToString(),
                ["scheduled"] = summary.Scheduled.ToString(),
                ["failed"] = summary.Failed.ToString()
            });
    }

    private static string GetLabel(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "正常",
            PimHealthStatus.Warning => "有警告",
            PimHealthStatus.Critical => "故障",
            _ => "未知"
        };

    private static int GetSeverityRank(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => 0,
            PimHealthStatus.Unknown => 1,
            PimHealthStatus.Warning => 2,
            PimHealthStatus.Critical => 3,
            _ => 1
        };

    private static string GetMessage(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "All checked systems are healthy.",
            PimHealthStatus.Warning => "Some systems need attention.",
            PimHealthStatus.Critical => "One or more systems are failing.",
            _ => "System status is unknown."
        };
}
