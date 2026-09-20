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

    private static readonly HashSet<string> AllowedBrowserTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "edge", "firefox", "safari", "other"
    };

    private const int MaxBrowserLength = 16;
    private const int MaxInstanceIdLength = 128;

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

            timestamp = TruncateToMillisecond(timestamp);

            if (!DateTime.TryParseExact(e.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                throw new ArgumentException($"Invalid date '{e.Date}'. Expected YYYY-MM-DD.", nameof(req));

            if (e.Duration < 0)
                throw new ArgumentException($"Duration must be >=0, got {e.Duration}.", nameof(req));

            // 时长必须与起点同一精度（毫秒），否则事件结束时刻会落在非整毫秒上。
            // 客户端分段离散化（NativeTrackerService.SessionToEvents）产出的片段是
            // "上一段结束 = 下一段开始"的相接关系：下一段起点就是上一段的精确结束时刻，
            // 再被 TruncateToMillisecond 归一化到毫秒。若这里只对时长单独取整，
            // 结束时刻与"下一段起点向下取整"会差到 1 tick，产生亚毫秒重叠或空洞 ——
            // 前者被 S1（同类型事件不重叠）误判为真实违规（实测 24h 内 146 对全部 <1ms）。
            // 因此改为**先把结束边界归一化到毫秒、再反推时长**：这样结束时刻与
            // "下一段起点向下取整"在数学上恒等，相接片段必然首尾对齐。
            var normalizedDuration = NormalizeDurationToMillisecond(timestamp, e.Duration);

            if (timestamp > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new ArgumentException($"Timestamp '{e.Timestamp}' is in the future.", nameof(req));

            var browserNorm = NormalizeBrowser(e.Browser, nameof(req));
            var instanceIdNorm = NormalizeInstanceId(e.InstanceId, nameof(req));

            var canonicalBusinessDate = GetBusinessDayForTimestamp(timestamp);
            var rawJson = e.RawJson is null ? "{}" : JsonSerializer.Serialize(e.RawJson, ApiJsonSerializerOptions);

            entities.Add(new TrackerEventEntity
            {
                DeviceId = req.DeviceId,
                Timestamp = timestamp,
                Duration = normalizedDuration,
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
                Browser = browserNorm,
                InstanceId = instanceIdNorm,
                RawJson = rawJson,
                CreatedAt = now,
                Date = canonicalBusinessDate
            });
        }

        if (entities.Count == 0) return 0;

        // Deduplication: same device + timestamp + duration + eventType + appName + browser + instanceId
        var minTs = entities.Min(x => x.Timestamp);
        var maxTs = entities.Max(x => x.Timestamp);
        var existing = await _db.Set<TrackerEventEntity>()
            .Where(x => x.DeviceId == req.DeviceId && x.Timestamp >= minTs && x.Timestamp <= maxTs)
            .Select(x => new { x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId })
            .ToListAsync(ct);
        var existingKeys = existing.Select(x => MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId)).ToHashSet();

        var toInsert = entities.Where(x => existingKeys.Add(MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId))).ToList();
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
                .Where(x => x.DeviceId == req.DeviceId && x.Timestamp >= minTs && x.Timestamp <= maxTs)
                .Select(x => new { x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId })
                .ToListAsync(ct);
            var retryKeys = retryExisting.Select(x => MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId)).ToHashSet();
            var retryInsert = entities.Where(x => retryKeys.Add(MakeTrackerKey(x.Timestamp, x.Duration, x.EventType, x.AppName, x.Browser, x.InstanceId))).ToList();
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
        existing.SiteConnected = req.SiteConnected;
        existing.SiteLastEventAgeSeconds = req.SiteLastEventAgeSeconds;
        existing.SiteEventsUploaded = req.SiteEventsUploaded;
        existing.SiteLastError = req.SiteLastError;
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

    /// <summary>最近一次上报的采集健康（跨设备取最新），供 Web 状态页展示。</summary>
    public async Task<TrackerHealthEntity?> GetLatestTrackerHealthAsync(CancellationToken ct)
    {
        return await _db.Set<TrackerHealthEntity>()
            .OrderByDescending(x => x.ReportedAt)
            .FirstOrDefaultAsync(ct);
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

    // Deliberately coalesce browser/instanceId nulls to "" to mirror the DB unique
    // index semantics: ux_tracker_events_dedup is a COALESCE(browser,'')/COALESCE(instance_id,'')
    // expression index so legacy rows (NULL browser/instance_id) keep dedup.
    private static string MakeTrackerKey(DateTimeOffset ts, double duration, string eventType, string? appName, string? browser = null, string? instanceId = null)
        => $"{ts.ToUnixTimeMilliseconds()}|{duration.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}|{eventType}|{appName}|{browser ?? ""}|{instanceId ?? ""}";

    private static string? NormalizeBrowser(string? browser, string paramName)
    {
        if (browser is null) return null;
        var trimmed = browser.Trim();
        if (trimmed.Length > MaxBrowserLength)
            throw new ArgumentException($"Browser too long (max {MaxBrowserLength}): '{browser}'", paramName);
        if (trimmed.Length == 0) return null;
        var lower = trimmed.ToLowerInvariant();
        return AllowedBrowserTypes.Contains(lower) ? lower : "other";
    }

    private static string? NormalizeInstanceId(string? instanceId, string paramName)
    {
        if (instanceId is null) return null;
        var trimmed = instanceId.Trim();
        if (trimmed.Length > MaxInstanceIdLength)
            throw new ArgumentException($"InstanceId too long (max {MaxInstanceIdLength}): '{instanceId}'", paramName);
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static DateTimeOffset TruncateToMillisecond(DateTimeOffset dto)
    {
        // Use UTC milliseconds to stay consistent with MakeTrackerKey's ToUnixTimeMilliseconds
        return DateTimeOffset.FromUnixTimeMilliseconds(dto.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 把时长归一化到整毫秒，使事件**结束时刻**落在整毫秒上（#254 S1）。
    ///
    /// 为什么需要它：客户端分段离散化（<c>NativeTrackerService.SessionToEvents</c>）产出的
    /// 相接片段满足 "B.start == A.end"，而 B.start 会被 <see cref="TruncateToMillisecond(DateTimeOffset)"/>
    /// 截断到毫秒。若 A 的时长保留浮点全精度，A.end 就会带亚毫秒尾数、比 B.start 大出
    /// 约 100 微秒，被 S1（同类型事件不重叠）判为真实违规（实测 24h 内 146 对，全部 &lt;1ms）。
    ///
    /// 实现要点：先算结束边界、截断到毫秒，再以**整毫秒**为单位反推时长。
    /// 直接对时长做 <c>Math.Floor(x*1000)/1000</c> 是不够的 —— 那个值经
    /// <see cref="DateTimeOffset.AddSeconds"/> 还原时会因浮点舍入差 1 个 tick，
    /// 相接片段依旧对不齐。以 <c>k / 1000.0</c>（k 为整数毫秒）表示时长后，
    /// <c>AddSeconds</c> 内部换算 <c>k * 10^4</c> 个 tick 是精确的，首尾相接得以严格成立。
    ///
    /// 结果：消除亚毫秒重叠且不产生空洞；单条事件最多损失 1 毫秒时长，用户不可感知。
    /// 非正时长原样返回（零时长是合法的"瞬时事件"）。
    /// </summary>
    private static double NormalizeDurationToMillisecond(DateTimeOffset start, double durationSeconds)
    {
        if (durationSeconds <= 0) return durationSeconds;

        var normalizedEnd = TruncateToMillisecond(start.AddSeconds(durationSeconds));
        var wholeMilliseconds = (long)Math.Round(
            (normalizedEnd - start).TotalMilliseconds,
            MidpointRounding.AwayFromZero);

        // 归一只允许把时长缩小（结束边界向下对齐），不得放大到超出原始区间。
        if (wholeMilliseconds <= 0) return 0;
        return wholeMilliseconds / 1000.0;
    }

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
