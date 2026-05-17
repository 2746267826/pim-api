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

        return new PcSummaryResponse(ks, heatmap, appRanking, timeline, sessions, null, new List<CategorySummary>());
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
