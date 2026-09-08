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
        List<DaemonHeartbeatEntity> heartbeats;
        try
        {
            heartbeats = await _db.DaemonHeartbeats
                .AsNoTracking()
                .Where(d => d.DaemonKind == "windows")
                .OrderByDescending(d => d.ReceivedAt)
                .ToListAsync(ct);
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

        if (heartbeats.Count == 0)
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

        var latestPerDevice = heartbeats
            .GroupBy(h => h.DeviceId)
            .Select(g => g.First())
            .ToList();

        var classified = latestPerDevice
            .Select(h => (Heartbeat: h, Lifecycle: DaemonLifecycleClassifier.Classify(h, checkedAt)))
            .ToList();

        if (latestPerDevice.Count == 1)
        {
            var single = classified[0];
            var details = new Dictionary<string, string>
            {
                ["deviceId"] = single.Heartbeat.DeviceId,
                ["version"] = single.Heartbeat.Version,
                ["receivedAt"] = single.Heartbeat.ReceivedAt.ToString("O"),
                ["activityWatch"] = single.Heartbeat.ActivityWatchState,
                ["keyStats"] = single.Heartbeat.KeyStatsState,
                ["daemonState"] = single.Lifecycle.State
            };

            if (single.Lifecycle.PlannedOfflineAt is not null)
            {
                details["plannedOfflineAt"] = single.Lifecycle.PlannedOfflineAt;
                details["offlineReason"] = single.Lifecycle.OfflineReason ?? "";
            }

            return new StatusComponentDto(
                "windows-daemon",
                "Windows 守护程序",
                StatusComponentKind.Daemon,
                single.Lifecycle.Status,
                single.Lifecycle.Message,
                checkedAt,
                details);
        }

        var hasHealthy = classified.Any(c => c.Lifecycle.Status == PimHealthStatus.Healthy);
        var allPlannedOffline = classified.All(c => c.Lifecycle.State == "planned-offline");
        var primary = classified.OrderByDescending(c => c.Heartbeat.ReceivedAt).First();

        PimHealthStatus overallStatus;
        string overallMessage;

        if (hasHealthy)
        {
            overallStatus = PimHealthStatus.Healthy;
            overallMessage = $"Windows 守护程序运行中（{classified.Count} 台已注册设备）。";
        }
        else if (allPlannedOffline)
        {
            overallStatus = PimHealthStatus.Healthy;
            overallMessage = "所有 Windows 设备均处于计划离线状态。";
        }
        else
        {
            overallStatus = primary.Lifecycle.Status == PimHealthStatus.Critical ? PimHealthStatus.Warning : primary.Lifecycle.Status;
            overallMessage = primary.Lifecycle.Message;
        }

        var multiDetails = new Dictionary<string, string>
        {
            ["deviceCount"] = classified.Count.ToString(),
            ["deviceId"] = primary.Heartbeat.DeviceId,
            ["version"] = primary.Heartbeat.Version,
            ["receivedAt"] = primary.Heartbeat.ReceivedAt.ToString("O"),
            ["activityWatch"] = primary.Heartbeat.ActivityWatchState,
            ["keyStats"] = primary.Heartbeat.KeyStatsState,
            ["daemonState"] = primary.Lifecycle.State
        };

        if (primary.Lifecycle.PlannedOfflineAt is not null)
        {
            multiDetails["plannedOfflineAt"] = primary.Lifecycle.PlannedOfflineAt;
            multiDetails["offlineReason"] = primary.Lifecycle.OfflineReason ?? "";
        }

        return new StatusComponentDto(
            "windows-daemon",
            "Windows 守护程序",
            StatusComponentKind.Daemon,
            overallStatus,
            overallMessage,
            checkedAt,
            multiDetails);
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
