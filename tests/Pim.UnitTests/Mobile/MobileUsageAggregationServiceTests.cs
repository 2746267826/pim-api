using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileUsageAggregationServiceTests
{
    [Fact]
    public async Task GetOverviewAsync_UsesOverridesGoalsAndBeijingHour()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-06T13:00:00Z"), DateTimeOffset.Parse("2026-07-06T13:30:00Z"));
        db.Set<MobileAppCatalogOverrideEntity>().Add(new MobileAppCatalogOverrideEntity
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = "com.tencent.mobileqq",
            DisplayNameOverride = "QQ",
            LifeCategory = MobileLifeCategories.Social
        });
        db.Set<MobileUsageGoalEntity>().Add(new MobileUsageGoalEntity
        {
            UserId = MobileTestHelpers.UserId,
            Scope = "total-daily",
            Label = "每日手机总时长",
            LimitSeconds = 3600,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, now);

        var overview = await service.GetOverviewAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z")), CancellationToken.None);

        Assert.Equal(1800, overview.TotalForegroundSeconds);
        Assert.Equal(21, overview.PeakLocalHour);
        Assert.Equal(1, overview.AppCount);
        Assert.Equal("每日手机总时长", overview.GoalProgress?.Label);
        Assert.Equal(1800, overview.GoalProgress?.RemainingSeconds);
        Assert.Contains(overview.Suggestions, item => item.Text.Contains("社交沟通", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetHeatmapAndChartsAsync_ReturnReadableChineseBuckets()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-06T13:00:00Z"), DateTimeOffset.Parse("2026-07-06T13:30:00Z"));
        SeedSession(db, "com.ss.android.ugc.aweme", DateTimeOffset.Parse("2026-07-06T14:00:00Z"), DateTimeOffset.Parse("2026-07-06T14:20:00Z"));
        await db.SaveChangesAsync();

        var service = CreateService(db, now);

        var heatmap = await service.GetHeatmapAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z")), CancellationToken.None);
        var charts = await service.GetChartsAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z")), CancellationToken.None);

        Assert.Contains(heatmap, bucket => bucket.LocalHour == 21 && bucket.LifeCategory == "社交沟通");
        Assert.Contains(heatmap, bucket => bucket.LocalHour == 22 && bucket.LifeCategory == "短视频/娱乐");
        Assert.Contains(charts, chart => chart.Key == "category-share" && chart.Points.Any(point => point.Label == "社交沟通"));
        Assert.Contains(charts, chart => chart.Key == "top-apps" && chart.Points.Any(point => point.PackageName == "com.tencent.mobileqq"));
        Assert.Contains(charts, chart => chart.Key == "hour-distribution");
    }

    [Fact]
    public async Task GetOverviewAndHeatmapAsync_ProrateFallbackSummariesToOverlappingRange()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageSummaryEntity>().Add(SeedSummary(
            "com.tencent.mobileqq",
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T02:00:00Z"),
            7200));
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var query = new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T01:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T02:00:00Z"));

        var overview = await service.GetOverviewAsync(query, CancellationToken.None);
        var heatmap = await service.GetHeatmapAsync(query, CancellationToken.None);

        Assert.Equal(3600, overview.TotalForegroundSeconds);
        var bucket = Assert.Single(heatmap);
        Assert.Equal(3600, bucket.ForegroundSeconds);
        Assert.Equal(9, bucket.LocalHour);
    }

    [Fact]
    public async Task GetOverviewAsync_ReportsHiddenNoiseAndMissingMetadataQualityFlags()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.android.systemui", DateTimeOffset.Parse("2026-07-06T12:00:00Z"), DateTimeOffset.Parse("2026-07-06T12:02:00Z"));
        SeedSession(db, "com.example.unknown", DateTimeOffset.Parse("2026-07-06T13:00:00Z"), DateTimeOffset.Parse("2026-07-06T13:05:00Z"));
        await db.SaveChangesAsync();

        var service = CreateService(db, now);

        var overview = await service.GetOverviewAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z")), CancellationToken.None);

        Assert.Equal(300, overview.TotalForegroundSeconds);
        Assert.True(overview.Quality.SystemNoiseShare > 0);
        Assert.Equal(2, overview.Quality.MissingMetadataAppCount);
        Assert.Contains("hidden-system-noise", overview.Quality.QualityFlags);
        Assert.Contains("missing-metadata", overview.Quality.QualityFlags);
    }

    private static MobileUsageAggregationService CreateService(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var timeProvider = MobileTestHelpers.Time(now);
        return new MobileUsageAggregationService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileAnalyticsQueryService(timeProvider),
            new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), timeProvider),
            timeProvider);
    }

    private static void SeedSession(
        Pim.Infrastructure.Data.PimDbContext db,
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = packageName,
            StartUtc = start,
            EndUtc = end,
            DurationMs = (long)(end - start).TotalMilliseconds,
            QualityFlagsJson = "[]",
            CreatedAt = start
        });
    }

    private static MobileUsageSummaryEntity SeedSummary(
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end,
        int seconds)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = packageName,
            WindowStartUtc = start,
            WindowEndUtc = end,
            TotalTimeVisibleMs = seconds * 1000L,
            LastTimeUsedUtc = end,
            SourceKind = "fallback",
            QualityFlagsJson = "[]",
            CreatedAt = start,
            UpdatedAt = end
        };
}
