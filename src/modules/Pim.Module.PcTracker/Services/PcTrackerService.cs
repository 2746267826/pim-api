using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class PcTrackerService
{
    private const int BusinessDayStartHour = 4;
    private const int MaxCompleteAwEventsPerUpload = 500;
    private const string PostgreSqlUniqueViolationSqlState = "23505";
    private static readonly JsonSerializerOptions ApiJsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PimDbContext _db;
    private List<AppCategoryRule>? _cachedRules;

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

        _db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity
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
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> UploadAwEventsAsync(AwEventsUploadRequest req, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var incoming = req.Events.Select(e => new AwEventEntity
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

        if (incoming.Count == 0) return 0;

        var minTimestamp = incoming.Min(e => e.Timestamp);
        var maxTimestamp = incoming.Max(e => e.Timestamp);
        var existing = await _db.Set<AwEventEntity>()
            .Where(e => e.DeviceId == req.DeviceId && e.Timestamp >= minTimestamp && e.Timestamp <= maxTimestamp)
            .Select(e => new { e.Timestamp, e.Duration, e.EventType, e.AppName, e.WindowTitle, e.AfkStatus })
            .ToListAsync(ct);
        var existingKeys = existing
            .Select(e => MakeAwEventKey(e.Timestamp, e.Duration, e.EventType, e.AppName, e.WindowTitle, e.AfkStatus))
            .ToHashSet();

        var entities = incoming
            .Where(e => existingKeys.Add(MakeAwEventKey(e.Timestamp, e.Duration, e.EventType, e.AppName, e.WindowTitle, e.AfkStatus)))
            .ToList();

        if (entities.Count == 0) return 0;

        _db.Set<AwEventEntity>().AddRange(entities);
        await _db.SaveChangesAsync(ct);
        return entities.Count;
    }

    public async Task<int> UploadCompleteAwEventsAsync(CompleteAwUploadRequest req, CancellationToken ct)
    {
        if (req.Events.Count > MaxCompleteAwEventsPerUpload)
        {
            throw new ArgumentException(
                $"Complete ActivityWatch uploads are limited to {MaxCompleteAwEventsPerUpload} events.",
                nameof(req));
        }

        return await UploadCompleteAwEventsCoreAsync(req, ct, attempt: 0);
    }

    public async Task UpsertKeystatsSampleAsync(KeystatsSampleUploadRequest req, CancellationToken ct)
    {
        if (!TryParseTimestamp(req.SampledAt, out var sampledAt))
            throw new ArgumentException($"Invalid KeyStats sampledAt timestamp: '{req.SampledAt}'.", nameof(req));

        if (!TryParseLocalDateOffset(req.Date, out var statsDateOffset))
            throw new ArgumentException($"Invalid KeyStats date timestamp: '{req.Date}'.", nameof(req));

        await UpsertKeystatsSampleCoreAsync(
            req,
            TruncateToMinute(sampledAt),
            statsDateOffset.Date,
            (int)statsDateOffset.Offset.TotalMinutes,
            ct,
            attempt: 0);
    }

    private async Task UpsertKeystatsSampleCoreAsync(
        KeystatsSampleUploadRequest req,
        DateTimeOffset sampledAtUtc,
        DateTime statsDate,
        int statsTimezoneOffsetMinutes,
        CancellationToken ct,
        int attempt)
    {
        var existing = await _db.Set<KeystatsSampleEntity>()
            .FirstOrDefaultAsync(x => x.PimDeviceId == req.PimDeviceId && x.SampledAtUtc == sampledAtUtc, ct);

        var entity = existing ?? new KeystatsSampleEntity
        {
            PimDeviceId = req.PimDeviceId,
            SampledAtUtc = sampledAtUtc,
            CreatedAt = DateTimeOffset.UtcNow
        };

        entity.StatsDate = statsDate;
        entity.StatsTimezoneOffsetMinutes = statsTimezoneOffsetMinutes;
        entity.KeyPresses = req.KeyPresses;
        entity.LeftClicks = req.LeftClicks;
        entity.RightClicks = req.RightClicks;
        entity.MiddleClicks = req.MiddleClicks;
        entity.SideBackClicks = req.SideBackClicks;
        entity.SideForwardClicks = req.SideForwardClicks;
        entity.MouseDistance = req.MouseDistance;
        entity.ScrollDistance = req.ScrollDistance;
        entity.PeakKps = req.PeakKps;
        entity.PeakCps = req.PeakCps;
        entity.FormattedMouseDistance = req.FormattedMouseDistance;
        entity.FormattedScrollDistance = req.FormattedScrollDistance;
        entity.KeyCountsJson = ToJson(req.KeyPressCounts);
        entity.AppStatsJson = ToApiJson(req.AppStats);
        entity.RawJson = ToApiJson(req);

        if (existing is null)
            _db.Set<KeystatsSampleEntity>().Add(entity);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            await UpsertKeystatsSampleCoreAsync(
                req,
                sampledAtUtc,
                statsDate,
                statsTimezoneOffsetMinutes,
                ct,
                attempt: 1);
        }
    }

    private async Task<int> UploadCompleteAwEventsCoreAsync(CompleteAwUploadRequest req, CancellationToken ct, int attempt)
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = await _db.Set<AwBucketEntity>()
            .FirstOrDefaultAsync(x => x.PimDeviceId == req.PimDeviceId && x.BucketId == req.Bucket.Id, ct);

        if (bucket is null)
        {
            bucket = new AwBucketEntity
            {
                PimDeviceId = req.PimDeviceId,
                AwDeviceId = req.AwInfo?.DeviceId,
                BucketId = req.Bucket.Id,
                Name = req.Bucket.Name,
                BucketType = req.Bucket.Type,
                Client = req.Bucket.Client,
                Hostname = req.Bucket.Hostname,
                CreatedAtSource = ParseOptionalOffset(req.Bucket.Created),
                LastUpdatedSource = ParseOptionalOffset(req.Bucket.LastUpdated),
                DataJson = ToJson(req.Bucket.Data),
                SeenAt = now
            };
            _db.Set<AwBucketEntity>().Add(bucket);
        }
        else
        {
            bucket.AwDeviceId = req.AwInfo?.DeviceId;
            bucket.Name = req.Bucket.Name;
            bucket.BucketType = req.Bucket.Type;
            bucket.Client = req.Bucket.Client;
            bucket.Hostname = req.Bucket.Hostname;
            bucket.LastUpdatedSource = ParseOptionalOffset(req.Bucket.LastUpdated);
            bucket.DataJson = ToJson(req.Bucket.Data);
            bucket.SeenAt = now;
        }

        var sourceIds = req.Events.Select(e => e.SourceEventId).ToList();
        var existingRows = await _db.Set<AwEventEntity>()
            .Where(e => e.DeviceId == req.PimDeviceId
                && e.BucketId == req.Bucket.Id
                && e.SourceEventId != null
                && sourceIds.Contains(e.SourceEventId.Value))
            .ToListAsync(ct);
        var existing = existingRows
            .GroupBy(e => e.SourceEventId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).First());

        var inserted = 0;
        foreach (var incoming in req.Events)
        {
            if (!TryParseTimestamp(incoming.Timestamp, out var timestamp))
                continue;

            var data = incoming.Data ?? new Dictionary<string, object>();
            var app = GetString(data, "app");
            var title = GetString(data, "title");
            var status = GetString(data, "status");

            if (!existing.TryGetValue(incoming.SourceEventId, out var entity))
            {
                entity = new AwEventEntity
                {
                    DeviceId = req.PimDeviceId,
                    CreatedAt = now
                };
                _db.Set<AwEventEntity>().Add(entity);
                existing[incoming.SourceEventId] = entity;
                inserted++;
            }

            entity.AwDeviceId = req.AwInfo?.DeviceId;
            entity.AwHostname = req.AwInfo?.Hostname;
            entity.BucketId = req.Bucket.Id;
            entity.BucketType = req.Bucket.Type;
            entity.BucketClient = req.Bucket.Client;
            entity.SourceEventId = incoming.SourceEventId;
            entity.Timestamp = timestamp;
            entity.Duration = incoming.Duration;
            entity.DataJson = ToJson(data);
            entity.EventType = ClassifyAwEventType(req.Bucket.Type);
            entity.AppName = app;
            entity.AppNameNormalized = AppNameNormalizer.Normalize(app);
            entity.WindowTitle = title;
            entity.AfkStatus = status;
            entity.UpdatedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return await UploadCompleteAwEventsCoreAsync(req, ct, attempt: 1);
        }

        return inserted;
    }

    public async Task<PcSummaryResponse> GetSummaryAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = BusinessDayStart(date);
        var dayEnd = dayStart.AddDays(1);
        var keystats = await LatestKeystatsForDate(date, ct);
        var keystatsSample = keystats is null
            ? await LatestKeystatsSampleForDate(date, ct)
            : null;
        var awEvents = await _db.Set<AwEventEntity>()
            .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd && e.EventType == "window")
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var heatmap = BuildHourlyHeatmap(dayStart, awEvents);
        var timeline = awEvents
            .Where(e => e.AppName is not null)
            .Select(ToTimelineItem)
            .ToList();

        return new PcSummaryResponse(
            keystats is not null ? BuildKeystatsSummary(keystats) : BuildKeystatsSummaryFromSample(keystatsSample),
            heatmap,
            keystats is not null ? BuildAppRanking(keystats) : BuildAppRankingFromSample(keystatsSample),
            timeline,
            BuildSessions(awEvents),
            ComputeDerivedMetrics(keystats, keystatsSample, awEvents),
            await GetCategorySummariesAsync(date, ct));
    }

    public async Task<List<TimelineItem>> GetTimelineAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = BusinessDayStart(date);
        var dayEnd = dayStart.AddDays(1);

        var events = await _db.Set<AwEventEntity>()
            .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd && e.EventType == "window" && e.AppName != null)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        return events.Select(ToTimelineItem).ToList();
    }

    public async Task<List<HeatmapBucket>> GetHeatmapAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var s = BusinessDayStart(start);
        var e = BusinessDayStart(end).AddDays(1);
        var events = await _db.Set<AwEventEntity>()
            .Where(ev => ev.Timestamp >= s && ev.Timestamp < e && ev.EventType == "window")
            .ToListAsync(ct);

        var buckets = new List<HeatmapBucket>();
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            buckets.AddRange(BuildHourlyHeatmap(BusinessDayStart(day), events));
        }
        return buckets;
    }

    public async Task<List<CategorySummary>> GetCategorySummariesAsync(DateTime date, CancellationToken ct)
    {
        var keystats = await LatestKeystatsForDate(date, ct);
        if (keystats is null || !keystats.AppBreakdowns.Any()) return new();

        var rules = await GetCategoryRulesAsync(ct);
        var totals = new Dictionary<string, (int Keys, int Clicks, string Color)>();

        foreach (var app in keystats.AppBreakdowns)
        {
            var category = ClassifyApp(app.AppName, rules);
            var color = GetCategoryColor(category, rules);
            var current = totals.TryGetValue(category, out var existing)
                ? existing
                : (Keys: 0, Clicks: 0, Color: color);
            totals[category] = (
                current.Keys + app.KeyPresses,
                current.Clicks + TotalClicks(app),
                current.Color);
        }

        var grandTotal = totals.Values.Sum(x => x.Keys + x.Clicks);
        return totals
            .OrderByDescending(kv => kv.Value.Keys + kv.Value.Clicks)
            .Take(5)
            .Select(kv => new CategorySummary(
                kv.Key,
                kv.Value.Color,
                grandTotal > 0 ? Math.Round((double)(kv.Value.Keys + kv.Value.Clicks) / grandTotal * 100, 0) : 0,
                kv.Value.Keys,
                kv.Value.Clicks))
            .ToList();
    }

    public async Task<DetailQueryResponse> QueryDetailAsync(DetailQueryParams q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
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
        if (!string.IsNullOrWhiteSpace(q.AppName))
            query = query.Where(x => x.AppBreakdowns.Any(a =>
                a.AppName.ToLower().Contains(q.AppName.ToLower()) ||
                a.DisplayName.ToLower().Contains(q.AppName.ToLower())));
        if (!string.IsNullOrWhiteSpace(q.KeyName))
            query = query.Where(x => x.KeyCounts.Any(k => k.KeyName.ToLower().Contains(q.KeyName.ToLower())));

        var totalCount = await query.CountAsync(ct);
        query = ApplyDetailSort(query, q);

        var entities = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = entities.Select(x => new Dictionary<string, object>
        {
            ["date"] = x.SnapshotDate.ToString("yyyy-MM-dd"),
            ["deviceId"] = x.DeviceId,
            ["keyPresses"] = x.KeyPresses,
            ["totalClicks"] = TotalClicks(x),
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
            items,
            page,
            pageSize,
            totalCount,
            (int)Math.Ceiling((double)totalCount / pageSize));
    }

    public async Task<TypedDetailQueryResponse> QueryCompleteDetailAsync(DetailQueryParams q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var (start, end) = GetDetailQueryRange(q);

        var awEvents = await _db.Set<AwEventEntity>()
            .Where(e => e.Timestamp >= start && e.Timestamp < end)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);
        var samples = await _db.Set<KeystatsSampleEntity>()
            .Where(s => s.SampledAtUtc >= start && s.SampledAtUtc < end)
            .OrderBy(s => s.PimDeviceId)
            .ThenBy(s => s.SampledAtUtc)
            .ToListAsync(ct);
        var rules = await GetCategoryRulesAsync(ct);

        var records = new List<PcDetailRecord>();
        records.AddRange(awEvents.Select(e => ToAwDetailRecord(e, rules)));
        records.AddRange(ToInputMinuteRecords(samples));

        records = ApplyCompleteDetailFilters(records, q).ToList();
        records = ApplyCompleteDetailSort(records, q).ToList();

        var totalCount = records.Count;
        var items = records
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new TypedDetailQueryResponse(
            items,
            page,
            pageSize,
            totalCount,
            (int)Math.Ceiling((double)totalCount / pageSize));
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
        return new AppCategoryRule(entity.Id, entity.AppPattern, entity.CategoryName, entity.Color, entity.Priority, entity.IsBuiltin);
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
            var dayStart = BusinessDayStart(targetDate);
            var dayEnd = dayStart.AddDays(1);
            var awEvents = await _db.Set<AwEventEntity>()
                .Where(e => e.Timestamp >= dayStart && e.Timestamp < dayEnd && e.EventType == "window")
                .ToListAsync(ct);

            var totalAwEvents = awEvents.Count;
            var row = Enumerable.Range(0, 24).Select(h =>
            {
                var bucketStart = dayStart.AddHours(h);
                var bucketEnd = bucketStart.AddHours(1);
                var eventCount = awEvents.Count(e => e.Timestamp >= bucketStart && e.Timestamp < bucketEnd);
                var keyCount = daily is not null && daily.KeyPresses > 0
                    ? totalAwEvents > 0 ? (int)((double)daily.KeyPresses * eventCount / totalAwEvents) : (int)(daily.KeyPresses / 24.0)
                    : 0;
                return new HeatmapBucket(bucketStart.ToString("O"), bucketEnd.ToString("O"), bucketStart.Hour, 0, eventCount, keyCount);
            }).ToList();

            return new HeatmapGridResponse(new List<List<HeatmapBucket>> { row }, dimension, maxKeyCount);
        }

        var grid = new List<List<HeatmapBucket>>();
        var rowDays = new List<HeatmapBucket>();
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            var daily = keystats.FirstOrDefault(x => x.SnapshotDate == day);
            rowDays.Add(new HeatmapBucket(
                new DateTimeOffset(day, TimeSpan.Zero).ToString("O"),
                new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToString("O"),
                (int)day.DayOfWeek,
                0,
                0,
                daily?.KeyPresses ?? 0));

            if (rowDays.Count == 7)
            {
                grid.Add(rowDays);
                rowDays = new List<HeatmapBucket>();
            }
        }

        if (rowDays.Count > 0) grid.Add(rowDays);
        return new HeatmapGridResponse(grid, dimension, maxKeyCount);
    }

    private async Task<KeystatsDailyEntity?> LatestKeystatsForDate(DateTime date, CancellationToken ct)
    {
        return await _db.Set<KeystatsDailyEntity>()
            .Include(x => x.KeyCounts)
            .Include(x => x.AppBreakdowns)
            .Where(x => x.SnapshotDate == date.Date)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<KeystatsSampleEntity?> LatestKeystatsSampleForDate(DateTime date, CancellationToken ct)
    {
        return await _db.Set<KeystatsSampleEntity>()
            .Where(x => x.StatsDate == date.Date)
            .OrderByDescending(x => x.SampledAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public static DateTimeOffset GetBusinessDayStartForQuery(DateTime date)
    {
        var localStart = DateTime.SpecifyKind(date.Date.AddHours(BusinessDayStartHour), DateTimeKind.Local);
        return new DateTimeOffset(localStart).ToUniversalTime();
    }

    private static DateTimeOffset BusinessDayStart(DateTime date) => GetBusinessDayStartForQuery(date);

    private static IQueryable<KeystatsDailyEntity> ApplyDetailSort(
        IQueryable<KeystatsDailyEntity> query,
        DetailQueryParams q)
    {
        var ascending = q.SortDir?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true;
        return (q.SortBy, ascending) switch
        {
            ("keyPresses", true) => query.OrderBy(x => x.KeyPresses),
            ("keyPresses", false) => query.OrderByDescending(x => x.KeyPresses),
            ("totalClicks", true) => query.OrderBy(x => x.LeftClicks + x.RightClicks + x.MiddleClicks + x.SideBackClicks + x.SideForwardClicks),
            ("totalClicks", false) => query.OrderByDescending(x => x.LeftClicks + x.RightClicks + x.MiddleClicks + x.SideBackClicks + x.SideForwardClicks),
            ("date", true) => query.OrderBy(x => x.SnapshotDate),
            _ => query.OrderByDescending(x => x.SnapshotDate)
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetDetailQueryRange(DetailQueryParams q)
    {
        var startDate = DateTime.TryParse(q.DateFrom, out var from)
            ? from.Date
            : DateTime.Today;
        var endDate = DateTime.TryParse(q.DateTo, out var to)
            ? to.Date
            : startDate;

        return (
            BusinessDayStart(startDate),
            BusinessDayStart(endDate).AddDays(1));
    }

    private static PcDetailRecord ToAwDetailRecord(AwEventEntity e, List<AppCategoryRule> rules)
    {
        var normalizedApp = AppNameNormalizer.Normalize(e.AppNameNormalized ?? e.AppName);
        var category = ClassifyApp(normalizedApp, rules);

        return new PcDetailRecord(
            e.EventType,
            FormatUtc(e.Timestamp),
            FormatUtc(e.Timestamp.AddSeconds(e.Duration)),
            e.Duration,
            e.DeviceId,
            e.AppName,
            normalizedApp,
            category,
            e.WindowTitle,
            null,
            null,
            null,
            null,
            null,
            ParseJsonObject(e.DataJson));
    }

    private static IEnumerable<PcDetailRecord> ToInputMinuteRecords(List<KeystatsSampleEntity> samples)
    {
        var previousByDevice = new Dictionary<string, KeystatsSampleEntity>();

        foreach (var sample in samples)
        {
            previousByDevice.TryGetValue(sample.PimDeviceId, out var previous);
            previousByDevice[sample.PimDeviceId] = sample;
            if (previous is null)
                continue;

            var delta = KeystatsDeltaCalculator.Calculate(previous, sample);
            var keyCounts = delta.IsReset
                ? new Dictionary<string, int>()
                : CalculateKeyCountDeltas(previous.KeyCountsJson, sample.KeyCountsJson);
            var durationSeconds = delta.IsGap
                ? (sample.SampledAtUtc - previous.SampledAtUtc).TotalSeconds
                : 60;
            yield return new PcDetailRecord(
                "input-minute",
                FormatUtc(previous.SampledAtUtc),
                FormatUtc(sample.SampledAtUtc),
                durationSeconds,
                sample.PimDeviceId,
                null,
                null,
                null,
                null,
                delta.KeyPresses,
                delta.TotalClicks,
                delta.MouseDistance,
                delta.ScrollDistance,
                keyCounts,
                ParseJsonObject(sample.RawJson));
        }
    }

    private static string FormatUtc(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O");
    }

    private static IEnumerable<PcDetailRecord> ApplyCompleteDetailFilters(
        IEnumerable<PcDetailRecord> records,
        DetailQueryParams q)
    {
        if (!string.IsNullOrWhiteSpace(q.EventType))
        {
            records = records.Where(r => string.Equals(r.RecordType, q.EventType, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(q.DeviceId))
        {
            records = records.Where(r => string.Equals(r.DeviceId, q.DeviceId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q.AppName))
        {
            records = records.Where(r =>
                ContainsIgnoreCase(r.AppName, q.AppName)
                || ContainsIgnoreCase(r.DisplayName, q.AppName));
        }

        if (!string.IsNullOrWhiteSpace(q.CategoryName))
        {
            records = records.Where(r => string.Equals(r.CategoryName, q.CategoryName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q.KeyName))
        {
            records = records.Where(r =>
                r.RecordType == "input-minute"
                && r.KeyCounts is not null
                && r.KeyCounts.Keys.Any(k => ContainsIgnoreCase(k, q.KeyName)));
        }

        return records;
    }

    private static IEnumerable<PcDetailRecord> ApplyCompleteDetailSort(
        IEnumerable<PcDetailRecord> records,
        DetailQueryParams q)
    {
        var ascending = q.SortDir?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true;
        return ascending
            ? records.OrderBy(r => r.Start, StringComparer.Ordinal)
            : records.OrderByDescending(r => r.Start, StringComparer.Ordinal);
    }

    private static object? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, int> ParseKeyCounts(string? keyCountsJson)
    {
        if (string.IsNullOrWhiteSpace(keyCountsJson))
            return new Dictionary<string, int>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(keyCountsJson)
                ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    private static List<AppStatEntry> ParseAppStats(string? appStatsJson)
    {
        if (string.IsNullOrWhiteSpace(appStatsJson))
            return new List<AppStatEntry>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, AppStatEntry>>(appStatsJson, ApiJsonSerializerOptions)
                ?.Values
                .ToList() ?? new List<AppStatEntry>();
        }
        catch (JsonException)
        {
            return new List<AppStatEntry>();
        }
    }

    private static Dictionary<string, int> CalculateKeyCountDeltas(string? previousJson, string? currentJson)
    {
        var previous = ParseKeyCounts(previousJson);
        var current = ParseKeyCounts(currentJson);
        return current
            .Select(kv => new KeyValuePair<string, int>(
                kv.Key,
                Math.Max(0, kv.Value - previous.GetValueOrDefault(kv.Key))))
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static bool ContainsIgnoreCase(string? value, string? search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(search)
            && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static KeystatsSummary? BuildKeystatsSummary(KeystatsDailyEntity? keystats)
    {
        if (keystats is null) return null;
        var totalKeys = keystats.KeyPresses;
        return new KeystatsSummary(
            keystats.SnapshotDate.ToString("yyyy-MM-dd"),
            keystats.KeyPresses,
            TotalClicks(keystats),
            keystats.LeftClicks,
            keystats.RightClicks,
            keystats.MiddleClicks,
            keystats.SideBackClicks,
            keystats.SideForwardClicks,
            keystats.MouseDistance,
            keystats.ScrollDistance,
            keystats.PeakKps,
            keystats.PeakCps,
            keystats.KeyCounts.OrderByDescending(k => k.Count).Take(10)
                .Select(k => new KeyCountItem(k.KeyName, k.Count, totalKeys > 0 ? (double)k.Count / totalKeys : 0))
                .ToList());
    }

    private static KeystatsSummary? BuildKeystatsSummaryFromSample(KeystatsSampleEntity? sample)
    {
        if (sample is null) return null;
        var totalKeys = sample.KeyPresses;
        return new KeystatsSummary(
            sample.StatsDate.ToString("yyyy-MM-dd"),
            sample.KeyPresses,
            TotalClicks(sample),
            sample.LeftClicks,
            sample.RightClicks,
            sample.MiddleClicks,
            sample.SideBackClicks,
            sample.SideForwardClicks,
            sample.MouseDistance,
            sample.ScrollDistance,
            sample.PeakKps,
            sample.PeakCps,
            ParseKeyCounts(sample.KeyCountsJson)
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new KeyCountItem(kv.Key, kv.Value, totalKeys > 0 ? (double)kv.Value / totalKeys : 0))
                .ToList());
    }

    private static List<AppRankingItem> BuildAppRanking(KeystatsDailyEntity? keystats)
    {
        if (keystats is null) return new();
        var maxAppKeys = keystats.AppBreakdowns.Any() ? keystats.AppBreakdowns.Max(a => a.KeyPresses) : 1;
        return keystats.AppBreakdowns
            .OrderByDescending(a => a.KeyPresses + a.LeftClicks + a.RightClicks)
            .Select(a => new AppRankingItem(
                a.AppName,
                a.DisplayName,
                a.KeyPresses,
                TotalClicks(a),
                a.ScrollDistance,
                maxAppKeys > 0 ? (double)a.KeyPresses / maxAppKeys : 0))
            .ToList();
    }

    private static List<AppRankingItem> BuildAppRankingFromSample(KeystatsSampleEntity? sample)
    {
        if (sample is null) return new();
        var appStats = ParseAppStats(sample.AppStatsJson);
        var maxAppKeys = appStats.Any() ? appStats.Max(a => a.KeyPresses) : 1;
        return appStats
            .OrderByDescending(a => a.KeyPresses + a.LeftClicks + a.RightClicks)
            .Select(a => new AppRankingItem(
                a.AppName,
                a.DisplayName,
                a.KeyPresses,
                TotalClicks(a),
                a.ScrollDistance,
                maxAppKeys > 0 ? (double)a.KeyPresses / maxAppKeys : 0))
            .ToList();
    }

    private static List<HeatmapBucket> BuildHourlyHeatmap(DateTimeOffset dayStart, List<AwEventEntity> events)
    {
        return Enumerable.Range(0, 24).Select(hour =>
        {
            var bucketStart = dayStart.AddHours(hour);
            var bucketEnd = bucketStart.AddHours(1);
            var inBucket = events.Where(e => e.Timestamp >= bucketStart && e.Timestamp < bucketEnd).ToList();
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
            return new HeatmapBucket(bucketStart.ToString("O"), bucketEnd.ToString("O"), bucketStart.Hour, activeMinutes, inBucket.Count, intensity);
        }).ToList();
    }

    private static TimelineItem ToTimelineItem(AwEventEntity e)
    {
        return new TimelineItem(
            e.Timestamp.ToString("O"),
            e.Timestamp.AddSeconds(e.Duration).ToString("O"),
            e.Duration / 60,
            e.AppName ?? "unknown",
            e.WindowTitle);
    }

    private async Task<List<AppCategoryRule>> GetCategoryRulesAsync(CancellationToken ct)
    {
        if (_cachedRules is not null) return _cachedRules;
        _cachedRules = await GetAllCategoriesAsync(ct);
        return _cachedRules;
    }

    private static string ClassifyApp(string appName, List<AppCategoryRule> rules)
    {
        foreach (var rule in rules)
        {
            if (string.Equals(appName, rule.AppPattern, StringComparison.OrdinalIgnoreCase))
                return rule.CategoryName;
        }
        return "Other";
    }

    private static string GetCategoryColor(string categoryName, List<AppCategoryRule> rules)
    {
        return rules.FirstOrDefault(r => r.CategoryName == categoryName)?.Color ?? "#8B5CF6";
    }

    private static List<WorkSessionItem> BuildSessions(List<AwEventEntity> events)
    {
        var windowEvents = events.Where(e => e.AppName is not null).OrderBy(e => e.Timestamp).ToList();
        if (windowEvents.Count == 0) return new();

        var result = new List<WorkSessionItem>();
        var sessionStart = windowEvents[0].Timestamp;
        var sessionEnd = sessionStart;
        var appCounts = new Dictionary<string, int>();

        foreach (var ev in windowEvents)
        {
            var gap = (ev.Timestamp - sessionEnd).TotalMinutes;
            if (gap > 15)
            {
                result.Add(MakeSession(sessionStart, sessionEnd, appCounts));
                sessionStart = ev.Timestamp;
                sessionEnd = ev.Timestamp;
                appCounts.Clear();
            }

            sessionEnd = ev.Timestamp.AddSeconds(ev.Duration);
            appCounts[ev.AppName!] = appCounts.GetValueOrDefault(ev.AppName!) + 1;
        }
        result.Add(MakeSession(sessionStart, sessionEnd, appCounts));

        return result.Where(s => s.DurationMinutes >= 5).ToList();
    }

    private static DerivedMetrics ComputeDerivedMetrics(
        KeystatsDailyEntity? keystats,
        KeystatsSampleEntity? keystatsSample,
        List<AwEventEntity> awEvents)
    {
        var windowEvents = awEvents.Where(e => e.EventType == "window" && e.AppName is not null).ToList();
        var afkEvents = awEvents.Where(e => e.EventType == "afk").ToList();
        var totalRecorded = windowEvents.Count > 0
            ? (windowEvents.Max(e => e.Timestamp.AddSeconds(e.Duration)) - windowEvents.Min(e => e.Timestamp)).TotalMinutes
            : 0;
        var idleMin = afkEvents.Where(e => e.AfkStatus == "afk").Sum(e => Math.Min(e.Duration, 3600)) / 60;
        var sessions = BuildSessions(windowEvents);
        var keyPresses = keystats?.KeyPresses ?? keystatsSample?.KeyPresses ?? 0;
        var totalClicks = keystats is not null
            ? TotalClicks(keystats)
            : keystatsSample is not null ? TotalClicks(keystatsSample) : 0;
        var activeInputMin = keyPresses > 0 ? Math.Max(1, keyPresses / 30.0) : 0;

        var appSwitchCount = 0;
        string? previousApp = null;
        foreach (var ev in windowEvents.OrderBy(e => e.Timestamp))
        {
            if (ev.AppName is not null && previousApp is not null && ev.AppName != previousApp)
                appSwitchCount++;
            previousApp = ev.AppName;
        }

        return new DerivedMetrics(
            FormatDuration(totalRecorded),
            FormatDuration(activeInputMin),
            FormatDuration(idleMin),
            sessions.Count,
            windowEvents.Select(e => e.AppName).Distinct().Count(),
            keyPresses,
            totalClicks,
            appSwitchCount,
            totalRecorded > 0 ? Math.Round(appSwitchCount / totalRecorded * 10, 1) : 0,
            windowEvents.OrderByDescending(e => e.Duration).FirstOrDefault()?.AppName ?? "-",
            totalClicks > 0 ? Math.Round((double)keyPresses / totalClicks, 2) : 0);
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

    private static string MakeAwEventKey(
        DateTimeOffset timestamp,
        double duration,
        string eventType,
        string? appName,
        string? windowTitle,
        string? afkStatus)
    {
        return string.Join("|",
            timestamp.ToUniversalTime().ToString("O"),
            Math.Round(duration, 3),
            eventType,
            appName ?? "",
            windowTitle ?? "",
            afkStatus ?? "");
    }

    private static DateTimeOffset? ParseOptionalOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            utc.Minute,
            0,
            TimeSpan.Zero);
    }

    private static bool TryParseLocalDateOffset(string value, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return EnumerateExceptions(ex).Any(e =>
            string.Equals(GetStringProperty(e, "SqlState"), PostgreSqlUniqueViolationSqlState, StringComparison.Ordinal)
            || string.Equals(GetStringProperty(e, "SQLState"), PostgreSqlUniqueViolationSqlState, StringComparison.Ordinal));
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            yield return current;
    }

    private static string? GetStringProperty(Exception ex, string propertyName)
    {
        return ex.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(ex) as string;
    }

    private static string ToJson(object? value)
    {
        return JsonSerializer.Serialize(value ?? new { });
    }

    private static string ToApiJson(object? value)
    {
        return JsonSerializer.Serialize(value ?? new { }, ApiJsonSerializerOptions);
    }

    private static string? GetString(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;

        return value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : value.ToString();
    }

    private static string ClassifyAwEventType(string bucketType)
    {
        return bucketType switch
        {
            "afkstatus" => "afk",
            "web.tab.current" => "web",
            _ => "window"
        };
    }

    private static WorkSessionItem MakeSession(DateTimeOffset start, DateTimeOffset end, Dictionary<string, int> counts)
    {
        var mainApp = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return new WorkSessionItem(
            start.ToString("O"),
            end.ToString("O"),
            Math.Round((end - start).TotalMinutes, 1),
            mainApp.Key ?? "unknown",
            counts.Values.Sum());
    }

    private static int TotalClicks(KeystatsDailyEntity e)
    {
        return e.LeftClicks + e.RightClicks + e.MiddleClicks + e.SideBackClicks + e.SideForwardClicks;
    }

    private static int TotalClicks(KeystatsSampleEntity e)
    {
        return e.LeftClicks + e.RightClicks + e.MiddleClicks + e.SideBackClicks + e.SideForwardClicks;
    }

    private static int TotalClicks(KeystatsAppBreakdownEntity e)
    {
        return e.LeftClicks + e.RightClicks + e.MiddleClicks + e.SideBackClicks + e.SideForwardClicks;
    }

    private static int TotalClicks(AppStatEntry e)
    {
        return e.LeftClicks + e.RightClicks + e.MiddleClicks + e.SideBackClicks + e.SideForwardClicks;
    }
}
