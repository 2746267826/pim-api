using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public partial class PcTrackerService
{
    private static readonly HashSet<string> AllowedTrackerEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "window", "idle", "gap", "web-page"
    };

    public async Task<int> UploadTrackerEventsAsync(TrackerEventsUploadRequest req, CancellationToken ct)
    {
        if (req.Events.Count > MaxTrackerEventsPerUpload)
            throw new ArgumentException($"Tracker uploads are limited to {MaxTrackerEventsPerUpload} events.", nameof(req));

        if (string.IsNullOrWhiteSpace(req.DeviceId))
            throw new ArgumentException("DeviceId is required.", nameof(req));

        var now = DateTimeOffset.UtcNow;
        var entities = new List<TrackerEventEntity>(req.Events.Count);
        foreach (var e in req.Events)
        {
            if (!AllowedTrackerEventTypes.Contains(e.EventType))
                throw new ArgumentException($"Invalid eventType '{e.EventType}'. Allowed: window, idle, gap, web-page", nameof(req));

            if (!TryParseTimestamp(e.Timestamp, out var timestamp))
                throw new ArgumentException($"Invalid timestamp '{e.Timestamp}'.", nameof(req));

            if (!DateTime.TryParseExact(e.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                throw new ArgumentException($"Invalid date '{e.Date}'. Expected YYYY-MM-DD.", nameof(req));

            if (e.Duration < 0)
                throw new ArgumentException($"Duration must be >=0, got {e.Duration}.", nameof(req));

            if (timestamp > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new ArgumentException($"Timestamp '{e.Timestamp}' is in the future.", nameof(req));

            var rawJson = e.RawJson is null ? "{}" : JsonSerializer.Serialize(e.RawJson, ApiJsonSerializerOptions);

            entities.Add(new TrackerEventEntity
            {
                DeviceId = req.DeviceId,
                Timestamp = timestamp,
                Duration = e.Duration,
                EventType = e.EventType.ToLowerInvariant(),
                ExePath = e.ExePath,
                AppName = e.AppName,
                DisplayName = e.DisplayName,
                WindowTitle = e.WindowTitle,
                CommandLine = e.CommandLine,
                IsIdle = e.IsIdle,
                IsMediaActive = e.IsMediaActive,
                Url = e.Url,
                Domain = e.Domain,
                PagePath = e.PagePath,
                Audible = e.Audible,
                Incognito = e.Incognito,
                TabCount = e.TabCount,
                PageVisitCount = e.PageVisitCount,
                PageVisitDuration = e.PageVisitDuration,
                RawJson = rawJson,
                CreatedAt = now,
                Date = date.Date
            });
        }

        if (entities.Count == 0) return 0;

        // Deduplication: same device + timestamp + duration + eventType + appName equivalent to aw logic
        var minTs = entities.Min(x => x.Timestamp);
        var maxTs = entities.Max(x => x.Timestamp);
        var existing = await _db.Set<TrackerEventEntity>()
            .Where(x => x.DeviceId == req.DeviceId && x.Timestamp >= minTs && x.Timestamp < maxTs.AddTicks(1))
            .Select(x => new { x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath })
            .ToListAsync(ct);
        var existingKeys = existing.Select(x => MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath)).ToHashSet();

        var toInsert = entities.Where(x => existingKeys.Add(MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath))).ToList();
        if (toInsert.Count == 0) return 0;

        _db.Set<TrackerEventEntity>().AddRange(toInsert);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            // retry once: re-evaluate dedup against fresh db state
            var retryExisting = await _db.Set<TrackerEventEntity>()
                .Where(x => x.DeviceId == req.DeviceId && x.Timestamp >= minTs && x.Timestamp < maxTs.AddTicks(1))
                .Select(x => new { x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath })
                .ToListAsync(ct);
            var retryKeys = retryExisting.Select(x => MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath)).ToHashSet();
            var retryInsert = entities.Where(x => retryKeys.Add(MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.WindowTitle, x.ExePath))).ToList();
            if (retryInsert.Count == 0) return 0;
            _db.Set<TrackerEventEntity>().AddRange(retryInsert);
            await _db.SaveChangesAsync(ct);
            return retryInsert.Count;
        }

        return toInsert.Count;
    }

    public async Task RecordTrackerHealthAsync(TrackerHealthRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId))
            throw new ArgumentException("DeviceId is required.", nameof(req));

        var allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running", "degraded", "error" };
        if (!allowedStatuses.Contains(req.Status))
            throw new ArgumentException($"Invalid status '{req.Status}'.", nameof(req));

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Set<TrackerHealthEntity>()
            .FirstOrDefaultAsync(x => x.DeviceId == req.DeviceId, ct);

        if (existing is null)
        {
            existing = new TrackerHealthEntity
            {
                DeviceId = req.DeviceId,
                CreatedAt = now
            };
            _db.Set<TrackerHealthEntity>().Add(existing);
        }

        existing.Status = req.Status.ToLowerInvariant();
        existing.UptimeSeconds = req.UptimeSeconds;
        existing.HookActive = req.HookActive;
        existing.PollCount = req.PollCount;
        existing.SessionsCreated = req.SessionsCreated;
        existing.EventsUploaded = req.EventsUploaded;
        existing.UploadFailures = req.UploadFailures;
        existing.LastError = req.LastError;
        existing.BrowserConnected = req.BrowserConnected;
        existing.BrowserHeartbeatAgeSeconds = req.BrowserHeartbeatAgeSeconds;
        existing.ReportedAt = now;
        existing.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            var retry = await _db.Set<TrackerHealthEntity>().FirstOrDefaultAsync(x => x.DeviceId == req.DeviceId, ct);
            if (retry is null) throw;
            retry.Status = existing.Status;
            retry.UptimeSeconds = existing.UptimeSeconds;
            retry.HookActive = existing.HookActive;
            retry.PollCount = existing.PollCount;
            retry.SessionsCreated = existing.SessionsCreated;
            retry.EventsUploaded = existing.EventsUploaded;
            retry.UploadFailures = existing.UploadFailures;
            retry.LastError = existing.LastError;
            retry.BrowserConnected = existing.BrowserConnected;
            retry.BrowserHeartbeatAgeSeconds = existing.BrowserHeartbeatAgeSeconds;
            retry.ReportedAt = now;
            retry.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<TrackerHealthEntity?> GetTrackerHealthAsync(string deviceId, CancellationToken ct)
    {
        return await _db.Set<TrackerHealthEntity>()
            .FirstOrDefaultAsync(x => x.DeviceId == deviceId, ct);
    }

    public async Task<List<TrackerEventEntity>> QueryTrackerEventsAsync(string deviceId, DateTime from, DateTime to, CancellationToken ct)
    {
        var start = new DateTimeOffset(from.Date, TimeSpan.Zero);
        var end = new DateTimeOffset(to.Date.AddDays(1), TimeSpan.Zero);
        return await _db.Set<TrackerEventEntity>()
            .Where(x => x.DeviceId == deviceId && x.Timestamp >= start && x.Timestamp < end)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct);
    }

    // Timeline generation for tracker events – reuses BrowserPageTimelineBuilder logic but sources from tracker table
    public async Task<List<PcDetailRecord>> GetTrackerDetailRecordsAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = BusinessDayStart(date);
        var dayEnd = dayStart.AddDays(1);
        var events = await _db.Set<TrackerEventEntity>()
            .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var rules = await GetActivityCategoryRulesAsync(ct);
        var records = TrackerPageTimelineBuilder.BuildInterpretedRecords(events, rules);
        return await _classificationSnapshots.EnsureClassificationsAsync(records, rules, auditId: null, ct);
    }

    private static string MakeTrackerKey(DateTimeOffset ts, double duration, string eventType, string? appName, string? title, string? exePath)
        => $"{ts.ToUnixTimeMilliseconds()}|{duration}|{eventType}|{appName}|{title}|{exePath}";

    private async Task<List<PcDetailRecord>> BuildInterpretedTrackerDetailRecordsAsync(List<TrackerEventEntity> events, CancellationToken ct)
    {
        var rules = await GetActivityCategoryRulesAsync(ct);
        var records = TrackerPageTimelineBuilder.BuildInterpretedRecords(events, rules);
        return await _classificationSnapshots.EnsureClassificationsAsync(records, rules, auditId: null, ct);
    }

    private static List<HeatmapBucket> BuildHourlyHeatmapCombined(DateTimeOffset dayStart, List<AwEventEntity> awEvents, List<TrackerEventEntity> trackerEvents)
    {
        var timeZone = ResolveBusinessDayTimeZone();
        var allIntervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var e in awEvents.Where(x => x.Duration > 0))
            allIntervals.Add((e.Timestamp, e.Timestamp.AddSeconds(e.Duration)));
        foreach (var e in trackerEvents.Where(x => x.Duration > 0 && x.EventType == "window"))
            allIntervals.Add((e.Timestamp, e.Timestamp.AddSeconds(e.Duration)));

        var merged = MergeIntervals(allIntervals);
        return Enumerable.Range(0, 24).Select(hour =>
        {
            var bucketStart = dayStart.AddHours(hour);
            var bucketEnd = bucketStart.AddHours(1);
            var inBucketAw = awEvents.Count(e => e.Timestamp >= bucketStart && e.Timestamp < bucketEnd);
            var inBucketTracker = trackerEvents.Count(e => e.Timestamp >= bucketStart && e.Timestamp < bucketEnd);
            var activeMinutes = (int)Math.Min(60, SumOverlapSecondsCombined(merged, bucketStart, bucketEnd) / 60);
            var intensity = activeMinutes switch
            {
                0 => 0,
                <= 5 => 1,
                <= 15 => 2,
                <= 30 => 3,
                <= 45 => 4,
                _ => 5
            };
            var localHour = TimeZoneInfo.ConvertTime(bucketStart, timeZone).Hour;
            return new HeatmapBucket(bucketStart.ToString("O"), bucketEnd.ToString("O"), localHour, activeMinutes, inBucketAw + inBucketTracker, intensity);
        }).ToList();
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0) return new List<(DateTimeOffset, DateTimeOffset)>();
        var sorted = intervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            var cur = sorted[i];
            if (cur.Start <= last.End)
                merged[^1] = (last.Start, cur.End > last.End ? cur.End : last.End);
            else
                merged.Add(cur);
        }
        return merged;
    }

    private static double SumOverlapSecondsCombined(List<(DateTimeOffset Start, DateTimeOffset End)> merged, DateTimeOffset bucketStart, DateTimeOffset bucketEnd)
    {
        double total = 0;
        foreach (var (s, e) in merged)
        {
            var overlapStart = s > bucketStart ? s : bucketStart;
            var overlapEnd = e < bucketEnd ? e : bucketEnd;
            if (overlapEnd > overlapStart)
                total += (overlapEnd - overlapStart).TotalSeconds;
        }
        return total;
    }
}
