using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileUsageAggregationService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileAnalyticsQueryService _queryService;
    private readonly MobileUsageGoalService _goalService;
    private readonly TimeProvider _timeProvider;
    private readonly MobileAppClassificationService? _classificationService;

    public MobileUsageAggregationService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileAnalyticsQueryService queryService,
        MobileUsageGoalService goalService,
        TimeProvider timeProvider,
        MobileAppClassificationService? classificationService = null)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
        _goalService = goalService;
        _timeProvider = timeProvider;
        _classificationService = classificationService;
    }

    public async Task<MobileAnalyticsOverviewResponse> GetOverviewAsync(
        MobileAnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var timeZoneInfo = _queryService.ResolveTimezone(context.Range.Timezone);
        var rows = await LoadRowsAsync(context, ct);
        var qualityRows = context.IncludeSystemNoise
            ? rows
            : await LoadRowsAsync(context with { IncludeSystemNoise = true }, ct);
        var totalSeconds = rows.Sum(row => row.ForegroundSeconds);
        var qualityTotalSeconds = qualityRows.Sum(row => row.ForegroundSeconds);
        var localDayCount = Math.Max(1, CountLocalDays(context.Range));
        var dayBuckets = SplitRowsIntoBuckets(rows, timeZoneInfo, TimeSpan.FromDays(1), "day");
        var hourBuckets = SplitRowsIntoBuckets(rows, timeZoneInfo, TimeSpan.FromHours(1), "hour");
        var byLocalDay = dayBuckets
            .GroupBy(row => row.LocalDate)
            .Select(group => new { Date = group.Key, Seconds = group.Sum(row => row.BucketSeconds) })
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.Date, StringComparer.Ordinal)
            .ToList();
        var byHour = hourBuckets
            .GroupBy(row => row.LocalHour)
            .Select(group => new { Hour = group.Key, Seconds = group.Sum(row => row.BucketSeconds) })
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.Hour)
            .ToList();

        var goal = await FirstGoalProgressAsync(totalSeconds, ct);
        var anomalies = BuildAnomalies(rows, totalSeconds);
        var suggestions = BuildSuggestions(rows);
        var fallbackSeconds = rows.Where(row => row.Source == "fallback").Sum(row => row.ForegroundSeconds);
        var qualityFallbackSeconds = qualityRows.Where(row => row.Source == "fallback").Sum(row => row.ForegroundSeconds);
        var systemNoiseSeconds = qualityRows.Where(row => row.IsSystemNoise).Sum(row => row.ForegroundSeconds);
        var qualityFlags = qualityRows
            .SelectMany(row => row.QualityFlags)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!context.IncludeSystemNoise && systemNoiseSeconds > 0)
            qualityFlags.Add("hidden-system-noise");

        return new MobileAnalyticsOverviewResponse(
            context.Range,
            _timeProvider.GetUtcNow(),
            rows.Any(row => row.IsStale),
            totalSeconds,
            totalSeconds / localDayCount,
            0,
            byLocalDay.FirstOrDefault()?.Date,
            byHour.FirstOrDefault()?.Hour,
            rows.Select(row => row.PackageName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            rows.Count,
            totalSeconds <= 0 ? 0 : Math.Round(1 - fallbackSeconds / (double)totalSeconds, 2),
            new MobileAnalyticsQualitySummaryDto(
                qualityTotalSeconds <= 0 ? 0 : Math.Round(1 - qualityFallbackSeconds / (double)qualityTotalSeconds, 2),
                qualityTotalSeconds <= 0 ? 0 : Math.Round(qualityFallbackSeconds / (double)qualityTotalSeconds, 2),
                qualityRows.Where(row => row.QualityFlags.Contains("missing-metadata"))
                    .Select(row => row.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                qualityTotalSeconds <= 0 ? 0 : Math.Round(systemNoiseSeconds / (double)qualityTotalSeconds, 2),
                qualityTotalSeconds <= 0 ? 0 : Math.Round(qualityRows.Where(row => row.QualityFlags.Contains("short-event-noise")).Sum(row => row.ForegroundSeconds) / (double)qualityTotalSeconds, 2),
                await FailedBatchCountAsync(context, ct),
                await LastSyncAtAsync(context, ct),
                qualityFlags),
            goal,
            anomalies,
            suggestions);
    }

    public async Task<IReadOnlyList<MobileHeatmapBucketDto>> GetHeatmapAsync(
        MobileAnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var timeZoneInfo = _queryService.ResolveTimezone(context.Range.Timezone);
        var bucketSize = context.Granularity switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "day" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

        return SplitRowsIntoBuckets(await LoadRowsAsync(context, ct), timeZoneInfo, bucketSize, context.Granularity)
            .GroupBy(row => new
            {
                row.BucketStartUtc,
                row.BucketEndUtc,
                row.LocalDate,
                row.LocalHour,
                row.LifeCategory
            })
            .Select(group => new MobileHeatmapBucketDto(
                group.Key.BucketStartUtc,
                group.Key.BucketEndUtc,
                group.Key.LocalDate,
                group.Key.LocalHour,
                group.Key.LifeCategory,
                group.Sum(row => row.BucketSeconds),
                group.SelectMany(row => row.QualityFlags).Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(bucket => bucket.BucketStartUtc)
            .ThenBy(bucket => bucket.LifeCategory, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<MobileAnalyticsChartDto>> GetChartsAsync(
        MobileAnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var timeZoneInfo = _queryService.ResolveTimezone(context.Range.Timezone);
        var rows = await LoadRowsAsync(context, ct);
        var totalSeconds = rows.Sum(row => row.ForegroundSeconds);
        var dayBuckets = SplitRowsIntoBuckets(rows, timeZoneInfo, TimeSpan.FromDays(1), "day");
        var hourBuckets = SplitRowsIntoBuckets(rows, timeZoneInfo, TimeSpan.FromHours(1), "hour");

        var categoryShare = rows
            .GroupBy(row => row.LifeCategory)
            .Select(group => ChartPoint(
                group.Key,
                group.Key,
                group.Sum(row => row.ForegroundSeconds),
                totalSeconds,
                group.Key,
                null,
                null,
                null))
            .OrderByDescending(point => point.Value)
            .ThenBy(point => point.Label, StringComparer.Ordinal)
            .ToList();
        var topApps = rows
            .GroupBy(row => new { row.PackageName, row.DisplayName, row.LifeCategory })
            .Select(group => ChartPoint(
                group.Key.PackageName,
                group.Key.DisplayName,
                group.Sum(row => row.ForegroundSeconds),
                totalSeconds,
                group.Key.LifeCategory,
                group.Key.PackageName,
                null,
                null))
            .OrderByDescending(point => point.Value)
            .ThenBy(point => point.Label, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var dailyTrend = dayBuckets
            .GroupBy(row => row.LocalDate)
            .Select(group => ChartPoint(group.Key, group.Key, group.Sum(row => row.BucketSeconds), totalSeconds, null, null, group.Key, null))
            .OrderBy(point => point.Key, StringComparer.Ordinal)
            .ToList();
        var hourDistribution = hourBuckets
            .GroupBy(row => row.LocalHour)
            .Select(group => ChartPoint(group.Key.ToString("00", CultureInfo.InvariantCulture), $"{group.Key:00}:00", group.Sum(row => row.BucketSeconds), totalSeconds, null, null, null, group.Key))
            .OrderBy(point => point.LocalHour)
            .ToList();
        var categoryTrend = dayBuckets
            .GroupBy(row => new { Date = row.LocalDate, row.LifeCategory })
            .Select(group => ChartPoint($"{group.Key.Date}:{group.Key.LifeCategory}", group.Key.LifeCategory, group.Sum(row => row.BucketSeconds), totalSeconds, group.Key.LifeCategory, null, group.Key.Date, null))
            .OrderBy(point => point.LocalDate, StringComparer.Ordinal)
            .ThenBy(point => point.Label, StringComparer.Ordinal)
            .ToList();
        var switchTrend = rows
            .GroupBy(row => LocalDate(row.StartUtc, timeZoneInfo))
            .Select(group => new MobileAnalyticsChartPointDto(group.Key, group.Key, group.Count(), null, null, null, group.Key, null))
            .OrderBy(point => point.Key, StringComparer.Ordinal)
            .ToList();

        return
        [
            new MobileAnalyticsChartDto("category-share", "分类占比", "category-share", "seconds", categoryShare),
            new MobileAnalyticsChartDto("top-apps", "Top App", "top-apps", "seconds", topApps),
            new MobileAnalyticsChartDto("daily-total", "每日趋势", "daily-total", "seconds", dailyTrend),
            new MobileAnalyticsChartDto("hour-distribution", "小时分布", "hour-distribution", "seconds", hourDistribution),
            new MobileAnalyticsChartDto("category-trend", "分类趋势", "category-trend", "seconds", categoryTrend),
            new MobileAnalyticsChartDto("switch-trend", "切换趋势", "switch-trend", "count", switchTrend),
            new MobileAnalyticsChartDto("comparison", "周期对比", "comparison", "ratio", []),
            new MobileAnalyticsChartDto("goal-marker", "目标进度", "goal-marker", "seconds", [])
        ];
    }

    private async Task<IReadOnlyList<UsageRow>> LoadRowsAsync(
        MobileAnalyticsQueryContext context,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var sessionsQuery = _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(session => session.UserId == userId
                && session.StartUtc < context.Range.RangeEndUtc
                && (session.EndUtc == null || session.EndUtc > context.Range.RangeStartUtc));
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            sessionsQuery = sessionsQuery.Where(session => session.DeviceId == context.DeviceId);
        if (!string.IsNullOrWhiteSpace(context.PackageName))
            sessionsQuery = sessionsQuery.Where(session => session.PackageName == context.PackageName);

        var sessions = await sessionsQuery.ToListAsync(ct);
        var summariesQuery = MobileUsageQueryService
            .WhereFallbackSummaries(_db.Set<MobileUsageSummaryEntity>().AsNoTracking())
            .Where(summary => summary.UserId == userId
                && summary.WindowStartUtc < context.Range.RangeEndUtc
                && summary.WindowEndUtc > context.Range.RangeStartUtc);
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            summariesQuery = summariesQuery.Where(summary => summary.DeviceId == context.DeviceId);
        if (!string.IsNullOrWhiteSpace(context.PackageName))
            summariesQuery = summariesQuery.Where(summary => summary.PackageName == context.PackageName);
        var summaries = await DeduplicateFallbackSummariesAsync(summariesQuery, ct);

        var packages = sessions.Select(session => session.PackageName)
            .Concat(summaries.Select(summary => summary.PackageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var classifications = await LoadClassificationsAsync(userId, packages, ct);
        var rows = new List<UsageRow>();

        foreach (var session in sessions)
        {
            // dirty data: skip anomalous sessions
            if (session.QualityFlagsJson.Contains("anomalous_duration", StringComparison.OrdinalIgnoreCase)
                || session.QualityFlagsJson.Contains("day_overflow", StringComparison.OrdinalIgnoreCase)
                || (session.DurationMs ?? 0) > 8L * 60 * 60 * 1000)
                continue;
            var classification = classifications.GetValueOrDefault(session.PackageName, Classification.Default(session.PackageName));
            if (!MatchesClassification(context, classification))
                continue;
            var start = Max(session.StartUtc, context.Range.RangeStartUtc);
            var end = Min(session.EndUtc ?? start.AddMilliseconds(session.DurationMs.GetValueOrDefault()), context.Range.RangeEndUtc);
            var seconds = Math.Max(0, (long)(end - start).TotalSeconds);
            if (seconds <= context.MinDurationSeconds)
                continue;
            rows.Add(new UsageRow(
                session.DeviceId,
                session.PackageName,
                classification.DisplayName,
                classification.LifeCategory ?? MobileLifeCategories.Uncategorized,
                start,
                end,
                seconds,
                "events",
                classification.IsSystemNoise,
                session.QualityFlagsJson.Contains("stale", StringComparison.OrdinalIgnoreCase),
                QualityFlags(session.QualityFlagsJson, classification.HasMetadata)));
        }

        foreach (var summary in summaries)
        {
            if (summary.QualityFlagsJson.Contains("duplicate_summary", StringComparison.OrdinalIgnoreCase))
                continue;
            var classification = classifications.GetValueOrDefault(summary.PackageName, Classification.Default(summary.PackageName));
            if (!MatchesClassification(context, classification))
                continue;
            var start = Max(summary.WindowStartUtc, context.Range.RangeStartUtc);
            var end = Min(summary.WindowEndUtc, context.Range.RangeEndUtc);
            var seconds = ProratedSeconds(
                summary.WindowStartUtc,
                summary.WindowEndUtc,
                start,
                end,
                summary.TotalTimeVisibleMs);
            if (seconds <= context.MinDurationSeconds)
                continue;
            rows.Add(new UsageRow(
                summary.DeviceId,
                summary.PackageName,
                classification.DisplayName,
                classification.LifeCategory ?? MobileLifeCategories.Uncategorized,
                start,
                end,
                seconds,
                "fallback",
                classification.IsSystemNoise,
                summary.QualityFlagsJson.Contains("stale", StringComparison.OrdinalIgnoreCase),
                QualityFlags(summary.QualityFlagsJson, classification.HasMetadata, "fallback-only")));
        }

        return rows;
    }

    private static IReadOnlyList<UsageBucketRow> SplitRowsIntoBuckets(
        IReadOnlyList<UsageRow> rows,
        TimeZoneInfo timeZoneInfo,
        TimeSpan bucketSize,
        string granularity)
        => rows.SelectMany(row => SplitRowIntoBuckets(row, timeZoneInfo, bucketSize, granularity)).ToList();

    private static IEnumerable<UsageBucketRow> SplitRowIntoBuckets(
        UsageRow row,
        TimeZoneInfo timeZoneInfo,
        TimeSpan bucketSize,
        string granularity)
    {
        if (row.ForegroundSeconds <= 0 || row.EndUtc <= row.StartUtc)
            yield break;

        var localStart = TimeZoneInfo.ConvertTime(row.StartUtc, timeZoneInfo);
        var localBucket = FloorLocalBucket(localStart, bucketSize, granularity);
        var segments = new List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string LocalDate, int LocalHour, double OverlapMs)>();
        var seenBuckets = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var bucketStartUtc = LocalBucketUtc(localBucket, timeZoneInfo);
            if (bucketStartUtc >= row.EndUtc)
                break;

            var nextLocalBucket = NextLocalBucket(localBucket, bucketSize, granularity);
            var bucketEndUtc = LocalBucketUtc(nextLocalBucket, timeZoneInfo);
            // DST safety: ensure bucket maps to a unique local date via DateOnly.FromDateTime
            var localBucketDate = DateOnly.FromDateTime(localBucket).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var bucketKey = $"{localBucketDate}:{localBucket:HH:mm}";
            if (!seenBuckets.Add(bucketKey) && granularity == "day")
            {
                localBucket = nextLocalBucket;
                continue;
            }
            if (bucketEndUtc > row.StartUtc)
            {
                var overlapStart = Max(row.StartUtc, bucketStartUtc);
                var overlapEnd = Min(row.EndUtc, bucketEndUtc);
                if (overlapEnd > overlapStart)
                {
                    segments.Add((
                        bucketStartUtc,
                        bucketEndUtc,
                        localBucketDate,
                        granularity == "day" ? 0 : localBucket.Hour,
                        (overlapEnd - overlapStart).TotalMilliseconds));
                }
            }

            localBucket = nextLocalBucket;
        }

        if (segments.Count == 0)
            yield break;

        var totalOverlapMs = segments.Sum(segment => segment.OverlapMs);
        if (totalOverlapMs <= 0)
            yield break;

        var exact = segments.Select(s => row.ForegroundSeconds * (s.OverlapMs / totalOverlapMs)).ToArray();
        var floors = exact.Select(v => (long)Math.Floor(v)).ToArray();
        var allocatedFloors = floors.Sum();
        var remainder = row.ForegroundSeconds - allocatedFloors;
        var fractions = exact.Select((v, idx) => new { idx, frac = v - Math.Floor(v) })
            .OrderByDescending(x => x.frac)
            .ThenBy(x => x.idx)
            .ToList();
        for (var k = 0; k < remainder && k < fractions.Count; k++)
            floors[fractions[k].idx] += 1;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var seconds = floors[i];
            if (seconds <= 0)
                continue;

            yield return new UsageBucketRow(
                row.DeviceId,
                row.PackageName,
                row.DisplayName,
                row.LifeCategory,
                segment.StartUtc,
                segment.EndUtc,
                segment.LocalDate,
                segment.LocalHour,
                seconds,
                row.Source,
                row.IsSystemNoise,
                row.IsStale,
                row.QualityFlags);
        }
    }

    private static DateTime FloorLocalBucket(DateTimeOffset local, TimeSpan bucketSize, string granularity)
    {
        if (granularity == "day")
            return new DateTime(local.Year, local.Month, local.Day, 0, 0, 0);

        var bucketMinutes = bucketSize.TotalMinutes >= 60 ? 60 : (int)bucketSize.TotalMinutes;
        var minute = bucketMinutes >= 60 ? 0 : (local.Minute / bucketMinutes) * bucketMinutes;
        return new DateTime(local.Year, local.Month, local.Day, local.Hour, minute, 0);
    }

    private static DateTime NextLocalBucket(DateTime localBucket, TimeSpan bucketSize, string granularity)
        => granularity == "day" ? localBucket.AddDays(1) : localBucket.Add(bucketSize);

    private static DateTimeOffset LocalBucketUtc(DateTime localBucket, TimeZoneInfo timeZoneInfo)
    {
        var unspecified = DateTime.SpecifyKind(localBucket, DateTimeKind.Unspecified);
        // Spring-forward gap may be 30min in some zones, loop until valid
        while (timeZoneInfo.IsInvalidTime(unspecified))
            unspecified = unspecified.AddMinutes(30);
        // Fall-back ambiguous hour
        if (timeZoneInfo.IsAmbiguousTime(unspecified))
        {
            var offsets = timeZoneInfo.GetAmbiguousTimeOffsets(unspecified);
            var chosenOffset = offsets.Max();
            var dto = new DateTimeOffset(unspecified, chosenOffset);
            return dto.ToUniversalTime();
        }
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZoneInfo);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private async Task<IReadOnlyDictionary<string, Classification>> LoadClassificationsAsync(
        Guid userId,
        IReadOnlyCollection<string> packageNames,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Classification>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageName in packageNames)
        {
            if (_classificationService is not null)
            {
                var classified = await _classificationService.ClassifyAsync(packageName, ct);
                result[packageName] = new Classification(
                    classified.DisplayName,
                    classified.LifeCategory,
                    classified.IsSystemNoise,
                    classified.HasMetadata);
                continue;
            }

            var appOverride = await _db.Set<MobileAppCatalogOverrideEntity>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId && item.PackageName == packageName, ct);
            var app = await _db.Set<MobileAppCatalogEntity>()
                .AsNoTracking()
                .Where(item => item.UserId == userId && item.PackageName == packageName)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            var builtIn = BuiltIn(packageName);
            var displayName = FirstNonBlank(appOverride?.DisplayNameOverride, app?.DisplayName, builtIn.DisplayName, packageName);
            var lifeCategory = FirstNonBlank(appOverride?.LifeCategory, builtIn.LifeCategory, MapAndroidCategory(app?.Category), MobileLifeCategories.Uncategorized);
            var isSystemNoise = appOverride?.IsSystemNoise ?? builtIn.IsSystemNoise || app?.IsSystemApp == true;
            result[packageName] = new Classification(displayName, lifeCategory, isSystemNoise, app is not null);
        }

        return result;
    }

    private async Task<MobileGoalProgressDto?> FirstGoalProgressAsync(long usedSeconds, CancellationToken ct)
    {
        var goal = (await _goalService.ListAsync(ct))
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.Scope == "total-daily" ? 0 : 1)
            .FirstOrDefault();
        if (goal is null)
            return null;

        return new MobileGoalProgressDto(
            goal.Scope,
            goal.Label,
            goal.LimitSeconds,
            usedSeconds,
            usedSeconds > goal.LimitSeconds,
            Math.Max(0, goal.LimitSeconds - usedSeconds));
    }

    private async Task<int> FailedBatchCountAsync(MobileAnalyticsQueryContext context, CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .CountAsync(batch => batch.UserId == userId
                && (context.DeviceId == null || batch.DeviceId == context.DeviceId)
                && batch.WindowStartUtc < context.Range.RangeEndUtc
                && batch.WindowEndUtc > context.Range.RangeStartUtc
                && (batch.FailedCount > 0 || batch.Status != "completed"), ct);
    }

    private async Task<DateTimeOffset?> LastSyncAtAsync(MobileAnalyticsQueryContext context, CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(batch => batch.UserId == userId
                && (context.DeviceId == null || batch.DeviceId == context.DeviceId)
                && batch.WindowStartUtc < context.Range.RangeEndUtc
                && batch.WindowEndUtc > context.Range.RangeStartUtc)
            .Select(batch => batch.CompletedAtUtc ?? batch.CreatedAt)
            .OrderByDescending(value => value)
            .FirstOrDefaultAsync(ct);
    }

    private static bool MatchesClassification(MobileAnalyticsQueryContext context, Classification classification)
        => (context.IncludeSystemNoise || !classification.IsSystemNoise)
            && (string.IsNullOrWhiteSpace(context.LifeCategory)
                || string.Equals(context.LifeCategory, classification.LifeCategory, StringComparison.Ordinal));

    private static IReadOnlyList<MobileAnomalyDto> BuildAnomalies(IReadOnlyList<UsageRow> rows, long totalSeconds)
    {
        var anomalies = new List<MobileAnomalyDto>();
        if (rows.Any(row => TimeZoneInfo.ConvertTime(row.StartUtc, ChinaTimeZone()).Hour >= 22))
        {
            anomalies.Add(new MobileAnomalyDto(
                "night-use",
                "Warning",
                "夜间使用偏高",
                "22:00 后仍有明显使用记录",
                "heatmap:night"));
        }
        if (totalSeconds > 6 * 60 * 60)
        {
            anomalies.Add(new MobileAnomalyDto(
                "long-total",
                "Warning",
                "总使用时长偏高",
                "所选时间段总使用时长超过 6 小时",
                "overview:total"));
        }

        return anomalies;
    }

    private static IReadOnlyList<MobileSuggestionDto> BuildSuggestions(IReadOnlyList<UsageRow> rows)
    {
        var topCategory = rows
            .GroupBy(row => row.LifeCategory)
            .Select(group => new { LifeCategory = group.Key, Seconds = group.Sum(row => row.ForegroundSeconds) })
            .OrderByDescending(item => item.Seconds)
            .FirstOrDefault();

        if (topCategory is null)
            return [];

        return
        [
            new MobileSuggestionDto(
                "top-category-review",
                $"{topCategory.LifeCategory} 是当前主要使用分类，可点击分类图表继续查看",
                $"category:{topCategory.LifeCategory}")
        ];
    }

    private static MobileAnalyticsChartPointDto ChartPoint(
        string key,
        string label,
        long seconds,
        long totalSeconds,
        string? lifeCategory,
        string? packageName,
        string? localDate,
        int? localHour)
        => new(key, label, seconds, seconds, lifeCategory, packageName, localDate, localHour);

    private static IReadOnlyList<string> QualityFlags(string json, bool hasMetadata, params string[] extra)
    {
        var flags = extra.Where(flag => !string.IsNullOrWhiteSpace(flag)).ToList();
        if (!hasMetadata)
            flags.Add("missing-metadata");
        if (json.Contains("partial", StringComparison.OrdinalIgnoreCase))
            flags.Add("partial-sync");
        if (json.Contains("stale", StringComparison.OrdinalIgnoreCase))
            flags.Add("stale-aggregate");
        return flags.Distinct(StringComparer.Ordinal).ToList();
    }

    private static int CountLocalDays(MobileAnalyticsRangeDto range)
    {
        var start = DateOnly.Parse(range.LocalStartDate, CultureInfo.InvariantCulture);
        var end = DateOnly.Parse(range.LocalEndDate, CultureInfo.InvariantCulture);
        return end.DayNumber - start.DayNumber + 1;
    }

    private static string LocalDate(DateTimeOffset value, TimeZoneInfo timeZoneInfo)
        => TimeZoneInfo.ConvertTime(value, timeZoneInfo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ChinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(MobileAnalyticsDefaults.DefaultTimezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }

    private static Classification BuiltIn(string packageName)
        => packageName switch
        {
            "com.tencent.mobileqq" => new("QQ", MobileLifeCategories.Chat, false, true),
            "com.tencent.mm" => new("微信", MobileLifeCategories.Chat, false, true),
            "com.ss.android.ugc.aweme" => new("抖音", MobileLifeCategories.Video, false, true),
            "com.smile.gifmaker" => new("快手", MobileLifeCategories.Video, false, true),
            "com.netease.cloudmusic" => new("网易云音乐", MobileLifeCategories.Other, false, true),
            "com.android.systemui" => new("系统界面", MobileLifeCategories.ToolsSystem, true, true),
            _ when packageName.Contains("launcher", StringComparison.OrdinalIgnoreCase) => new(packageName, MobileLifeCategories.ToolsSystem, true, true),
            _ => new(packageName, null, false, false)
        };

    private static string? MapAndroidCategory(string? category)
        => category?.ToLowerInvariant() switch
        {
            "0" => MobileLifeCategories.Game,
            "1" => MobileLifeCategories.Other,
            "2" => MobileLifeCategories.Video,
            "3" => MobileLifeCategories.Other,
            "4" => MobileLifeCategories.Chat,
            "5" => MobileLifeCategories.Learning,
            "6" => MobileLifeCategories.Other,
            "7" => MobileLifeCategories.Documents,
            "communication" => MobileLifeCategories.Chat,
            "social" => MobileLifeCategories.Chat,
            "video" => MobileLifeCategories.Video,
            "entertainment" => MobileLifeCategories.Video,
            "game" => MobileLifeCategories.Game,
            "games" => MobileLifeCategories.Game,
            "audio" => MobileLifeCategories.Other,
            "music" => MobileLifeCategories.Other,
            "news" => MobileLifeCategories.Learning,
            "education" => MobileLifeCategories.Learning,
            "learning" => MobileLifeCategories.Learning,
            "productivity" => MobileLifeCategories.Documents,
            "business" => MobileLifeCategories.Documents,
            "tools" => MobileLifeCategories.ToolsSystem,
            "system" => MobileLifeCategories.ToolsSystem,
            "browser" => MobileLifeCategories.Other,
            "maps" => MobileLifeCategories.Other,
            "navigation" => MobileLifeCategories.Other,
            "shopping" => MobileLifeCategories.Other,
            "finance" => MobileLifeCategories.Other,
            "health" => MobileLifeCategories.Other,
            "fitness" => MobileLifeCategories.Other,
            "camera" => MobileLifeCategories.Other,
            "image" => MobileLifeCategories.Other,
            _ => null
        };

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static long ProratedSeconds(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc,
        DateTimeOffset overlapStartUtc,
        DateTimeOffset overlapEndUtc,
        long totalVisibleMs)
    {
        if (totalVisibleMs <= 0 || overlapEndUtc <= overlapStartUtc)
            return 0;

        var sourceMs = (sourceEndUtc - sourceStartUtc).TotalMilliseconds;
        var overlapMs = (overlapEndUtc - overlapStartUtc).TotalMilliseconds;
        if (sourceMs <= 0)
            return Math.Max(0, totalVisibleMs / 1000);

        var ratio = Math.Clamp(overlapMs / sourceMs, 0, 1);
        return Math.Max(0, Convert.ToInt64(Math.Floor(totalVisibleMs * ratio / 1000)));
    }

    private static async Task<List<MobileUsageSummaryEntity>> DeduplicateFallbackSummariesAsync(
        IQueryable<MobileUsageSummaryEntity> query,
        CancellationToken ct)
    {
        var summaries = await query.ToListAsync(ct);
        return summaries
            .GroupBy(s => (
                Package: s.PackageName.ToLowerInvariant(),
                HourStart: new DateTimeOffset(s.WindowStartUtc.Year, s.WindowStartUtc.Month, s.WindowStartUtc.Day, s.WindowStartUtc.Hour, 0, 0, TimeSpan.Zero)))
            .Select(group => group.OrderByDescending(s => s.TotalTimeVisibleMs).First())
            .ToList();
    }

    private static string FirstNonBlank(params string?[] values)
        => values.Select(value => value?.Trim()).First(value => !string.IsNullOrWhiteSpace(value))!;

    private sealed record UsageRow(
        string DeviceId,
        string PackageName,
        string DisplayName,
        string LifeCategory,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        long ForegroundSeconds,
        string Source,
        bool IsSystemNoise,
        bool IsStale,
        IReadOnlyList<string> QualityFlags);

    private sealed record UsageBucketRow(
        string DeviceId,
        string PackageName,
        string DisplayName,
        string LifeCategory,
        DateTimeOffset BucketStartUtc,
        DateTimeOffset BucketEndUtc,
        string LocalDate,
        int LocalHour,
        long BucketSeconds,
        string Source,
        bool IsSystemNoise,
        bool IsStale,
        IReadOnlyList<string> QualityFlags);

    private sealed record Classification(
        string DisplayName,
        string? LifeCategory,
        bool IsSystemNoise,
        bool HasMetadata)
    {
        public static Classification Default(string packageName)
            => new(packageName, MobileLifeCategories.Uncategorized, false, false);
    }
}
