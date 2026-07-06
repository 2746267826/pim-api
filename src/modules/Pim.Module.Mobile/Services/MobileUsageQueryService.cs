using Microsoft.EntityFrameworkCore;
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

        var summaryRows = await summaries.ToListAsync(ct);
        var deviceIds = summaryRows.Select(s => s.DeviceId).Distinct().ToArray();
        var packageNames = summaryRows.Select(s => s.PackageName).Distinct().ToArray();
        var appCatalog = await AppCatalog(userId, query.DeviceId, packageNames, ct);
        var sessionCounts = await SessionCounts(userId, query, ct);
        var launchCounts = await LaunchCounts(userId, query, ct);
        var batches = await SyncBatches(userId, query, ct);

        var totalMs = summaryRows.Sum(s => s.TotalTimeVisibleMs);
        var fallbackMs = summaryRows
            .Where(s => IsFallbackSource(s.SourceKind))
            .Sum(s => s.TotalTimeVisibleMs);
        var totalSeconds = totalMs / 1000;
        var fallbackSeconds = fallbackMs / 1000;
        var appSwitchCount = sessionCounts.Values.Sum();
        var appsUsed = packageNames.Length;
        var failedBatchCount = batches.Count(b => !string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || b.FailedCount > 0);

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
                0,
                0,
                b.FailedCount,
                b.ErrorJson is "{}" or "" ? null : b.ErrorJson))
            .ToList();

        return new MobileUsageSummaryResponse(
            DateLabel(query),
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

    public async Task<MobileTimelineResponse> GetTimelineAsync(MobileSummaryQuery query, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var sessions = _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId);

        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            sessions = sessions.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            sessions = sessions.Where(s => s.EndUtc == null || s.EndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            sessions = sessions.Where(s => s.StartUtc < query.RangeEndUtc);

        var sessionRows = await sessions
            .OrderBy(s => s.StartUtc)
            .Take(500)
            .ToListAsync(ct);

        var summaries = _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId && IsFallbackSource(s.SourceKind));
        if (!string.IsNullOrWhiteSpace(query.DeviceId))
            summaries = summaries.Where(s => s.DeviceId == query.DeviceId);
        if (query.RangeStartUtc is not null)
            summaries = summaries.Where(s => s.WindowEndUtc > query.RangeStartUtc);
        if (query.RangeEndUtc is not null)
            summaries = summaries.Where(s => s.WindowStartUtc < query.RangeEndUtc);

        var fallbackRows = await summaries
            .OrderBy(s => s.WindowStartUtc)
            .Take(500)
            .ToListAsync(ct);

        var packageNames = sessionRows.Select(s => s.PackageName)
            .Concat(fallbackRows.Select(s => s.PackageName))
            .Distinct()
            .ToArray();
        var appCatalog = await AppCatalog(userId, query.DeviceId, packageNames, ct);

        var sessionItems = sessionRows
            .Select(s => new MobileTimelineItemDto(
                s.Id.ToString("N"),
                "session",
                s.DeviceId,
                s.PackageName,
                DisplayName(appCatalog, s.PackageName),
                s.StartUtc,
                s.EndUtc,
                Math.Max(0, (s.DurationMs ?? DurationMs(s.StartUtc, s.EndUtc)) / 1000),
                "events",
                1,
                string.Empty))
            .ToList();

        var fallbackItems = fallbackRows
            .Select(s => new MobileTimelineItemDto(
                s.Id.ToString("N"),
                "fallback",
                s.DeviceId,
                s.PackageName,
                DisplayName(appCatalog, s.PackageName),
                s.WindowStartUtc,
                s.WindowEndUtc,
                Math.Max(0, s.TotalTimeVisibleMs / 1000),
                "fallback",
                0.6,
                "汇总数据"))
            .ToList();

        var items = sessionItems
            .Concat(fallbackItems)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.PackageName)
            .ToList();

        return new MobileTimelineResponse(
            DateLabel(query),
            query.DeviceId,
            _timeProvider.GetUtcNow(),
            sessionItems,
            fallbackItems,
            items);
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

        return await sessions
            .GroupBy(s => s.PackageName)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
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

        return await events
            .GroupBy(e => e.PackageName)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
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

    private static string DateLabel(MobileSummaryQuery query)
        => (query.RangeStartUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd");

    private static string DisplayName(IReadOnlyDictionary<string, MobileAppCatalogEntity> apps, string packageName)
        => apps.TryGetValue(packageName, out var app) && !string.IsNullOrWhiteSpace(app.DisplayName)
            ? app.DisplayName
            : packageName;

    private static long DurationMs(DateTimeOffset start, DateTimeOffset? end)
        => end is null ? 0 : Convert.ToInt64((end.Value - start).TotalMilliseconds);
}
