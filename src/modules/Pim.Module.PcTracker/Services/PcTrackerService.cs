using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class PcTrackerService
{
    private readonly PimDbContext _db;

    public PcTrackerService(PimDbContext db)
    {
        _db = db;
    }

    public async Task UpsertKeystatsAsync(KeystatsUploadRequest req, CancellationToken ct)
    {
        var snapshotDate = DateTimeOffset.Parse(req.Date).Date;

        var existing = await _db.Set<KeystatsDailyEntity>()
            .Include(x => x.KeyCounts)
            .Include(x => x.AppBreakdowns)
            .FirstOrDefaultAsync(x => x.DeviceId == req.DeviceId && x.SnapshotDate == snapshotDate, ct);

        if (existing is not null)
        {
            _db.Set<KeystatsKeyCountEntity>().RemoveRange(existing.KeyCounts);
            _db.Set<KeystatsAppBreakdownEntity>().RemoveRange(existing.AppBreakdowns);
            _db.Set<KeystatsDailyEntity>().Remove(existing);
        }

        var entity = new KeystatsDailyEntity
        {
            DeviceId = req.DeviceId,
            SnapshotDate = snapshotDate,
            KeyPresses = req.KeyPresses,
            LeftClicks = req.LeftClicks,
            RightClicks = req.RightClicks,
            MiddleClicks = req.MiddleClicks,
            SideBackClicks = req.SideBackClicks,
            SideForwardClicks = req.SideForwardClicks,
            MouseDistance = req.MouseDistance,
            ScrollDistance = req.ScrollDistance,
            PeakKps = req.PeakKps,
            PeakCps = req.PeakCps,
            KeyCounts = req.KeyPressCounts?.Select(kv => new KeystatsKeyCountEntity
            {
                KeyName = kv.Key,
                Count = kv.Value
            }).ToList() ?? new(),
            AppBreakdowns = req.AppStats?.Select(kv => new KeystatsAppBreakdownEntity
            {
                AppName = kv.Value.AppName,
                DisplayName = kv.Value.DisplayName,
                KeyPresses = kv.Value.KeyPresses,
                LeftClicks = kv.Value.LeftClicks,
                RightClicks = kv.Value.RightClicks,
                MiddleClicks = kv.Value.MiddleClicks,
                SideBackClicks = kv.Value.SideBackClicks,
                SideForwardClicks = kv.Value.SideForwardClicks,
                ScrollDistance = kv.Value.ScrollDistance
            }).ToList() ?? new()
        };

        _db.Set<KeystatsDailyEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> UploadAwEventsAsync(AwEventsUploadRequest req, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var entities = req.Events.Select(e => new AwEventEntity
        {
            DeviceId = req.DeviceId,
            Timestamp = DateTimeOffset.Parse(e.Timestamp),
            Duration = e.Duration,
            EventType = e.EventType,
            AppName = e.AppName,
            WindowTitle = e.WindowTitle,
            AfkStatus = e.AfkStatus,
            CreatedAt = now
        }).ToList();

        _db.Set<AwEventEntity>().AddRange(entities);
        await _db.SaveChangesAsync(ct);
        return entities.Count;
    }

    public async Task<PcSummaryResponse> GetSummaryAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(date.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        // Keystats
        var keystats = await _db.Set<KeystatsDailyEntity>()
            .Include(x => x.KeyCounts)
            .Include(x => x.AppBreakdowns)
            .Where(x => x.SnapshotDate == date.Date)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        KeystatsSummary? ks = null;
        List<AppRankingItem> appRanking = new();
        if (keystats is not null)
        {
            var totalKeys = keystats.KeyPresses;
            ks = new KeystatsSummary(
                keystats.SnapshotDate.ToString("yyyy-MM-dd"),
                keystats.KeyPresses,
                keystats.LeftClicks + keystats.RightClicks + keystats.MiddleClicks + keystats.SideBackClicks + keystats.SideForwardClicks,
                keystats.LeftClicks, keystats.RightClicks, keystats.MiddleClicks,
                keystats.SideBackClicks, keystats.SideForwardClicks,
                keystats.MouseDistance, keystats.ScrollDistance,
                keystats.PeakKps, keystats.PeakCps,
                keystats.KeyCounts.OrderByDescending(k => k.Count).Take(10)
                    .Select(k => new KeyCountItem(k.KeyName, k.Count, totalKeys > 0 ? (double)k.Count / totalKeys : 0)).ToList()
            );

            var maxAppKeys = keystats.AppBreakdowns.Any()
                ? keystats.AppBreakdowns.Max(a => a.KeyPresses)
                : 1;
            appRanking = keystats.AppBreakdowns
                .OrderByDescending(a => a.KeyPresses + a.LeftClicks + a.RightClicks)
                .Select(a => new AppRankingItem(
                    a.AppName, a.DisplayName,
                    a.KeyPresses,
                    a.LeftClicks + a.RightClicks + a.MiddleClicks + a.SideBackClicks + a.SideForwardClicks,
                    a.ScrollDistance,
                    maxAppKeys > 0 ? (double)a.KeyPresses / maxAppKeys : 0
                )).ToList();
        }

        // Heatmap (hourly aggregation of AW events)
        var awEvents = await _db.Set<AwEventEntity>()
            .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd && e.EventType == "window")
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var heatmap = Enumerable.Range(0, 24).Select(hour =>
        {
            var bucketStart = dayStart.AddHours(hour);
            var bucketEnd = bucketStart.AddHours(1);
            var inBucket = awEvents.Where(e => e.Timestamp >= bucketStart && e.Timestamp < bucketEnd).ToList();
            var activeMinutes = (int)Math.Min(60, inBucket.Sum(e => Math.Min(e.Duration, 3600)) / 60);
            var intensity = activeMinutes switch
            {
                0 => 0,
                <= 5 => 1,
                <= 15 => 2,
                <= 30 => 3,
                <= 45 => 4,
                _ => 5
            };
            return new HeatmapBucket(
                bucketStart.ToString("O"), bucketEnd.ToString("O"),
                hour, activeMinutes, inBucket.Count, intensity);
        }).ToList();

        // Timeline
        var timeline = awEvents
            .Where(e => e.AppName is not null)
            .Select(e => new TimelineItem(
                e.Timestamp.ToString("O"),
                e.Timestamp.AddSeconds(e.Duration).ToString("O"),
                e.Duration / 60,
                e.AppName ?? "unknown",
                e.WindowTitle
            )).ToList();

        // Work sessions (merge consecutive AW events, split by AFK > 15 min gap)
        var sessions = BuildSessions(awEvents);

        var metrics = await ComputeDerivedMetricsAsync(date, keystats, awEvents, ct);
        var categories = await GetCategorySummariesAsync(date, ct);
        return new PcSummaryResponse(ks, heatmap, appRanking, timeline, sessions, metrics, categories);
    }

    public async Task<List<TimelineItem>> GetTimelineAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(date.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var events = await _db.Set<AwEventEntity>()
            .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd && e.EventType == "window" && e.AppName != null)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        return events.Select(e => new TimelineItem(
            e.Timestamp.ToString("O"),
            e.Timestamp.AddSeconds(e.Duration).ToString("O"),
            e.Duration / 60,
            e.AppName ?? "unknown",
            e.WindowTitle
        )).ToList();
    }

    public async Task<List<HeatmapBucket>> GetHeatmapAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var s = new DateTimeOffset(start.Date, TimeSpan.Zero);
        var e = new DateTimeOffset(end.Date.AddDays(1), TimeSpan.Zero);

        var events = await _db.Set<AwEventEntity>()
            .Where(ev => ev.Timestamp >= s && ev.Timestamp < e && ev.EventType == "window")
            .ToListAsync(ct);

        var days = (end.Date - start.Date).Days + 1;
        var buckets = new List<HeatmapBucket>();

        for (int d = 0; d < days; d++)
        {
            var day = s.AddDays(d);
            for (int h = 0; h < 24; h++)
            {
                var bucketStart = day.AddHours(h);
                var bucketEnd = bucketStart.AddHours(1);
                var inBucket = events.Where(ev => ev.Timestamp >= bucketStart && ev.Timestamp < bucketEnd).ToList();
                var activeMinutes = (int)Math.Min(60, inBucket.Sum(ev => Math.Min(ev.Duration, 3600)) / 60);
                var intensity = activeMinutes switch { 0 => 0, <= 5 => 1, <= 15 => 2, <= 30 => 3, <= 45 => 4, _ => 5 };
                buckets.Add(new HeatmapBucket(
                    bucketStart.ToString("O"), bucketEnd.ToString("O"),
                    h, activeMinutes, inBucket.Count, intensity));
            }
        }
        return buckets;
    }

    public async Task<List<CategorySummary>> GetCategorySummariesAsync(DateTime date, CancellationToken ct)
    {
        var keystats = await _db.Set<KeystatsDailyEntity>()
            .Include(x => x.AppBreakdowns)
            .Where(x => x.SnapshotDate == date.Date)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (keystats is null || !keystats.AppBreakdowns.Any()) return new();

        var rules = await GetCategoryRulesAsync(ct);
        var categoryTotals = new Dictionary<string, (int Keys, int Clicks, string Color)>();

        foreach (var app in keystats.AppBreakdowns)
        {
            var cat = ClassifyApp(app.AppName, rules);
            var color = GetCategoryColor(cat, rules);
            if (!categoryTotals.ContainsKey(cat))
                categoryTotals[cat] = (0, 0, color);
            var cur = categoryTotals[cat];
            categoryTotals[cat] = (cur.Keys + app.KeyPresses,
                cur.Clicks + app.LeftClicks + app.RightClicks + app.MiddleClicks + app.SideBackClicks + app.SideForwardClicks,
                cur.Color);
        }

        var grandTotal = categoryTotals.Values.Sum(c => c.Keys + c.Clicks);
        return categoryTotals
            .OrderByDescending(kv => kv.Value.Keys + kv.Value.Clicks)
            .Take(5)
            .Select(kv => new CategorySummary(
                kv.Key, kv.Value.Color,
                grandTotal > 0 ? Math.Round((double)(kv.Value.Keys + kv.Value.Clicks) / grandTotal * 100, 0) : 0,
                kv.Value.Keys, kv.Value.Clicks
            )).ToList();
    }

    public async Task<DetailQueryResponse> QueryDetailAsync(DetailQueryParams q, CancellationToken ct)
    {
        var query = _db.Set<KeystatsDailyEntity>()
            .Include(x => x.KeyCounts)
            .Include(x => x.AppBreakdowns)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q.DateFrom) && DateTime.TryParse(q.DateFrom, out var from))
            query = query.Where(x => x.SnapshotDate >= from.Date);
        if (!string.IsNullOrWhiteSpace(q.DateTo) && DateTime.TryParse(q.DateTo, out var to))
            query = query.Where(x => x.SnapshotDate <= to.Date);
        if (!string.IsNullOrWhiteSpace(q.DeviceId))
            query = query.Where(x => x.DeviceId == q.DeviceId);

        var totalCount = await query.CountAsync(ct);
        var entities = await query
            .OrderByDescending(x => x.SnapshotDate)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        var items = entities.Select(x => new Dictionary<string, object>
        {
            ["date"] = x.SnapshotDate.ToString("yyyy-MM-dd"),
            ["deviceId"] = x.DeviceId,
            ["keyPresses"] = x.KeyPresses,
            ["totalClicks"] = x.LeftClicks + x.RightClicks + x.MiddleClicks + x.SideBackClicks + x.SideForwardClicks,
            ["leftClicks"] = x.LeftClicks,
            ["rightClicks"] = x.RightClicks,
            ["middleClicks"] = x.MiddleClicks,
            ["mouseDistance"] = x.MouseDistance,
            ["scrollDistance"] = x.ScrollDistance,
            ["peakKps"] = x.PeakKps,
            ["peakCps"] = x.PeakCps,
            ["apps"] = x.AppBreakdowns.Select(a => a.DisplayName ?? a.AppName).ToList(),
            ["topKeys"] = x.KeyCounts.OrderByDescending(k => k.Count).Take(5)
                .Select(k => new { k.KeyName, k.Count }).ToList()
        }).ToList();

        return new DetailQueryResponse(
            items, q.Page, q.PageSize, totalCount,
            (int)Math.Ceiling((double)totalCount / q.PageSize));
    }

    public async Task<List<AppCategoryRule>> GetAllCategoriesAsync(CancellationToken ct)
    {
        return await _db.Set<AppCategoryEntity>()
            .OrderByDescending(r => r.Priority)
            .Select(r => new AppCategoryRule(r.Id, r.AppPattern, r.CategoryName, r.Color, r.Priority, r.IsBuiltin))
            .ToListAsync(ct);
    }

    public async Task<AppCategoryRule> SaveCategoryAsync(SaveCategoryRequest req, CancellationToken ct)
    {
        var entity = await _db.Set<AppCategoryEntity>()
            .FirstOrDefaultAsync(e => e.AppPattern == req.AppPattern, ct);

        if (entity is not null)
        {
            entity.CategoryName = req.CategoryName;
            entity.Color = req.Color;
            entity.Priority = req.Priority;
        }
        else
        {
            entity = new AppCategoryEntity
            {
                AppPattern = req.AppPattern,
                CategoryName = req.CategoryName,
                Color = req.Color,
                Priority = req.Priority,
                IsBuiltin = false
            };
            _db.Set<AppCategoryEntity>().Add(entity);
        }

        await _db.SaveChangesAsync(ct);
        _cachedRules = null;

        return new AppCategoryRule(entity.Id, entity.AppPattern, entity.CategoryName,
            entity.Color, entity.Priority, entity.IsBuiltin);
    }

    public async Task<bool> DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<AppCategoryEntity>().FindAsync(new object[] { id }, ct);
        if (entity is null || entity.IsBuiltin) return false;
        _db.Set<AppCategoryEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        _cachedRules = null;
        return true;
    }

    public async Task<HeatmapGridResponse> GetHeatmapGridAsync(DateTime start, DateTime end, string dimension, CancellationToken ct)
    {
        var keystats = await _db.Set<KeystatsDailyEntity>()
            .Where(x => x.SnapshotDate >= start.Date && x.SnapshotDate <= end.Date)
            .ToListAsync(ct);

        var maxKeyCount = keystats.Any() ? keystats.Max(x => x.KeyPresses) : 1;

        if (dimension == "hour")
        {
            var targetDate = start.Date;
            var daily = keystats.FirstOrDefault(x => x.SnapshotDate == targetDate);
            var grid = new List<List<HeatmapBucket>> { new() };
            for (int h = 0; h < 24; h++)
            {
                var bucketStart = new DateTimeOffset(targetDate.AddHours(h), TimeSpan.Zero);
                var bucketEnd = bucketStart.AddHours(1);
                int keyCount = daily is not null ? (int)(daily.KeyPresses / 24.0) : 0;
                grid[0].Add(new HeatmapBucket(bucketStart.ToString("O"), bucketEnd.ToString("O"),
                    h, 0, 0, keyCount));
            }
            return new HeatmapGridResponse(grid, dimension, maxKeyCount);
        }

        var days = (end.Date - start.Date).Days + 1;
        var grid2 = new List<List<HeatmapBucket>>();
        var row = new List<HeatmapBucket>();
        for (int d = 0; d < days; d++)
        {
            var day = start.Date.AddDays(d);
            var daily = keystats.FirstOrDefault(x => x.SnapshotDate == day);
            row.Add(new HeatmapBucket(
                new DateTimeOffset(day, TimeSpan.Zero).ToString("O"),
                new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToString("O"),
                (int)day.DayOfWeek,
                0, 0,
                daily?.KeyPresses ?? 0));

            if (row.Count == 7)
            {
                grid2.Add(row);
                row = new List<HeatmapBucket>();
            }
        }
        if (row.Count > 0) grid2.Add(row);

        return new HeatmapGridResponse(grid2, dimension, maxKeyCount);
    }

    private List<AppCategoryRule>? _cachedRules;

    private async Task<List<AppCategoryRule>> GetCategoryRulesAsync(CancellationToken ct)
    {
        if (_cachedRules is not null) return _cachedRules;
        _cachedRules = await _db.Set<AppCategoryEntity>()
            .OrderByDescending(r => r.Priority)
            .Select(r => new AppCategoryRule(r.Id, r.AppPattern, r.CategoryName, r.Color, r.Priority, r.IsBuiltin))
            .ToListAsync(ct);
        return _cachedRules;
    }

    private static string ClassifyApp(string appName, List<AppCategoryRule> rules)
    {
        foreach (var rule in rules)
        {
            if (string.Equals(appName, rule.AppPattern, StringComparison.OrdinalIgnoreCase))
                return rule.CategoryName;
        }
        return "其他";
    }

    private static string GetCategoryColor(string categoryName, List<AppCategoryRule> rules)
    {
        return rules.FirstOrDefault(r => r.CategoryName == categoryName)?.Color ?? "#8B5CF6";
    }

    private static List<WorkSessionItem> BuildSessions(List<AwEventEntity> events)
    {
        if (events.Count == 0) return new();

        var result = new List<WorkSessionItem>();
        var sessionStart = events[0].Timestamp;
        var sessionEnd = sessionStart;
        var currentApp = events[0].AppName;
        var appCounts = new Dictionary<string, int>();

        foreach (var ev in events.Where(e => e.AppName is not null))
        {
            var gap = (ev.Timestamp - sessionEnd).TotalMinutes;
            if (gap > 15)
            {
                result.Add(MakeSession(sessionStart, sessionEnd, appCounts));
                sessionStart = ev.Timestamp;
                sessionEnd = ev.Timestamp;
                currentApp = ev.AppName;
                appCounts.Clear();
            }

            sessionEnd = ev.Timestamp.AddSeconds(ev.Duration);
            if (ev.AppName is not null)
            {
                appCounts[ev.AppName] = appCounts.GetValueOrDefault(ev.AppName) + 1;
            }
        }
        result.Add(MakeSession(sessionStart, sessionEnd, appCounts));

        return result.Where(s => s.DurationMinutes >= 5).ToList();
    }

    private async Task<DerivedMetrics> ComputeDerivedMetricsAsync(
        DateTime date, KeystatsDailyEntity? keystats, List<AwEventEntity> awEvents, CancellationToken ct)
    {
        var windowEvents = awEvents.Where(e => e.EventType == "window" && e.AppName is not null).ToList();
        var afkEvents = awEvents.Where(e => e.EventType == "afk").ToList();

        var totalRecorded = windowEvents.Count > 0
            ? (windowEvents.Max(e => e.Timestamp.AddSeconds(e.Duration)) -
               windowEvents.Min(e => e.Timestamp)).TotalMinutes
            : 0;

        double activeInputMin = 0;
        if (keystats is not null)
            activeInputMin = Math.Max(1, keystats.KeyPresses / 30.0);

        var idleMin = afkEvents
            .Where(e => e.AfkStatus == "afk")
            .Sum(e => Math.Min(e.Duration, 3600)) / 60;

        var sessions = BuildSessions(windowEvents);
        var sessionCount = sessions.Count;

        var activeApps = windowEvents.Select(e => e.AppName).Distinct().Count();

        var keyPresses = keystats?.KeyPresses ?? 0;

        var totalClicks = keystats is not null
            ? keystats.LeftClicks + keystats.RightClicks + keystats.MiddleClicks +
              keystats.SideBackClicks + keystats.SideForwardClicks
            : 0;

        var appSwitchCount = 0;
        string? prevApp = null;
        foreach (var ev in windowEvents.OrderBy(e => e.Timestamp))
        {
            if (ev.AppName is not null && prevApp is not null && ev.AppName != prevApp)
                appSwitchCount++;
            prevApp = ev.AppName;
        }

        var switchFreq = totalRecorded > 0 ? Math.Round(appSwitchCount / totalRecorded * 10, 1) : 0;

        var longestApp = windowEvents
            .Where(e => e.AppName is not null)
            .OrderByDescending(e => e.Duration)
            .FirstOrDefault()?.AppName ?? "—";

        var ratio = totalClicks > 0 ? Math.Round((double)keyPresses / totalClicks, 2) : 0;

        return new DerivedMetrics(
            FormatDuration(totalRecorded),
            FormatDuration(activeInputMin),
            FormatDuration(idleMin),
            sessionCount,
            activeApps,
            keyPresses,
            totalClicks,
            appSwitchCount,
            switchFreq,
            longestApp,
            ratio
        );
    }

    private static string FormatDuration(double minutes)
    {
        if (minutes <= 0) return "0m";
        if (minutes >= 60)
        {
            var h = (int)(minutes / 60);
            var m = (int)(minutes % 60);
            return m > 0 ? $"{h}h {m}m" : $"{h}h";
        }
        return $"{Math.Round(minutes)}m";
    }

    private static WorkSessionItem MakeSession(DateTimeOffset start, DateTimeOffset end, Dictionary<string, int> counts)
    {
        var mainApp = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return new WorkSessionItem(
            start.ToString("O"), end.ToString("O"),
            Math.Round((end - start).TotalMinutes, 1),
            mainApp.Key ?? "unknown",
            counts.Values.Sum()
        );
    }
}
