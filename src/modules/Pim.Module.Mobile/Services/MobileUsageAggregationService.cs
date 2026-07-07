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
        var byLocalDay = rows
            .GroupBy(row => LocalDate(row.StartUtc, timeZoneInfo))
            .Select(group => new { Date = group.Key, Seconds = group.Sum(row => row.ForegroundSeconds) })
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.Date, StringComparer.Ordinal)
            .ToList();
        var byHour = rows
            .GroupBy(row => TimeZoneInfo.ConvertTime(row.StartUtc, timeZoneInfo).Hour)
            .Select(group => new { Hour = group.Key, Seconds = group.Sum(row => row.ForegroundSeconds) })
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

        return (await LoadRowsAsync(context, ct))
            .GroupBy(row =>
            {
                var local = TimeZoneInfo.ConvertTime(row.StartUtc, timeZoneInfo);
                var minute = bucketSize.TotalMinutes >= 60
                    ? 0
                    : (local.Minute / (int)bucketSize.TotalMinutes) * (int)bucketSize.TotalMinutes;
                var localBucket = new DateTime(local.Year, local.Month, local.Day, context.Granularity == "day" ? 0 : local.Hour, minute, 0);
                var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localBucket, DateTimeKind.Unspecified), timeZoneInfo);
                var endUtc = bucketSize == TimeSpan.FromDays(1)
                    ? startUtc.AddDays(1)
                    : startUtc.Add(bucketSize);
                return new
                {
                    StartUtc = new DateTimeOffset(startUtc, TimeSpan.Zero),
                    EndUtc = new DateTimeOffset(endUtc, TimeSpan.Zero),
                    LocalDate = localBucket.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    LocalHour = localBucket.Hour,
                    row.LifeCategory
                };
            })
            .Select(group => new MobileHeatmapBucketDto(
                group.Key.StartUtc,
                group.Key.EndUtc,
                group.Key.LocalDate,
                group.Key.LocalHour,
                group.Key.LifeCategory,
                group.Sum(row => row.ForegroundSeconds),
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
        var dailyTrend = rows
            .GroupBy(row => LocalDate(row.StartUtc, timeZoneInfo))
            .Select(group => ChartPoint(group.Key, group.Key, group.Sum(row => row.ForegroundSeconds), totalSeconds, null, null, group.Key, null))
            .OrderBy(point => point.Key, StringComparer.Ordinal)
            .ToList();
        var hourDistribution = rows
            .GroupBy(row => TimeZoneInfo.ConvertTime(row.StartUtc, timeZoneInfo).Hour)
            .Select(group => ChartPoint(group.Key.ToString("00", CultureInfo.InvariantCulture), $"{group.Key:00}:00", group.Sum(row => row.ForegroundSeconds), totalSeconds, null, null, null, group.Key))
            .OrderBy(point => point.LocalHour)
            .ToList();
        var categoryTrend = rows
            .GroupBy(row => new { Date = LocalDate(row.StartUtc, timeZoneInfo), row.LifeCategory })
            .Select(group => ChartPoint($"{group.Key.Date}:{group.Key.LifeCategory}", group.Key.LifeCategory, group.Sum(row => row.ForegroundSeconds), totalSeconds, group.Key.LifeCategory, null, group.Key.Date, null))
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
        var summaries = await summariesQuery.ToListAsync(ct);

        var packages = sessions.Select(session => session.PackageName)
            .Concat(summaries.Select(summary => summary.PackageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var classifications = await LoadClassificationsAsync(userId, packages, ct);
        var rows = new List<UsageRow>();

        foreach (var session in sessions)
        {
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
            "com.tencent.mobileqq" => new("QQ", MobileLifeCategories.Social, false, true),
            "com.tencent.mm" => new("微信", MobileLifeCategories.Social, false, true),
            "com.ss.android.ugc.aweme" => new("抖音", MobileLifeCategories.ShortVideoEntertainment, false, true),
            "com.smile.gifmaker" => new("快手", MobileLifeCategories.ShortVideoEntertainment, false, true),
            "com.netease.cloudmusic" => new("网易云音乐", MobileLifeCategories.MusicAudio, false, true),
            "com.android.systemui" => new("系统界面", MobileLifeCategories.ToolsSystem, true, true),
            _ when packageName.Contains("launcher", StringComparison.OrdinalIgnoreCase) => new(packageName, MobileLifeCategories.ToolsSystem, true, true),
            _ => new(packageName, null, false, false)
        };

    private static string? MapAndroidCategory(string? category)
        => category?.ToLowerInvariant() switch
        {
            "0" => MobileLifeCategories.Game,
            "1" => MobileLifeCategories.MusicAudio,
            "2" => MobileLifeCategories.ShortVideoEntertainment,
            "3" => MobileLifeCategories.CameraCreation,
            "4" => MobileLifeCategories.Social,
            "5" => MobileLifeCategories.ReadingNews,
            "6" => MobileLifeCategories.TravelMaps,
            "7" => MobileLifeCategories.WorkProductivity,
            "communication" => MobileLifeCategories.Social,
            "social" => MobileLifeCategories.Social,
            "video" => MobileLifeCategories.ShortVideoEntertainment,
            "entertainment" => MobileLifeCategories.ShortVideoEntertainment,
            "game" => MobileLifeCategories.Game,
            "games" => MobileLifeCategories.Game,
            "audio" => MobileLifeCategories.MusicAudio,
            "music" => MobileLifeCategories.MusicAudio,
            "news" => MobileLifeCategories.ReadingNews,
            "education" => MobileLifeCategories.Learning,
            "learning" => MobileLifeCategories.Learning,
            "productivity" => MobileLifeCategories.WorkProductivity,
            "business" => MobileLifeCategories.WorkProductivity,
            "tools" => MobileLifeCategories.ToolsSystem,
            "system" => MobileLifeCategories.ToolsSystem,
            "browser" => MobileLifeCategories.BrowserSearch,
            "maps" => MobileLifeCategories.TravelMaps,
            "navigation" => MobileLifeCategories.TravelMaps,
            "shopping" => MobileLifeCategories.ShoppingFood,
            "finance" => MobileLifeCategories.FinancePayment,
            "health" => MobileLifeCategories.HealthFitness,
            "fitness" => MobileLifeCategories.HealthFitness,
            "camera" => MobileLifeCategories.CameraCreation,
            "image" => MobileLifeCategories.CameraCreation,
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
