using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileQualityService
{
    private static readonly TimeSpan StaleHeartbeatAge = TimeSpan.FromMinutes(30);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileQualityService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<MobileQualityResponse> GetQualityAsync(
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        CancellationToken ct = default,
        string? deviceId = null)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var checkedAt = _timeProvider.GetUtcNow();
        var rangeEnd = rangeEndUtc ?? checkedAt;
        var rangeStart = rangeStartUtc ?? rangeEnd.AddDays(-1);
        if (rangeEnd < rangeStart)
            (rangeStart, rangeEnd) = (rangeEnd, rangeStart);

        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        var registeredDeviceQuery = _db.Set<MobileDeviceEntity>()
            .AsNoTracking()
            .Where(d => d.UserId == userId);
        if (normalizedDeviceId is not null)
            registeredDeviceQuery = registeredDeviceQuery.Where(d => d.DeviceId == normalizedDeviceId);
        var registeredDeviceIds = await registeredDeviceQuery
            .Select(d => d.DeviceId)
            .Distinct()
            .ToListAsync(ct);

        var heartbeat = registeredDeviceIds.Count == 0
            ? null
            : await _db.Set<DaemonHeartbeatEntity>()
                .AsNoTracking()
                .Where(h => h.DaemonKind == "android" && registeredDeviceIds.Contains(h.DeviceId))
                .OrderByDescending(h => h.ReceivedAt)
                .FirstOrDefaultAsync(ct);

        var eventPackages = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && (normalizedDeviceId == null || e.DeviceId == normalizedDeviceId)
                && e.EventTimestampUtc >= rangeStart
                && e.EventTimestampUtc < rangeEnd)
            .Select(e => e.PackageName)
            .ToListAsync(ct);

        var summaryRows = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && (normalizedDeviceId == null || s.DeviceId == normalizedDeviceId)
                && s.WindowEndUtc > rangeStart
                && s.WindowStartUtc < rangeEnd)
            .Select(s => new { s.PackageName, s.SourceKind })
            .ToListAsync(ct);

        var eventCount = eventPackages.Count;
        var fallbackSummaryCount = summaryRows.Count(s => IsFallbackSource(s.SourceKind));

        var batchRows = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId
                && (normalizedDeviceId == null || b.DeviceId == normalizedDeviceId)
                && b.WindowEndUtc > rangeStart
                && b.WindowStartUtc < rangeEnd)
            .ToListAsync(ct);

        var locationRows = await _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(p => p.UserId == userId
                && (normalizedDeviceId == null || p.DeviceId == normalizedDeviceId)
                && p.RecordedAtUtc >= rangeStart
                && p.RecordedAtUtc < rangeEnd)
            .ToListAsync(ct);

        var usedPackages = eventPackages
            .Concat(summaryRows.Select(s => s.PackageName))
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var appMetadataPackages = await _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(a => a.UserId == userId
                && (normalizedDeviceId == null || a.DeviceId == normalizedDeviceId))
            .Select(a => a.PackageName)
            .ToListAsync(ct);
        var missingAppMetadataCount = usedPackages
            .Except(appMetadataPackages, StringComparer.Ordinal)
            .Count();

        var issues = new List<MobileQualityIssueDto>();
        var components = new List<MobileQualityComponentDto>
        {
            CheckHeartbeat(heartbeat, checkedAt, issues),
            CheckUsage(eventCount, fallbackSummaryCount, checkedAt, issues),
            CheckSync(batchRows, checkedAt, issues),
            CheckLocation(locationRows, checkedAt, issues),
            CheckAppMetadata(appMetadataPackages.Count, usedPackages.Length, missingAppMetadataCount, checkedAt, issues)
        };

        var overall = components
            .Select(component => component.Status)
            .OrderByDescending(SeverityRank)
            .FirstOrDefault();

        return new MobileQualityResponse(
            overall,
            Label(overall),
            Message(overall),
            checkedAt,
            components,
            issues,
            issues
                .Select(issue => issue.NextStep)
                .Where(step => !string.IsNullOrWhiteSpace(step))
                .Distinct(StringComparer.Ordinal)
                .Cast<string>()
                .ToList());
    }

    private static MobileQualityComponentDto CheckHeartbeat(
        DaemonHeartbeatEntity? heartbeat,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        if (heartbeat is null)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-heartbeat-missing",
                PimHealthStatus.Unknown,
                "android-heartbeat",
                "尚未收到当前用户 Android 设备的心跳。",
                "打开 Android App 并完成登录后重新同步。"));
            return Component(
                "android-heartbeat",
                "Android 心跳",
                PimHealthStatus.Unknown,
                "尚未收到当前用户 Android 设备的心跳。",
                checkedAt,
                new Dictionary<string, string>());
        }

        var age = checkedAt - heartbeat.ReceivedAt;
        var isStale = age > StaleHeartbeatAge;
        var hasLastError = !string.IsNullOrWhiteSpace(heartbeat.LastError);
        var uploadQueueCount = heartbeat.UploadQueueCount.GetValueOrDefault();
        var hasUploadQueue = uploadQueueCount > 0;

        if (isStale)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-heartbeat-stale",
                PimHealthStatus.Warning,
                "android-heartbeat",
                "Android 客户端心跳偏旧。",
                "打开 Android App 确认服务器连接和登录状态。"));
        }
        if (hasLastError)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-heartbeat-error",
                PimHealthStatus.Warning,
                "android-heartbeat",
                "Android 最近一次同步报告了错误。",
                "查看 Android App 日志和服务器连接状态后重新同步。"));
        }
        if (hasUploadQueue)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-heartbeat-upload-queue",
                PimHealthStatus.Warning,
                "android-heartbeat",
                "Android 仍有待上传队列。",
                "保持 Android App 打开，等待待上传记录完成传输。"));
        }

        var status = isStale || hasLastError || hasUploadQueue
            ? PimHealthStatus.Warning
            : PimHealthStatus.Healthy;
        var message = isStale
            ? "Android 客户端心跳偏旧。"
            : hasLastError
                ? "Android 最近一次同步报告了错误。"
                : hasUploadQueue
                    ? "Android 仍有待上传队列。"
                    : "Android 客户端心跳正常。";

        return Component(
            "android-heartbeat",
            "Android 心跳",
            status,
            message,
            checkedAt,
            new Dictionary<string, string>
            {
                ["deviceId"] = heartbeat.DeviceId,
                ["receivedAt"] = heartbeat.ReceivedAt.ToString("O"),
                ["lastSuccessfulUploadAt"] = heartbeat.LastSuccessfulUploadAt?.ToString("O") ?? string.Empty,
                ["uploadQueueCount"] = uploadQueueCount.ToString(),
                ["lastError"] = heartbeat.LastError ?? string.Empty
            });
    }

    private static MobileQualityComponentDto CheckUsage(
        int eventCount,
        int fallbackSummaryCount,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        var status = PimHealthStatus.Healthy;
        var message = "移动使用事件采集正常。";

        if (eventCount == 0 && fallbackSummaryCount == 0)
        {
            status = PimHealthStatus.Unknown;
            message = "所选范围内没有移动使用数据。";
            issues.Add(new MobileQualityIssueDto(
                "mobile-usage-missing",
                PimHealthStatus.Unknown,
                "mobile-usage-coverage",
                message,
                "确认 Android 使用情况访问权限已开启并重新同步。"));
        }
        else if (fallbackSummaryCount > 0)
        {
            status = PimHealthStatus.Warning;
            message = eventCount == 0
                ? "所选范围仅有 UsageStats 汇总数据，缺少 UsageEvents 时间线。"
                : "所选范围仍包含 fallback-only 的 Android 使用汇总。";
            issues.Add(new MobileQualityIssueDto(
                "mobile-usage-fallback-only",
                PimHealthStatus.Warning,
                "mobile-usage-coverage",
                message,
                "在 Android App 中重新触发同步，确认 UsageEvents 可读取。"));
        }

        return Component(
            "mobile-usage-coverage",
            "移动使用采集",
            status,
            message,
            checkedAt,
            new Dictionary<string, string>
            {
                ["eventCount"] = eventCount.ToString(),
                ["fallbackSummaryCount"] = fallbackSummaryCount.ToString()
            });
    }

    private static MobileQualityComponentDto CheckSync(
        IReadOnlyCollection<MobileSyncBatchEntity> batches,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        if (batches.Count == 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-sync-missing",
                PimHealthStatus.Unknown,
                "mobile-sync",
                "所选范围内没有移动同步批次。",
                "打开 Android App 并重新同步。"));
            return Component(
                "mobile-sync",
                "移动同步批次",
                PimHealthStatus.Unknown,
                "所选范围内没有移动同步批次。",
                checkedAt,
                new Dictionary<string, string>
                {
                    ["batchCount"] = "0",
                    ["failedBatchCount"] = "0",
                    ["acceptedCount"] = "0"
                });
        }

        var failedBatchCount = batches.Count(b => b.FailedCount > 0
            || !string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase));
        if (failedBatchCount > 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-sync-failed-batch",
                PimHealthStatus.Warning,
                "mobile-sync",
                "存在失败或未完成的移动同步批次。",
                "查看 Android App 日志和服务器连接状态后重新同步。"));
        }

        return Component(
            "mobile-sync",
            "移动同步批次",
            failedBatchCount > 0 ? PimHealthStatus.Warning : PimHealthStatus.Healthy,
            failedBatchCount > 0 ? "存在失败或未完成的移动同步批次。" : "移动同步批次正常。",
            checkedAt,
            new Dictionary<string, string>
            {
                ["batchCount"] = batches.Count.ToString(),
                ["failedBatchCount"] = failedBatchCount.ToString(),
                ["acceptedCount"] = batches.Sum(b => b.AcceptedCount).ToString()
            });
    }

    private static MobileQualityComponentDto CheckLocation(
        IReadOnlyCollection<MobileLocationPointEntity> locations,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        if (locations.Count == 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-location-missing",
                PimHealthStatus.Unknown,
                "mobile-location",
                "所选范围内没有移动定位记录。",
                "需要位置历史时，在 Android App 中手动触发定位。"));
            return Component(
                "mobile-location",
                "移动定位",
                PimHealthStatus.Unknown,
                "所选范围内没有移动定位记录。",
                checkedAt,
                new Dictionary<string, string>
                {
                    ["locationPointCount"] = "0",
                    ["rejectedLocationCount"] = "0"
                });
        }

        var rejectedCount = locations.Count(p => string.Equals(p.Quality, "rejected", StringComparison.OrdinalIgnoreCase));
        var usableCount = locations.Count - rejectedCount;
        if (rejectedCount > 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-location-rejected",
                PimHealthStatus.Warning,
                "mobile-location",
                "存在因误差过大而拒绝的移动定位点。",
                "在开阔环境重新手动定位，等待误差降到 50 米内。"));
        }

        return Component(
            "mobile-location",
            "移动定位",
            rejectedCount > 0 ? PimHealthStatus.Warning : PimHealthStatus.Healthy,
            rejectedCount > 0 ? "存在因误差过大而拒绝的移动定位点。" : "移动定位记录正常。",
            checkedAt,
            new Dictionary<string, string>
            {
                ["locationPointCount"] = usableCount.ToString(),
                ["rejectedLocationCount"] = rejectedCount.ToString()
            });
    }

    private static MobileQualityComponentDto CheckAppMetadata(
        int appMetadataCount,
        int usedPackageCount,
        int missingAppMetadataCount,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        if (appMetadataCount == 0 || missingAppMetadataCount > 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-app-metadata-missing",
                missingAppMetadataCount > 0 ? PimHealthStatus.Warning : PimHealthStatus.Unknown,
                "mobile-app-metadata",
                "Android 应用元数据不完整。",
                "重新同步 Android 使用记录以上传应用元数据。"));
        }

        var status = missingAppMetadataCount > 0
            ? PimHealthStatus.Warning
            : appMetadataCount == 0
                ? PimHealthStatus.Unknown
                : PimHealthStatus.Healthy;

        return Component(
            "mobile-app-metadata",
            "移动应用元数据",
            status,
            status == PimHealthStatus.Healthy ? "移动应用元数据正常。" : "Android 应用元数据不完整。",
            checkedAt,
            new Dictionary<string, string>
            {
                ["appMetadataCount"] = appMetadataCount.ToString(),
                ["usedPackageCount"] = usedPackageCount.ToString(),
                ["missingAppMetadataCount"] = missingAppMetadataCount.ToString()
            });
    }

    private static MobileQualityComponentDto Component(
        string key,
        string name,
        PimHealthStatus status,
        string message,
        DateTimeOffset checkedAt,
        IReadOnlyDictionary<string, string> details)
        => new(
            key,
            name,
            status,
            message,
            checkedAt,
            details);

    private static bool IsFallbackSource(string sourceKind)
        => sourceKind.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            || sourceKind.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private static int SeverityRank(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => 0,
            PimHealthStatus.Unknown => 1,
            PimHealthStatus.Warning => 2,
            PimHealthStatus.Critical => 3,
            _ => 0
        };

    private static string Label(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "Android 采集正常",
            PimHealthStatus.Warning => "Android 采集有警告",
            PimHealthStatus.Critical => "Android 采集故障",
            _ => "Android 采集未知"
        };

    private static string Message(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "移动端同步、定位和应用使用采集诊断可用。",
            PimHealthStatus.Warning => "移动端数据可用，但部分采集质量问题需要关注。",
            PimHealthStatus.Critical => "移动端采集存在严重问题。",
            _ => "移动端诊断缺少足够数据。"
        };
}
