using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileGapService
{
    private static readonly TimeSpan MaxBackfillAge = TimeSpan.FromDays(14);
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
            .Select(e => new CoverageWindow(e.SourceWindowStartUtc, e.SourceWindowEndUtc))
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
            .ToListAsync(ct);

        var completedBatchWindows = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId
                && b.DeviceId == request.DeviceId
                && b.WindowEndUtc > start
                && b.WindowStartUtc < end
                && b.FailedCount == 0
                && b.Status == "completed")
            .Select(b => new CoverageWindow(b.WindowStartUtc, b.WindowEndUtc))
            .ToListAsync(ct);

        var nonFallbackSummaryWindows = summaries
            .Where(summary => !IsFallbackSource(summary.SourceKind))
            .Select(summary => new CoverageWindow(summary.WindowStartUtc, summary.WindowEndUtc));
        var coverageWindows = eventWindows
            .Concat(nonFallbackSummaryWindows)
            .Concat(completedBatchWindows)
            .ToList();
        var fallbackWindows = summaries
            .Where(summary => IsFallbackSource(summary.SourceKind))
            .Select(summary => new CoverageWindow(summary.WindowStartUtc, summary.WindowEndUtc))
            .ToList();

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
            var coveredUntil = ContinuousCoverageEnd(overlappingCoverage, cursor, windowEnd);
            if (coveredUntil >= windowEnd)
            {
                cursor = windowEnd;
                continue;
            }

            var hasFallback = fallbackWindows.Any(window => window.EndUtc > cursor && window.StartUtc < windowEnd);
            var gapStart = coveredUntil > cursor ? coveredUntil : cursor;
            var reason = Reason(cursor, windowEnd, gapStart, end, hasFallback);
            windows.Add(new MobileGapWindowDto(
                gapStart,
                windowEnd,
                reason,
                request.CapabilitiesJson));

            cursor = windowEnd;
        }

        return new MobileGapResponse(maxBackfillStart, windows);
    }

    private static CoverageWindow Clip(CoverageWindow window, DateTimeOffset start, DateTimeOffset end)
        => new(
            window.StartUtc < start ? start : window.StartUtc,
            window.EndUtc > end ? end : window.EndUtc);

    private static DateTimeOffset ContinuousCoverageEnd(
        IReadOnlyCollection<CoverageWindow> windows,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var coveredUntil = start;
        foreach (var window in windows)
        {
            if (window.EndUtc <= coveredUntil)
                continue;
            if (window.StartUtc > coveredUntil)
                break;

            coveredUntil = window.EndUtc;
            if (coveredUntil >= end)
                return end;
        }

        return coveredUntil;
    }

    private static string Reason(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset gapStart,
        DateTimeOffset requestEnd,
        bool hasFallback)
    {
        if (gapStart > windowStart && windowEnd == requestEnd)
            return "missing-tail";
        if (gapStart > windowStart)
            return "partial-day";

        return hasFallback ? "fallback-only" : "missing-day";
    }

    private static bool IsFallbackSource(string sourceKind)
        => sourceKind.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            || sourceKind.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private sealed record CoverageWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
