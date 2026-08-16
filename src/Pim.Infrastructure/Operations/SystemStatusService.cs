using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class SystemStatusService : ISystemStatusService
{
    private readonly PimDbContext _db;
    private readonly IBackgroundJobStatusService _backgroundJobs;
    private readonly TimeProvider _timeProvider;

    public SystemStatusService(PimDbContext db, IBackgroundJobStatusService backgroundJobs, TimeProvider timeProvider)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
        _timeProvider = timeProvider;
    }

    public async Task<SystemStatusSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(ct);
        return detail.Summary;
    }

    public async Task<SystemStatusDetailDto> GetDetailAsync(CancellationToken ct = default)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        var components = new List<StatusComponentDto>
        {
            new(
                "api",
                "API",
                StatusComponentKind.Api,
                PimHealthStatus.Healthy,
                "API 进程正在运行。",
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
                "数据库",
                StatusComponentKind.Database,
                PimHealthStatus.Healthy,
                "数据库可访问。",
                checkedAt,
                new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            return new StatusComponentDto(
                "database",
                "数据库",
                StatusComponentKind.Database,
                PimHealthStatus.Critical,
                "数据库不可用。",
                checkedAt,
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
        }
    }

    private async Task<StatusComponentDto> BuildWindowsDaemonComponentAsync(DateTimeOffset checkedAt, CancellationToken ct)
    {
        DaemonHeartbeatEntity? heartbeat;
        try
        {
            heartbeat = await _db.DaemonHeartbeats
                .AsNoTracking()
                .Where(d => d.DaemonKind == "windows")
                .OrderByDescending(d => d.ReceivedAt)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            return new StatusComponentDto(
                "windows-daemon",
                "Windows 守护程序",
                StatusComponentKind.Daemon,
                PimHealthStatus.Critical,
                "Windows 守护程序心跳状态不可用。",
                checkedAt,
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
        }

        if (heartbeat is null)
        {
            return new StatusComponentDto(
                "windows-daemon",
                "Windows 守护程序",
                StatusComponentKind.Daemon,
                PimHealthStatus.Unknown,
                "尚未收到 Windows 守护程序心跳。",
                checkedAt,
                new Dictionary<string, string>());
        }

        var lifecycle = DaemonLifecycleClassifier.Classify(heartbeat, checkedAt);
        var details = new Dictionary<string, string>
        {
            ["deviceId"] = heartbeat.DeviceId,
            ["version"] = heartbeat.Version,
            ["receivedAt"] = heartbeat.ReceivedAt.ToString("O"),
            ["activityWatch"] = heartbeat.ActivityWatchState,
            ["keyStats"] = heartbeat.KeyStatsState,
            ["daemonState"] = lifecycle.State
        };

        if (lifecycle.PlannedOfflineAt is not null)
        {
            details["plannedOfflineAt"] = lifecycle.PlannedOfflineAt;
            details["offlineReason"] = lifecycle.OfflineReason ?? "";
        }

        return new StatusComponentDto(
            "windows-daemon",
            "Windows 守护程序",
            StatusComponentKind.Daemon,
            lifecycle.Status,
            lifecycle.Message,
            checkedAt,
            details);
    }

    private async Task<StatusComponentDto> BuildBackgroundJobsComponentAsync(CancellationToken ct)
    {
        var summary = await _backgroundJobs.GetSummaryAsync(ct);

        return new StatusComponentDto(
            "background-jobs",
            "后台任务",
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
            PimHealthStatus.Healthy => "所有已检查系统均正常。",
            PimHealthStatus.Warning => "部分系统需要关注。",
            PimHealthStatus.Critical => "一个或多个系统正在故障。",
            _ => "系统状态未知。"
        };
}
