using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileGapService
{
    /// <summary>
    /// 补传窗口上限。孤儿批次回收器用同一份口径判断"这批数据是否已经不可能再回来"（#568），
    /// 因此不能再写成两份常量。
    /// </summary>
    internal static readonly TimeSpan MaxBackfillAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan MinGapThreshold = TimeSpan.FromMinutes(5);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileGapService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<MobileGapResponse> GetGapsAsync(MobileGapRequest request, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);

        var now = _timeProvider.GetUtcNow();
        var maxBackfillStart = now - MaxBackfillAge;
        var start = request.RangeStartUtc < maxBackfillStart ? maxBackfillStart : request.RangeStartUtc;
        var end = request.RangeEndUtc > now ? now : request.RangeEndUtc;
        if (end <= start)
            return new MobileGapResponse(maxBackfillStart, Array.Empty<MobileGapWindowDto>());

        var eventWindows = await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == request.DeviceId
                && e.SourceWindowEndUtc > start
                && e.SourceWindowStartUtc < end)
            .Select(e => new { e.SourceWindowStartUtc, e.SourceWindowEndUtc })
            .Distinct()
            .ToListAsync(ct);

        var summaries = await _db.Set<MobileUsageSummaryEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && s.DeviceId == request.DeviceId
                && s.WindowEndUtc > start
                && s.WindowStartUtc < end)
            .Select(s => new
            {
                s.WindowStartUtc,
                s.WindowEndUtc,
                s.SourceKind
            })
            .Distinct()
            .ToListAsync(ct);

        // 覆盖判据只看"这个批次到底有没有失败条目"，不看批次状态字符串（#242）：
        // 历史上条目级拒绝会把批次标成 completed-with-errors，从而让已成功入库的整段时段被持续回报为缺口。
        var completedBatchWindows = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId
                && b.DeviceId == request.DeviceId
                && b.WindowEndUtc > start
                && b.WindowStartUtc < end
                && b.FailedCount == 0
                && (b.Status == MobileSyncBatchStatus.Completed
                    || b.Status == MobileSyncBatchStatus.LegacyCompletedWithErrors))
            .Select(b => new { b.WindowStartUtc, b.WindowEndUtc })
            .Distinct()
            .ToListAsync(ct);

        var nonFallbackSummaryWindows = summaries
            .Where(summary => !IsFallbackSource(summary.SourceKind))
            .Select(summary => new CoverageWindow(summary.WindowStartUtc, summary.WindowEndUtc));
        var coverageWindows = MergeCoverageWindows(eventWindows
            .Select(window => new CoverageWindow(window.SourceWindowStartUtc, window.SourceWindowEndUtc))
            .Concat(nonFallbackSummaryWindows)
            .Concat(completedBatchWindows.Select(window => new CoverageWindow(window.WindowStartUtc, window.WindowEndUtc))));
        var fallbackWindows = summaries
            .Where(summary => IsFallbackSource(summary.SourceKind))
            .Select(summary => new CoverageWindow(summary.WindowStartUtc, summary.WindowEndUtc));
        var mergedFallbackWindows = MergeCoverageWindows(fallbackWindows);

        var windows = new List<MobileGapWindowDto>();
        var cursor = start;
        while (cursor < end)
        {
            var windowEnd = cursor.AddDays(1);
            if (windowEnd > end)
                windowEnd = end;

            var overlappingCoverage = coverageWindows
                .Where(window => window.EndUtc > cursor && window.StartUtc < windowEnd)
                .Select(window => Clip(window, cursor, windowEnd))
                .OrderBy(window => window.StartUtc)
                .ToList();
            var dayCursor = cursor;
            foreach (var coverage in overlappingCoverage)
            {
                if (coverage.StartUtc > dayCursor)
                    AddGapWindow(
                        windows,
                        request,
                        dayCursor,
                        coverage.StartUtc,
                        cursor,
                        windowEnd,
                        end,
                        HasFallback(mergedFallbackWindows, dayCursor, coverage.StartUtc));

                if (coverage.EndUtc > dayCursor)
                    dayCursor = coverage.EndUtc;
            }

            if (dayCursor < windowEnd)
                AddGapWindow(
                    windows,
                    request,
                    dayCursor,
                    windowEnd,
                    cursor,
                    windowEnd,
                    end,
                    HasFallback(mergedFallbackWindows, dayCursor, windowEnd));

            cursor = windowEnd;
        }

        return new MobileGapResponse(maxBackfillStart, windows);
    }

    private static CoverageWindow Clip(CoverageWindow window, DateTimeOffset start, DateTimeOffset end)
        => new(
            window.StartUtc < start ? start : window.StartUtc,
            window.EndUtc > end ? end : window.EndUtc);

    private static List<CoverageWindow> MergeCoverageWindows(IEnumerable<CoverageWindow> windows)
    {
        var merged = new List<CoverageWindow>();
        foreach (var window in windows
                     .Where(window => window.EndUtc > window.StartUtc)
                     .OrderBy(window => window.StartUtc)
                     .ThenByDescending(window => window.EndUtc))
        {
            if (merged.Count == 0)
            {
                merged.Add(window);
                continue;
            }

            var last = merged[^1];
            if (window.StartUtc <= last.EndUtc)
            {
                if (window.EndUtc > last.EndUtc)
                    merged[^1] = new CoverageWindow(last.StartUtc, window.EndUtc);
                continue;
            }

            merged.Add(window);
        }

        return merged;
    }

    private static void AddGapWindow(
        ICollection<MobileGapWindowDto> windows,
        MobileGapRequest request,
        DateTimeOffset gapStart,
        DateTimeOffset gapEnd,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset requestEnd,
        bool hasFallback)
    {
        if (gapEnd - gapStart < MinGapThreshold)
            return;

        var reason = gapStart > dayStart && gapEnd == dayEnd && dayEnd == requestEnd
            ? "missing-tail"
            : gapStart > dayStart
                ? "partial-day"
                : hasFallback
                    ? "fallback-only"
                    : "missing-day";

        windows.Add(new MobileGapWindowDto(
            gapStart,
            gapEnd,
            reason,
            request.CapabilitiesJson));
    }

    private static bool HasFallback(
        IReadOnlyCollection<CoverageWindow> fallbackWindows,
        DateTimeOffset start,
        DateTimeOffset end)
        => fallbackWindows.Any(window => window.EndUtc > start && window.StartUtc < end);

    private static bool IsFallbackSource(string sourceKind)
        => sourceKind.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            || sourceKind.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private sealed record CoverageWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
