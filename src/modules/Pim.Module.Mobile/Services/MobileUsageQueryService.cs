using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileUsageQueryService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileUsageQueryService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<MobileUsageSummaryResponse> GetSummaryAsync(MobileSummaryQuery query, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var summaries = _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId);

        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            summaries = summaries.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            summaries = summaries.Where(s => s.WindowEndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            summaries = summaries.Where(s => s.WindowStartUtc < query.RangeEndUtc);

        var rawSummaryRows = await summaries.ToListAsync(ct);
        var summaryRows = DeduplicateSummaries(rawSummaryRows);
        var deviceIds = summaryRows.Select(s => s.DeviceId).Distinct().ToArray();
        var packageNames = summaryRows.Select(s => s.PackageName).Distinct().ToArray();
        var appCatalog = await AppCatalog(userId, query.DeviceId, packageNames, ct);
        var sessionCounts = await SessionCounts(userId, query, ct);
        var launchCounts = await LaunchCounts(userId, query, ct);
        var batches = await SyncBatches(userId, query, ct);
        var locationPoints = await LocationPoints(userId, query, ct);

        var totalMs = summaryRows.Sum(s => s.TotalTimeVisibleMs);
        var fallbackMs = summaryRows
            .Where(s => IsFallbackSource(s.SourceKind))
            .Sum(s => s.TotalTimeVisibleMs);
        var totalSeconds = totalMs / 1000;
        var fallbackSeconds = fallbackMs / 1000;
        var appSwitchCount = sessionCounts.Values.Sum();
        var appsUsed = packageNames.Length;
        var failedBatchCount = batches.Count(b => MobileSyncBatchStatus.IsFailed(b.FailedCount, b.Status));

        var ranking = summaryRows
            .GroupBy(s => s.PackageName)
            .Select(group =>
            {
                var foregroundSeconds = group.Sum(s => s.TotalTimeVisibleMs) / 1000;
                var latest = group.Max(s => s.LastTimeUsedUtc);
                var app = appCatalog.GetValueOrDefault(group.Key);
                var source = group.Any(s => !IsFallbackSource(s.SourceKind)) ? "events" : "fallback";
                return new MobileAppUsageSummaryDto(
                    group.Key,
                    app?.DisplayName ?? group.Key,
                    app?.Category,
                    foregroundSeconds,
                    sessionCounts.GetValueOrDefault(group.Key),
                    launchCounts.GetValueOrDefault(group.Key),
                    latest,
                    source,
                    totalSeconds > 0 ? foregroundSeconds / (double)totalSeconds : 0);
            })
            .OrderByDescending(item => item.ForegroundSeconds)
            .ThenBy(item => item.PackageName)
            .Take(50)
            .ToList();

        var syncBatches = batches
            .OrderByDescending(b => b.CreatedAt)
            .Take(20)
            .Select(b => new MobileSyncBatchSummaryDto(
                b.Id,
                b.DeviceId,
                b.BatchId,
                b.WindowStartUtc,
                b.WindowEndUtc,
                b.CompletedAtUtc ?? b.CreatedAt,
                b.Status,
                b.AcceptedCount,
                b.SkippedCount,
                b.RejectedCount,
                locationPoints.Count(p => p.DeviceId == b.DeviceId
                    && p.RecordedAtUtc >= b.WindowStartUtc
                    && p.RecordedAtUtc < b.WindowEndUtc
                    && !string.Equals(p.Quality, "rejected", StringComparison.OrdinalIgnoreCase)),
                locationPoints.Count(p => p.DeviceId == b.DeviceId
                    && p.RecordedAtUtc >= b.WindowStartUtc
                    && p.RecordedAtUtc < b.WindowEndUtc
                    && string.Equals(p.Quality, "rejected", StringComparison.OrdinalIgnoreCase)),
                MobileSyncBatchEnvelopeCodec.ErrorMessage(b.ErrorJson)))
            .ToList();

        return new MobileUsageSummaryResponse(
            DateLabel(query.RangeStartUtc),
            query.DeviceId,
            _timeProvider.GetUtcNow(),
            totalSeconds,
            fallbackSeconds,
            appSwitchCount,
            appsUsed,
            Completeness(totalSeconds, fallbackSeconds, failedBatchCount),
            batches.Count == 0 ? null : batches.Max(b => b.CompletedAtUtc ?? b.CreatedAt),
            ranking,
            syncBatches,
            failedBatchCount);
    }

    public async Task<MobileTimelineResponse> GetTimelineAsync(MobileTimelineQuery query, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var page = MobileTimelinePagination.ClampPage(query.Page);
        var pageSize = MobileTimelinePagination.ClampPageSize(query.PageSize);

        var sessions = _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId);

        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            sessions = sessions.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            sessions = sessions.Where(s => s.EndUtc == null || s.EndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            sessions = sessions.Where(s => s.StartUtc < query.RangeEndUtc);

        // #330：先取总数再分页，让调用方能判断「是否还有数据」——
        // 旧实现在这里硬编码 Take(500) 且不报告总数，导致下午/晚间数据静默丢失。
        var sessionTotal = await sessions.CountAsync(ct);

        var summaries = _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            summaries = summaries.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            summaries = summaries.Where(s => s.WindowEndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            summaries = summaries.Where(s => s.WindowStartUtc < query.RangeEndUtc);

        var fallbackQuery = WhereFallbackSummaries(summaries);
        var fallbackTotal = await fallbackQuery.CountAsync(ct);

        // 会话与 fallback 汇总在时间上交错，必须作为**同一条合并流**分页：
        // 若两者各自 Skip/Take，第 2 页的会话可能早于第 1 页的汇总，
        // 客户端逐页拼接会得到乱序时间线（review 发现）。
        var totalCount = sessionTotal + fallbackTotal;

        // skip 用 long 计算：page 来自查询串，int 溢出会变成负 OFFSET，
        // 在 PostgreSQL 上直接报错而不是返回空页（review 发现）。
        var skip = ((long)page - 1) * pageSize;

        // 合并流要定位第 N 页必须读过其之前的行，读取成本是 O(skip)。
        // 超过可读偏移上限时明确拒绝，避免单次请求物化整段历史（review 指出）。
        if (skip > MobileTimelinePagination.MaxReadableOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"分页偏移过大（skip={skip}）：最多可读取 {MobileTimelinePagination.MaxReadableOffset} 条之前的数据，"
                + "请缩小日期范围后按日期分段请求。");
        }

        if (skip >= totalCount)
        {
            return new MobileTimelineResponse(
                DateLabel(query.RangeStartUtc),
                query.DeviceId,
                _timeProvider.GetUtcNow(),
                [],
                [],
                [],
                page,
                pageSize,
                totalCount,
                sessionTotal,
                fallbackTotal,
                false,
                false);
        }

        // 合并流的前 skip+pageSize 行必然落在「两个来源各自前 skip+pageSize 行」的并集内，
        // 因此只需从每个来源取这么多行，在内存里归并后再切片 —— 结果与
        // 「UNION ALL 后统一 ORDER BY 再 OFFSET/LIMIT」等价，但不用把全天数据读进来，
        // 也避开了 EF 无法把跨实体类型的 UNION 翻译成 SQL 的限制。
        var mergedBudget = skip + pageSize;
        var mergedRows = await LoadMergedRowsAsync(sessions, fallbackQuery, mergedBudget, ct);

        var packageNames = mergedRows
            .Select(row => row.PackageName)
            .Distinct()
            .ToArray();
        var appCatalog = await AppCatalog(userId, query.DeviceId, packageNames, ct);

        var pageItems = mergedRows
            .Skip((int)skip)
            .Take(pageSize)
            .Select(row => ToTimelineItem(row, appCatalog))
            .ToList();

        // 向后兼容：老客户端只读 sessions / fallbackSummaries。
        // 两者都是**当前页**的子集，与 items 完全一致，不再各自独立分页。
        var sessionItems = pageItems
            .Where(item => string.Equals(item.Kind, "session", StringComparison.Ordinal))
            .ToList();
        var fallbackItems = pageItems
            .Where(item => string.Equals(item.Kind, "fallback", StringComparison.Ordinal))
            .ToList();

        // 合并流分页后，hasMore 只有一个含义：后面还有页。
        var hasMore = skip + pageItems.Count < totalCount;

        return new MobileTimelineResponse(
            DateLabel(query.RangeStartUtc),
            query.DeviceId,
            _timeProvider.GetUtcNow(),
            sessionItems,
            fallbackItems,
            pageItems,
            page,
            pageSize,
            totalCount,
            sessionTotal,
            fallbackTotal,
            hasMore,
            hasMore);
    }

    /// <summary>
    /// 合并流的中间投影：会话与 fallback 汇总投影成同一形状，才能在同一条流里排序分页。
    /// </summary>
    private sealed record TimelineRow(
        Guid Id,
        string Kind,
        string DeviceId,
        string PackageName,
        DateTimeOffset Start,
        DateTimeOffset? End,
        long? DurationMs);

    /// <summary>
    /// 按时间归并两个来源、取前 <paramref name="budget"/> 行。
    /// <para>
    /// 两个来源各自只取前 <paramref name="budget"/> 行：合并流的前 budget 行必然落在
    /// 这个并集内（任一来源排在第 budget 名之后的行，前面已有该来源的 budget 行，
    /// 不可能进入全局前 budget）。因此结果与「UNION ALL 后统一排序再切片」等价，
    /// 但只需读取 O(budget) 行，而不是把全天数据物化进内存。
    /// </para>
    /// </summary>
    private static async Task<List<TimelineRow>> LoadMergedRowsAsync(
        IQueryable<MobileUsageSessionEntity> sessions,
        IQueryable<MobileUsageSummaryEntity> fallbackSummaries,
        long budget,
        CancellationToken ct)
    {
        var take = (int)Math.Min(budget, int.MaxValue);

        var sessionRows = await sessions
            .OrderBy(s => s.StartUtc)
            .ThenBy(s => s.Id)
            .Take(take)
            .Select(s => new TimelineRow(
                s.Id,
                "session",
                s.DeviceId,
                s.PackageName,
                s.StartUtc,
                s.EndUtc,
                s.DurationMs))
            .ToListAsync(ct);

        var fallbackRows = await fallbackSummaries
            .OrderBy(s => s.WindowStartUtc)
            .ThenBy(s => s.Id)
            .Take(take)
            .Select(s => new TimelineRow(
                s.Id,
                "fallback",
                s.DeviceId,
                s.PackageName,
                s.WindowStartUtc,
                s.WindowEndUtc,
                s.TotalTimeVisibleMs))
            .ToListAsync(ct);

        return sessionRows
            .Concat(fallbackRows)
            .OrderBy(row => row.Start)
            .ThenBy(row => row.Id)
            .ToList();
    }

    private static MobileTimelineItemDto ToTimelineItem(
        TimelineRow row,
        IReadOnlyDictionary<string, MobileAppCatalogEntity> appCatalog)
    {
        if (string.Equals(row.Kind, "session", StringComparison.Ordinal))
        {
            var durationMs = row.DurationMs ?? DurationMs(row.Start, row.End);
            return new MobileTimelineItemDto(
                row.Id.ToString("N"),
                "session",
                row.DeviceId,
                row.PackageName,
                DisplayName(appCatalog, row.PackageName),
                row.Start,
                row.End,
                Math.Max(0, durationMs / 1000),
                "events",
                1,
                string.Empty);
        }

        // fallback 汇总：可见时长不能超过声明的窗口长度，否则会虚报使用时间。
        var windowMs = Math.Max(0, (row.End!.Value - row.Start).TotalMilliseconds);
        var effectiveMs = Math.Min(row.DurationMs ?? 0, (long)windowMs);
        return new MobileTimelineItemDto(
            row.Id.ToString("N"),
            "fallback",
            row.DeviceId,
            row.PackageName,
            DisplayName(appCatalog, row.PackageName),
            row.Start,
            row.End,
            Math.Max(0, effectiveMs / 1000),
            "fallback",
            0.6,
            "汇总数据");
    }

    private async Task<Dictionary<string, MobileAppCatalogEntity>> AppCatalog(
        Guid userId,
        string? deviceId,
        IReadOnlyCollection<string> packageNames,
        CancellationToken ct)
    {
        if (packageNames.Count == 0)
            return new Dictionary<string, MobileAppCatalogEntity>();

        var query = _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(app => app.UserId == userId && packageNames.Contains(app.PackageName));

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(app => app.DeviceId == deviceId);

        return await query
            .GroupBy(app => app.PackageName)
            .Select(group => group.OrderByDescending(app => app.UpdatedAt).First())
            .ToDictionaryAsync(app => app.PackageName, ct);
    }

    public static IQueryable<MobileUsageSummaryEntity> WhereFallbackSummaries(
        IQueryable<MobileUsageSummaryEntity> summaries)
        => summaries.Where(s =>
            s.SourceKind.ToLower().Contains("fallback")
            || s.SourceKind.ToLower().Contains("summary"));

    private async Task<Dictionary<string, int>> SessionCounts(Guid userId, MobileSummaryQuery query, CancellationToken ct)
    {
        var sessions = _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            sessions = sessions.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            sessions = sessions.Where(s => s.EndUtc == null || s.EndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            sessions = sessions.Where(s => s.StartUtc < query.RangeEndUtc);

        return (await sessions
                .Select(s => s.PackageName)
                .ToListAsync(ct))
            .GroupBy(packageName => packageName)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<Dictionary<string, int>> LaunchCounts(Guid userId, MobileSummaryQuery query, CancellationToken ct)
    {
        var events = _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.EventType.Contains("FOREGROUND"));
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            events = events.Where(e => e.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            events = events.Where(e => e.EventTimestampUtc >= query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            events = events.Where(e => e.EventTimestampUtc < query.RangeEndUtc);

        return (await events
                .Select(e => e.PackageName)
                .ToListAsync(ct))
            .GroupBy(packageName => packageName)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<List<MobileSyncBatchEntity>> SyncBatches(Guid userId, MobileSummaryQuery query, CancellationToken ct)
    {
        var batches = _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            batches = batches.Where(b => b.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            batches = batches.Where(b => b.WindowEndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            batches = batches.Where(b => b.WindowStartUtc < query.RangeEndUtc);

        return await batches.ToListAsync(ct);
    }

    private async Task<List<MobileLocationPointEntity>> LocationPoints(Guid userId, MobileSummaryQuery query, CancellationToken ct)
    {
        var points = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(p => p.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            points = points.Where(p => p.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            points = points.Where(p => p.RecordedAtUtc >= query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            points = points.Where(p => p.RecordedAtUtc < query.RangeEndUtc);

        return await points.ToListAsync(ct);
    }

    private static bool IsFallbackSource(string sourceKind)
        => sourceKind.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            || sourceKind.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private static double Completeness(long totalSeconds, long fallbackSeconds, int issueCount)
    {
        if (totalSeconds <= 0)
            return 0;

        var dataScore = fallbackSeconds > 0 && fallbackSeconds >= totalSeconds ? 0.65 : 1;
        var issuePenalty = Math.Min(0.3, issueCount * 0.1);
        return Math.Max(0, Math.Round(dataScore - issuePenalty, 2));
    }

    /// <summary>
    /// 业务日标签。汇总与时间线共用（#330 起两者的查询类型不同，但日期口径必须一致）。
    /// </summary>
    private static string DateLabel(DateTimeOffset? rangeStartUtc)
        => BusinessDay.FormatDate(BusinessDay.GetBusinessDate(rangeStartUtc ?? DateTimeOffset.UtcNow));

    private static string DisplayName(IReadOnlyDictionary<string, MobileAppCatalogEntity> apps, string packageName)
        => apps.TryGetValue(packageName, out var app) && !string.IsNullOrWhiteSpace(app.DisplayName)
            ? app.DisplayName
            : packageName;

    private static long DurationMs(DateTimeOffset start, DateTimeOffset? end)
        => end is null ? 0 : Convert.ToInt64((end.Value - start).TotalMilliseconds);

    private static List<MobileUsageSummaryEntity> DeduplicateSummaries(List<MobileUsageSummaryEntity> summaries)
    {
        var shanghai = BusinessDay.TimeZone;
        return summaries
            .GroupBy(s =>
            {
                var localStart = TimeZoneInfo.ConvertTime(s.WindowStartUtc, shanghai);
                var hourKey = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0);
                return (DeviceId: s.DeviceId, Package: s.PackageName.ToLowerInvariant(), HourStart: hourKey);
            })
            .Select(g => g.OrderByDescending(s => s.TotalTimeVisibleMs).First())
            .ToList();
    }
}
