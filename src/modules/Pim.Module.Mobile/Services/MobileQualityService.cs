using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileQualityService
{
    private static readonly TimeSpan StaleHeartbeatAge = TimeSpan.FromMinutes(30);

    /// <summary>汇总滞后告警线：2 个窗口 = 4 小时（EPIC #254 阈值 T3）。</summary>
    private static readonly TimeSpan StaleSummaryLag = TimeSpan.FromHours(4);

    /// <summary>汇总断流红线：超过 1 天没有新汇总，且事件仍在入库。</summary>
    private static readonly TimeSpan BrokenSummaryLag = TimeSpan.FromHours(24);

    /// <summary>批次积压线：pending 超过 30 分钟仍未完成视为卡住（与 MobileSyncBacklogInspector 一致）。</summary>
    private static readonly TimeSpan StalledBatchAge = TimeSpan.FromMinutes(30);

    /// <summary>质量报告里内联展示的待补包名样例数量。</summary>
    private const int MissingPackageSampleSize = 10;
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly IDataReliabilityGate? _dataReliabilityGate;

    public MobileQualityService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider, IDataReliabilityGate? dataReliabilityGate = null)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
        _dataReliabilityGate = dataReliabilityGate;
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

        var eventRows = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && (normalizedDeviceId == null || e.DeviceId == normalizedDeviceId)
                && e.EventTimestampUtc >= rangeStart
                && e.EventTimestampUtc < rangeEnd)
            .Select(e => new { e.PackageName, e.EventTimestampUtc })
            .ToListAsync(ct);

        var summaryRows = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && (normalizedDeviceId == null || s.DeviceId == normalizedDeviceId)
                && s.WindowEndUtc > rangeStart
                && s.WindowStartUtc < rangeEnd)
            .Select(s => new { s.PackageName, s.SourceKind })
            .ToListAsync(ct);

        // 汇总新鲜度：以"所选窗口的结束时刻"为评估点，取该时刻之前最后一条汇总（#244）。
        // 用全局最新汇总会让历史范围被"未来的汇总"掩盖，用范围内最新汇总又会在汇总被裁剪时误报，
        // 以窗口结束时刻为界同时避免这两种错误。
        var evaluationEnd = rangeEnd > checkedAt ? checkedAt : rangeEnd;
        var latestSummaryWindowEndUtc = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && (normalizedDeviceId == null || s.DeviceId == normalizedDeviceId)
                && s.WindowEndUtc <= evaluationEnd)
            .MaxAsync(s => (DateTimeOffset?)s.WindowEndUtc, ct);
        var hasEverReceivedSummary = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId
                && (normalizedDeviceId == null || s.DeviceId == normalizedDeviceId), ct);

        var eventCount = eventRows.Count;
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

        var usedPackages = eventRows
            .Select(e => e.PackageName)
            .Concat(summaryRows.Select(s => s.PackageName))
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var deviceCatalogPackages = await _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(a => a.UserId == userId
                && (normalizedDeviceId == null || a.DeviceId == normalizedDeviceId))
            .Select(a => a.PackageName)
            .ToListAsync(ct);

        // "缺元数据"必须是同一口径相减（#245）：两边都按"该用户的包"算，
        // 否则数字随所选范围与设备漂移，也无法回答"该补哪些包"。
        var catalogPackagesAnyDevice = await _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.PackageName)
            .Distinct()
            .ToListAsync(ct);
        var missingAppMetadataPackages = usedPackages
            .Except(catalogPackagesAnyDevice, StringComparer.Ordinal)
            .OrderBy(packageName => packageName, StringComparer.Ordinal)
            .ToArray();

        var issues = new List<MobileQualityIssueDto>();
        var components = new List<MobileQualityComponentDto>
        {
            CheckHeartbeat(heartbeat, checkedAt, issues),
            CheckUsage(
                eventCount,
                fallbackSummaryCount,
                evaluationEnd,
                latestSummaryWindowEndUtc,
                hasEverReceivedSummary,
                checkedAt,
                issues),
            CheckSync(batchRows, checkedAt, issues),
            CheckLocation(locationRows, checkedAt, issues),
            CheckAppMetadata(
                deviceCatalogPackages.Distinct(StringComparer.Ordinal).Count(),
                catalogPackagesAnyDevice.Count,
                usedPackages.Length,
                missingAppMetadataPackages,
                checkedAt,
                issues)
        };
        AddDataReliabilityGate(components, issues, new[] { "S4", "S10", "S11", "S12" }, checkedAt);

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

    /// <summary>
    /// 把数据可信度尺子接入质量报告（#260 第 4 点）：尺子红，报告不得绿。
    /// 只做附加：新增一个 component 与对应 issue，整体状态由既有"取最严"聚合自然降级；
    /// 尺子尚未体检或结果过期时给出 Unknown 组件与明确文案，绝不静默判健康。
    /// </summary>
    private void AddDataReliabilityGate(
        List<MobileQualityComponentDto> components,
        List<MobileQualityIssueDto> issues,
        IReadOnlyList<string> ruleCodes,
        DateTimeOffset checkedAt)
    {
        if (_dataReliabilityGate is null)
        {
            return;
        }

        var verdict = _dataReliabilityGate.Evaluate(ruleCodes);

        components.Add(new MobileQualityComponentDto(
            "data_reliability",
            "数据可信度尺子",
            verdict.Status,
            verdict.Message,
            checkedAt,
            new Dictionary<string, string>
            {
                ["redRules"] = string.Join(",", verdict.RedRules),
                ["yellowRules"] = string.Join(",", verdict.YellowRules),
                ["unknownRules"] = string.Join(",", verdict.UnknownRules),
                ["inspectedAtUtc"] = verdict.InspectedAtUtc?.ToString("O") ?? string.Empty
            }));

        foreach (var code in verdict.RedRules)
        {
            issues.Add(new MobileQualityIssueDto(
                code,
                PimHealthStatus.Critical,
                "data_reliability",
                $"{code} 数据可信度尺子报红：{verdict.Message}",
                "打开「设置 → 数据可信度」查看违规样例与存量趋势"));
        }

        foreach (var code in verdict.YellowRules)
        {
            issues.Add(new MobileQualityIssueDto(
                code,
                PimHealthStatus.Warning,
                "data_reliability",
                $"{code} 数据可信度尺子报黄：{verdict.Message}",
                null));
        }
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
        DateTimeOffset evaluationEnd,
        DateTimeOffset? latestSummaryWindowEndUtc,
        bool hasEverReceivedSummary,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        var status = PimHealthStatus.Healthy;
        var message = "移动使用事件采集正常。";
        var summaryLagHours = string.Empty;

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
        else if (eventCount > 0)
        {
            // 有事件却没有（新鲜）汇总 = 汇总链路断流，此前被当作"采集正常"（#244）。
            var lag = latestSummaryWindowEndUtc is null
                ? (TimeSpan?)null
                : evaluationEnd - latestSummaryWindowEndUtc.Value;
            summaryLagHours = lag is null
                ? string.Empty
                : Math.Round(Math.Max(0, lag.Value.TotalHours), 1).ToString("0.0");
            var isStale = latestSummaryWindowEndUtc is null || lag > StaleSummaryLag;
            // 从未收到过任何汇总时只报警告：这类设备可能只是"窗口内一直有事件、用不到兜底汇总"，
            // 还没有足够证据说明汇总链路坏掉了。
            var isBroken = latestSummaryWindowEndUtc is null
                ? hasEverReceivedSummary
                : lag > BrokenSummaryLag;

            if (isBroken)
            {
                status = PimHealthStatus.Critical;
                message = $"移动使用汇总已断流约 {summaryLagHours} 小时（事件仍在入库）。";
                issues.Add(new MobileQualityIssueDto(
                    "mobile-usage-summary-stale",
                    PimHealthStatus.Critical,
                    "mobile-usage-coverage",
                    message,
                    "检查 Android App 的兜底汇总采集与上传是否仍在运行后重新同步。"));
            }
            else if (isStale)
            {
                status = PimHealthStatus.Warning;
                message = latestSummaryWindowEndUtc is null
                    ? "所选范围内有使用事件，但尚未收到对应的 UsageStats 兜底汇总。"
                    : $"移动使用汇总已滞后约 {summaryLagHours} 小时（事件仍在入库）。";
                issues.Add(new MobileQualityIssueDto(
                    "mobile-usage-summary-stale",
                    PimHealthStatus.Warning,
                    "mobile-usage-coverage",
                    message,
                    "检查 Android App 的兜底汇总采集与上传是否仍在运行后重新同步。"));
            }
        }

        if (fallbackSummaryCount > 0 && status == PimHealthStatus.Healthy)
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
                ["fallbackSummaryCount"] = fallbackSummaryCount.ToString(),
                ["evaluationEndAt"] = evaluationEnd.ToString("O"),
                ["latestSummaryAt"] = latestSummaryWindowEndUtc?.ToString("O") ?? string.Empty,
                ["summaryLagHours"] = summaryLagHours
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

        var failedBatchCount = batches.Count(b => MobileSyncBatchStatus.IsFailed(b.FailedCount, b.Status));
        var stalledBatchCount = batches.Count(b =>
            MobileSyncBatchStatus.IsActive(b.Status) && checkedAt - b.CreatedAt > StalledBatchAge);
        var rejectedItemCount = batches.Sum(b => b.RejectedCount);

        if (failedBatchCount > 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-sync-failed-batch",
                PimHealthStatus.Warning,
                "mobile-sync",
                "存在失败的移动同步批次。",
                "查看 Android App 日志和服务器连接状态后重新同步。"));
        }

        if (stalledBatchCount > 0)
        {
            // 未完成批次以前根本不可见（写入侧只写 completed / completed-with-errors，#243）。
            issues.Add(new MobileQualityIssueDto(
                "mobile-sync-stalled-batch",
                PimHealthStatus.Warning,
                "mobile-sync",
                "存在长时间未完成的移动同步批次。",
                "确认 Android App 的上传没有被中断，然后重新同步。"));
        }

        var status = failedBatchCount > 0 || stalledBatchCount > 0
            ? PimHealthStatus.Warning
            : PimHealthStatus.Healthy;
        var message = failedBatchCount > 0
            ? "存在失败的移动同步批次。"
            : stalledBatchCount > 0
                ? "存在长时间未完成的移动同步批次。"
                : "移动同步批次正常。";

        return Component(
            "mobile-sync",
            "移动同步批次",
            status,
            message,
            checkedAt,
            new Dictionary<string, string>
            {
                ["batchCount"] = batches.Count.ToString(),
                ["failedBatchCount"] = failedBatchCount.ToString(),
                ["stalledBatchCount"] = stalledBatchCount.ToString(),
                ["rejectedCount"] = rejectedItemCount.ToString(),
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
        int deviceCatalogPackageCount,
        int catalogPackageCount,
        int usedPackageCount,
        IReadOnlyList<string> missingPackages,
        DateTimeOffset checkedAt,
        List<MobileQualityIssueDto> issues)
    {
        var missingCount = missingPackages.Count;
        // 判据用"全设备口径"的目录数：missing 集合也是全设备口径（#245），
        // 否则"本设备无目录行、别的设备有"会被判成 Unknown。
        if (catalogPackageCount == 0 || missingCount > 0)
        {
            issues.Add(new MobileQualityIssueDto(
                "mobile-app-metadata-missing",
                missingCount > 0 ? PimHealthStatus.Warning : PimHealthStatus.Unknown,
                "mobile-app-metadata",
                missingCount > 0
                    ? $"所选范围内有 {missingCount} 个应用缺少元数据。"
                    : "Android 应用元数据不完整。",
                "调用 /api/v1/mobile/apps/missing-metadata 取回待补包名，重新同步以补上应用元数据。"));
        }

        var status = missingCount > 0
            ? PimHealthStatus.Warning
            : catalogPackageCount == 0
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
                ["appMetadataCount"] = deviceCatalogPackageCount.ToString(),
                ["catalogPackageCount"] = catalogPackageCount.ToString(),
                ["usedPackageCount"] = usedPackageCount.ToString(),
                ["missingAppMetadataCount"] = missingCount.ToString(),
                ["missingPackages"] = string.Join(",", missingPackages.Take(MissingPackageSampleSize))
            });
    }

    /// <summary>
    /// 待补元数据的包清单（#245）：服务端提供"该补哪些包"的口径，
    /// 供客户端或回填任务按需索取，而不是只给一个会漂移的数字。
    /// </summary>
    public async Task<MobileMissingAppMetadataResponse> GetMissingAppMetadataAsync(
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        string? deviceId = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var rangeEnd = rangeEndUtc ?? _timeProvider.GetUtcNow();
        var rangeStart = rangeStartUtc ?? rangeEnd.AddDays(-1);
        if (rangeEnd < rangeStart)
            (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
        var take = Math.Clamp(limit, 1, 1000);
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        var eventRows = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && (normalizedDeviceId == null || e.DeviceId == normalizedDeviceId)
                && e.EventTimestampUtc >= rangeStart
                && e.EventTimestampUtc < rangeEnd)
            .Select(e => new { e.PackageName, e.EventTimestampUtc })
            .ToListAsync(ct);

        var summaryRows = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && (normalizedDeviceId == null || s.DeviceId == normalizedDeviceId)
                && s.WindowEndUtc > rangeStart
                && s.WindowStartUtc < rangeEnd)
            .Select(s => new { s.PackageName, s.TotalTimeVisibleMs })
            .ToListAsync(ct);

        var catalogPackages = await _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.PackageName)
            .Distinct()
            .ToListAsync(ct);

        var used = eventRows
            .Select(e => new UsageRow(e.PackageName, e.EventTimestampUtc, 0))
            .Concat(summaryRows.Select(s => new UsageRow(s.PackageName, null, s.TotalTimeVisibleMs)))
            .Where(row => !string.IsNullOrWhiteSpace(row.PackageName))
            .ToList();

        var packages = used
            .GroupBy(row => row.PackageName, StringComparer.Ordinal)
            .Where(group => !catalogPackages.Contains(group.Key, StringComparer.Ordinal))
            .Select(group => new MissingAppMetadataPackageDto(
                group.Key,
                group.Count(row => row.LastUsedAtUtc is not null),
                group.Max(row => row.LastUsedAtUtc),
                group.Sum(row => row.ForegroundMs)))
            .OrderByDescending(item => item.ForegroundMs)
            .ThenBy(item => item.PackageName, StringComparer.Ordinal)
            .ToList();

        return new MobileMissingAppMetadataResponse(
            normalizedDeviceId,
            rangeStart,
            rangeEnd,
            packages.Count,
            packages.Take(take).ToList());
    }

    private sealed record UsageRow(string PackageName, DateTimeOffset? LastUsedAtUtc, long ForegroundMs);

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

    // === 脏数据自动清理 ===
    public async Task<int> CleanupAnomalousDataAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var updated = 0;
        const long eightHoursMs = 8L * 60 * 60 * 1000;

        // 1) session duration > 8h → anomalous_duration
        var longSessions = await _db.Set<MobileUsageSessionEntity>()
            .Where(s => s.UserId == userId && s.DurationMs > eightHoursMs && !s.QualityFlagsJson.Contains("anomalous_duration"))
            .ToListAsync(ct);
        foreach (var s in longSessions)
        {
            s.QualityFlagsJson = MergeFlag(s.QualityFlagsJson, "anomalous_duration");
            updated++;
        }

        // 2)同一天所有 session 之和 >24h → day_overflow (标记该天所有 session)
        var sessionsByDay = await _db.Set<MobileUsageSessionEntity>()
            .Where(s => s.UserId == userId)
            .Select(s => new { s.Id, s.StartUtc, s.DurationMs, s.QualityFlagsJson })
            .ToListAsync(ct);
        var byDayGroups = sessionsByDay.GroupBy(x => DateOnly.FromDateTime(x.StartUtc.UtcDateTime));
        foreach (var group in byDayGroups)
        {
            var totalMs = group.Sum(x => x.DurationMs ?? 0);
            if (totalMs > 24L * 60 * 60 * 1000)
            {
                foreach (var item in group.Where(x => !x.QualityFlagsJson.Contains("day_overflow")))
                {
                    var entity = await _db.Set<MobileUsageSessionEntity>().FindAsync(new object[] { item.Id }, ct);
                    if (entity != null)
                    {
                        entity.QualityFlagsJson = MergeFlag(entity.QualityFlagsJson, "day_overflow");
                        updated++;
                    }
                }
            }
        }

        // 3)同一 package+同一小时窗口多条 summary → 保留最大，其余标记重复
        var summaries = await _db.Set<MobileUsageSummaryEntity>()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
        var dupGroups = summaries.GroupBy(s => new { s.PackageName, Hour = new DateTimeOffset(s.WindowStartUtc.Year, s.WindowStartUtc.Month, s.WindowStartUtc.Day, s.WindowStartUtc.Hour, 0, 0, TimeSpan.Zero) })
            .Where(g => g.Count() > 1);
        foreach (var group in dupGroups)
        {
            var ordered = group.OrderByDescending(s => s.TotalTimeVisibleMs).ToList();
            foreach (var dup in ordered.Skip(1).Where(s => !s.QualityFlagsJson.Contains("duplicate_summary")))
            {
                dup.QualityFlagsJson = MergeFlag(dup.QualityFlagsJson, "duplicate_summary");
                updated++;
            }
        }

        if (updated > 0) await _db.SaveChangesAsync(ct);
        return updated;
    }

    private static string MergeFlag(string json, string flag)
    {
        try
        {
            var arr = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (!arr.Contains(flag)) arr.Add(flag);
            return System.Text.Json.JsonSerializer.Serialize(arr);
        }
        catch { return $"[\"{flag}\"]"; }
    }

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
