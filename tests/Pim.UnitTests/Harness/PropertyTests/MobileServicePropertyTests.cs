using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Mobile;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class MobileServicePropertyTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
    private static readonly DateTimeOffset RangeStart = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
    private static readonly DateTimeOffset RangeEnd = DateTimeOffset.Parse("2026-07-08T00:00:00Z");

    // ===== MobileUsageAggregationService =====

    [Fact]
    public async Task UsageAggregation_GetOverviewAsync_EmptyDb_ReturnsZero()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = CreateUsageAggregation(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(0, res.TotalForegroundSeconds);
        Assert.True(res.Completeness >= 0);
    }

    [Fact]
    public async Task UsageAggregation_GetOverviewAsync_WithSession_ReturnsPositive()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", RangeStart.AddHours(10), RangeStart.AddHours(10).AddMinutes(30));
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Equal(1800, res.TotalForegroundSeconds);
        Assert.True(res.AppCount >= 1);
    }

    [Fact]
    public async Task UsageAggregation_GetOverviewAsync_WithGoal_ReturnsProgress()
    {
        var now = BaseTime;
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mm", RangeStart.AddHours(13), RangeStart.AddHours(13).AddMinutes(20));
        db.Set<MobileUsageGoalEntity>().Add(new MobileUsageGoalEntity
        {
            UserId = MobileTestHelpers.UserId,
            Scope = "total-daily",
            Label = "每日总时长",
            LimitSeconds = 3600,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, now);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotNull(res.GoalProgress);
        Assert.Equal(3600, res.GoalProgress!.LimitSeconds);
    }

    [Fact]
    public async Task UsageAggregation_GetHeatmapAsync_Empty_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = CreateUsageAggregation(db, BaseTime);
        var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotNull(heat);
        Assert.Empty(heat);
    }

    [Fact]
    public async Task UsageAggregation_GetHeatmapAsync_WithSession_ReturnsBucket()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-06T13:00:00Z"), DateTimeOffset.Parse("2026-07-06T13:30:00Z"));
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, BaseTime);
        var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotEmpty(heat);
        Assert.All(heat, b => Assert.True(b.ForegroundSeconds > 0));
        Assert.All(heat, b => Assert.True(b.ForegroundSeconds <= 3600));
    }

    [Fact]
    public async Task UsageAggregation_GetHeatmapAsync_WithFallback_ProrationWorks()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.ss.android.ugc.aweme",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            WindowEndUtc = DateTimeOffset.Parse("2026-07-06T02:00:00Z"),
            TotalTimeVisibleMs = 7200 * 1000L,
            LastTimeUsedUtc = DateTimeOffset.Parse("2026-07-06T02:00:00Z"),
            SourceKind = "fallback",
            QualityFlagsJson = "[]",
            CreatedAt = RangeStart,
            UpdatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, BaseTime);
        var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeStart.AddHours(2)), CancellationToken.None);
        Assert.Equal(2, heat.Count);
        Assert.All(heat, b => Assert.Equal(3600, b.ForegroundSeconds));
    }

    [Fact]
    public async Task UsageAggregation_GetChartsAsync_Empty_ReturnsSevenCharts()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = CreateUsageAggregation(db, BaseTime);
        var charts = await svc.GetChartsAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Equal(8, charts.Count);
        Assert.Contains(charts, c => c.Key == "category-share");
        Assert.Contains(charts, c => c.Key == "top-apps");
    }

    [Fact]
    public async Task UsageAggregation_GetChartsAsync_WithSession_ContainsData()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", RangeStart.AddHours(10), RangeStart.AddHours(10).AddMinutes(15));
        SeedSession(db, "com.ss.android.ugc.aweme", RangeStart.AddHours(11), RangeStart.AddHours(11).AddMinutes(10));
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, BaseTime);
        var charts = await svc.GetChartsAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        var cat = charts.Single(c => c.Key == "category-share");
        Assert.NotEmpty(cat.Points);
        var top = charts.Single(c => c.Key == "top-apps");
        Assert.NotEmpty(top.Points);
    }

    [Fact]
    public async Task UsageAggregation_GetChartsAsync_CategoryShare_SumsCorrectly()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mobileqq", RangeStart.AddHours(9), RangeStart.AddHours(9).AddMinutes(10));
        await db.SaveChangesAsync();
        var svc = CreateUsageAggregation(db, BaseTime);
        var charts = await svc.GetChartsAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        var cat = charts.Single(c => c.Key == "category-share");
        var sum = cat.Points.Sum(p => p.ForegroundSeconds ?? 0);
        var overview = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Equal(overview.TotalForegroundSeconds, sum);
    }

    // ===== MobileUsageQueryService =====

    [Fact]
    public async Task UsageQuery_GetSummaryAsync_Empty_ReturnsZero()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileUsageQueryService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var res = await svc.GetSummaryAsync(new MobileSummaryQuery("android-main", RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(0, res.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageQuery_GetSummaryAsync_WithFallback_ReturnsRanking()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.app",
            WindowStartUtc = RangeStart.AddHours(1),
            WindowEndUtc = RangeStart.AddHours(2),
            TotalTimeVisibleMs = 1800 * 1000L,
            LastTimeUsedUtc = RangeStart.AddHours(2),
            SourceKind = "fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = RangeStart,
            UpdatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = new MobileUsageQueryService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var res = await svc.GetSummaryAsync(new MobileSummaryQuery("android-main", RangeStart, RangeEnd), CancellationToken.None);
        Assert.Equal(1800, res.TotalForegroundSeconds);
        Assert.Single(res.AppRanking);
    }

    [Fact]
    public async Task UsageQuery_GetTimelineAsync_WithSession_ReturnsItems()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSession(db, "com.tencent.mm", RangeStart.AddHours(10), RangeStart.AddHours(10).AddMinutes(5), deviceId: "android-main");
        await db.SaveChangesAsync();
        var svc = new MobileUsageQueryService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var res = await svc.GetTimelineAsync(new MobileSummaryQuery("android-main", RangeStart, RangeEnd), CancellationToken.None);
        Assert.NotEmpty(res.Items);
        Assert.NotEmpty(res.Sessions);
    }

    // ===== MobileLocationAggregationService =====

    [Fact]
    public async Task LocationAggregation_GetOverviewAsync_Empty_ReturnsZero()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = CreateLocationAggregation(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileLocationQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Equal(0, res.PointCount);
        Assert.Equal(0, res.DistanceMeters);
    }

    [Fact]
    public async Task LocationAggregation_GetOverviewAsync_WithPoints_ReturnsMetrics()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedLocation(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:20:00Z", 31.230416, 121.473701, 12, "usable");
        SeedLocation(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:22:00Z", 31.235000, 121.480000, 18, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocationAggregation(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.Equal(2, res.PointCount);
        Assert.True(res.AverageAccuracyMeters > 0);
    }

    [Fact]
    public async Task LocationAggregation_GetTracksAsync_Empty_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = CreateLocationAggregation(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task LocationAggregation_GetTracksAsync_SinglePoint_IsStay()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedLocation(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocationAggregation(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        var seg = Assert.Single(Assert.Single(tracks).Segments);
        Assert.Equal("stay", seg.Kind);
    }

    [Fact]
    public async Task LocationAggregation_GetTracksAsync_MultiPoint_MoveSegment()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedLocation(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedLocation(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230821, 121.473701, 12, "usable");
        SeedLocation(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.231226, 121.473701, 12, "usable");
        SeedLocation(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.231631, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocationAggregation(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        var seg = Assert.Single(Assert.Single(tracks).Segments);
        Assert.Equal("move", seg.Kind);
        Assert.True(seg.DistanceMeters > 0);
    }

    [Fact]
    public async Task LocationAggregation_GetMovementSegmentsAsync_WithTracks_ReturnsMove()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedLocation(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedLocation(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230821, 121.473701, 12, "usable");
        SeedLocation(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.231226, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocationAggregation(db, BaseTime);
        var segs = await svc.GetMovementSegmentsAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.NotEmpty(segs);
        Assert.All(segs, s => Assert.True(s.DistanceMeters >= 0));
    }

    // ===== MobileFrequentPlaceService (GetFrequentPlacesAsync) =====

    [Fact]
    public async Task FrequentPlaces_GetFrequentPlacesAsync_Empty_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileFrequentPlaceService(db, MobileTestHelpers.CurrentUser(), new MobileLocationQueryService(MobileTestHelpers.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Empty(res.Places);
        Assert.Null(res.Home);
    }

    [Fact]
    public async Task FrequentPlaces_GetFrequentPlacesAsync_InsufficientPoints_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        for (int i = 0; i < 5; i++)
            SeedLocation(db, $"00000000-0000-0000-0000-00000000000{i}", $"2026-07-07T10:0{i}:00Z", 31.230416, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, MobileTestHelpers.CurrentUser(), new MobileLocationQueryService(MobileTestHelpers.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.Empty(res.Places);
    }

    // ===== MobileGapService (DetectGapsAsync ~ GetGapsAsync) =====

    [Fact]
    public async Task Gap_GetGapsAsync_NoData_ReturnsMissingWindows()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", now.AddDays(-2), now, "{}"), CancellationToken.None);
        Assert.NotNull(res);
        Assert.NotEmpty(res.Windows);
        Assert.True(res.MaxBackfillStartUtc <= now);
    }

    [Fact]
    public async Task Gap_GetGapsAsync_FallbackOnly_ReturnsFallbackWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.fallback",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            WindowEndUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            TotalTimeVisibleMs = 60_000,
            SourceKind = "usage-stats-fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = DateTimeOffset.Parse("2026-07-05T12:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-05T12:00:00Z")
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-05T00:00:00Z"), now, "{}"), CancellationToken.None);
        Assert.Contains(res.Windows, w => w.Reason == "fallback-only");
    }

    [Fact]
    public async Task Gap_GetGapsAsync_CompletedBatch_TreatsAsCovered()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-covered",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            WindowEndUtc = now,
            AcceptedCount = 0,
            FailedCount = 0,
            Status = "completed",
            ErrorJson = "{}",
            CreatedAt = now.AddMinutes(-5),
            CompletedAtUtc = now.AddMinutes(-4)
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-06T00:00:00Z"), now, "{}"), CancellationToken.None);
        Assert.Empty(res.Windows);
    }

    // ===== MobileQualityService (GetQualityReportAsync ~ GetQualityAsync) =====

    [Fact]
    public async Task Quality_GetQualityAsync_Empty_ReturnsComponents()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var res = await svc.GetQualityAsync(null, null, CancellationToken.None);
        Assert.NotNull(res);
        Assert.NotEmpty(res.Components);
        Assert.Contains(res.Components, c => c.Key == "mobile-usage-coverage");
    }

    [Fact]
    public async Task Quality_GetQualityAsync_WithFallback_ReportsWarning()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            DeviceHash = "hash",
            DisplayName = "Test",
            Manufacturer = "PIM",
            Brand = "PIM",
            Model = "M1",
            OsVersion = "14",
            ApiLevel = 34,
            AppVersion = "1.0.0",
            MetadataJson = "{}",
            RegisteredAtUtc = now.AddDays(-1),
            LastSeenAtUtc = now,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.fallback",
            WindowStartUtc = now.AddHours(-2),
            WindowEndUtc = now.AddHours(-1),
            TotalTimeVisibleMs = 120_000,
            SourceKind = "usage-stats-fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now, CancellationToken.None);
        Assert.Contains(res.Issues, i => i.Code == "mobile-usage-fallback-only");
    }

    [Fact]
    public async Task Quality_CleanupAnomalousDataAsync_MarksLongSession()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.example.long",
            StartUtc = RangeStart,
            EndUtc = RangeStart.AddHours(9),
            DurationMs = 9L * 60 * 60 * 1000,
            QualityFlagsJson = "[]",
            CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var count = await svc.CleanupAnomalousDataAsync(CancellationToken.None);
        Assert.True(count >= 1);
    }

    // ===== MobileTimelineBlockService =====

    [Fact]
    public async Task TimelineBlock_GetBlocksAsync_Empty_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileTimelineBlockService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var page = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd), CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task TimelineBlock_GetBlocksAsync_WithSession_ReturnsBlock()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.tencent.mobileqq",
            DisplayName = "QQ",
            Category = "聊天",
            IsSystemApp = false,
            UpdatedAt = BaseTime
        });
        db.Set<MobileAppCatalogOverrideEntity>().Add(new MobileAppCatalogOverrideEntity
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = "com.tencent.mobileqq",
            LifeCategory = MobileLifeCategories.Chat,
            IsSystemNoise = false,
            HideShortEvents = false,
            UpdatedAt = BaseTime
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            Id = Guid.NewGuid(),
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.tencent.mobileqq",
            StartUtc = DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
            EndUtc = DateTimeOffset.Parse("2026-07-07T10:05:00Z"),
            DurationMs = 300 * 1000L,
            QualityFlagsJson = "[]"
        });
        await db.SaveChangesAsync();
        var svc = new MobileTimelineBlockService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var page = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.NotEmpty(page.Items);
        Assert.Equal("聊天", page.Items[0].LifeCategory);
    }

    // ===== MobileUsageGoalService (GetGoalsAsync ~ ListAsync) =====

    [Fact]
    public async Task UsageGoal_ListAsync_Empty_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var goals = await svc.ListAsync(CancellationToken.None);
        Assert.Empty(goals);
    }

    [Fact]
    public async Task UsageGoal_SaveAsync_ThenList_ReturnsGoal()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var svc = new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(BaseTime));
        var saved = await svc.SaveAsync(new MobileUsageGoalUpsertRequest("total-daily", null, null, "每日总时长", 7200, true), CancellationToken.None);
        Assert.Equal("total-daily", saved.Scope);
        var goals = await svc.ListAsync(CancellationToken.None);
        Assert.Single(goals);
        Assert.Equal(saved.Id, goals[0].Id);
    }

    // ===== helpers =====

    private static MobileUsageAggregationService CreateUsageAggregation(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var tp = MobileTestHelpers.Time(now);
        return new MobileUsageAggregationService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileAnalyticsQueryService(tp),
            new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), tp),
            tp);
    }

    private static MobileLocationAggregationService CreateLocationAggregation(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var tp = MobileTestHelpers.Time(now);
        return new MobileLocationAggregationService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(tp),
            tp);
    }

    private static void SeedSession(Pim.Infrastructure.Data.PimDbContext db, string pkg, DateTimeOffset start, DateTimeOffset end, string deviceId = "phone-main")
    {
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = pkg,
            StartUtc = start,
            EndUtc = end,
            DurationMs = (long)(end - start).TotalMilliseconds,
            QualityFlagsJson = "[]",
            CreatedAt = start
        });
    }

    private static void SeedLocation(Pim.Infrastructure.Data.PimDbContext db, string id, string recordedAt, double lat, double lon, double accuracy, string quality, string deviceId = "pixel-8")
    {
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            Id = Guid.Parse(id),
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = DateTimeOffset.Parse(recordedAt),
            Latitude = (decimal)lat,
            Longitude = (decimal)lon,
            HorizontalAccuracyMeters = (decimal)accuracy,
            Provider = "gps",
            Source = "auto",
            Quality = quality,
            RawJson = "{}",
            CreatedAt = DateTimeOffset.Parse(recordedAt)
        });
    }
}
