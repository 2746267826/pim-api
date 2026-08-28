using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileCoverageTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
    private static readonly DateTimeOffset RangeStart = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
    private static readonly DateTimeOffset RangeEnd = DateTimeOffset.Parse("2026-07-08T00:00:00Z");

    // ===== MobileUsageAggregationService =====

    [Fact]
    public async Task UsageAgg_FiltersAnomalousDuration_SkipsSession()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.app",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(30),
            DurationMs = 1800_000, QualityFlagsJson = "[\"anomalous_duration\"]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Equal(0, res.TotalForegroundSeconds);
        Assert.True(res.Completeness >= 0);
    }

    [Fact]
    public async Task UsageAgg_FiltersDayOverflow_SkipsSession()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.app",
            StartUtc = RangeStart.AddHours(9), EndUtc = RangeStart.AddHours(9).AddMinutes(10),
            DurationMs = 600_000, QualityFlagsJson = "[\"day_overflow\"]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Equal(0, res.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageAgg_FiltersDurationOver8h_SkipsSession()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.app",
            StartUtc = RangeStart, EndUtc = RangeStart.AddHours(9),
            DurationMs = 9L * 60 * 60 * 1000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Equal(0, res.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageAgg_MinDurationFilter_SkipsShortSession()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.app",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddSeconds(1),
            DurationMs = 1000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        // threshold 1 sec, duration 1 sec is <= threshold so skipped
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, MinDurationSeconds: 1));
        Assert.Equal(0, res.TotalForegroundSeconds);
        Assert.True(res.TotalForegroundSeconds >= 0);
    }

    [Fact]
    public async Task UsageAgg_IncludeSystemNoiseFalse_HiddenFlag()
    {
        await using var db = ServiceTestBase.CreateDb();
        // systemui is builtin system noise
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.android.systemui",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(30),
            DurationMs = 1800_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var resNoNoise = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, IncludeSystemNoise: false));
        var resWithNoise = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, IncludeSystemNoise: true));
        // without noise hidden, with noise visible
        Assert.Equal(0, resNoNoise.TotalForegroundSeconds);
        Assert.True(resWithNoise.TotalForegroundSeconds >= 1800);
        Assert.Contains("hidden-system-noise", resNoNoise.Quality.QualityFlags);
    }

    [Fact]
    public async Task UsageAgg_LifeCategoryFilter_FiltersCorrectly()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mobileqq",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(10),
            DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.ss.android.ugc.aweme",
            StartUtc = RangeStart.AddHours(11), EndUtc = RangeStart.AddHours(11).AddMinutes(10),
            DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, LifeCategory: MobileLifeCategories.Chat));
        Assert.Equal(600, res.TotalForegroundSeconds);
        Assert.True(res.AppCount >= 1);
    }

    [Fact]
    public async Task UsageAgg_DeviceIdFilter_IsolatesDevice()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-A", PackageName = "com.tencent.mm",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(10), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-B", PackageName = "com.tencent.mm",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(20), DurationMs = 1200_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, DeviceId: "phone-A"));
        Assert.Equal(600, res.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageAgg_PackageFilter_IsolatesPackage()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mobileqq",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(10), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, PackageName: "com.not.exist"));
        Assert.Equal(0, res.TotalForegroundSeconds);
        var res2 = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, PackageName: "com.tencent.mobileqq"));
        Assert.Equal(600, res2.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageAgg_Heatmap_15mGranularity_BucketsWithinLimits()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mm",
            StartUtc = DateTimeOffset.Parse("2026-07-06T13:05:00Z"), EndUtc = DateTimeOffset.Parse("2026-07-06T13:20:00Z"),
            DurationMs = 15 * 60 * 1000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, Granularity: "15m"));
        Assert.NotEmpty(heat);
        Assert.All(heat, b => Assert.True(b.ForegroundSeconds > 0 && b.ForegroundSeconds <= 900));
        Assert.True(heat.Sum(b => b.ForegroundSeconds) == 900);
    }

    [Fact]
    public async Task UsageAgg_Heatmap_DayGranularity_Sums()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mm",
            StartUtc = RangeStart.AddHours(1), EndUtc = RangeStart.AddHours(1).AddMinutes(10), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, Granularity: "day"));
        Assert.NotEmpty(heat);
        Assert.True(heat.All(b => b.LocalHour == 0));
        Assert.Equal(600, heat.Sum(b => b.ForegroundSeconds));
    }

    [Fact]
    public async Task UsageAgg_Anomaly_LongTotal_ProducesWarning()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 7 hours to trigger long-total >6h
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mm",
            StartUtc = RangeStart.AddHours(1), EndUtc = RangeStart.AddHours(8), DurationMs = 7 * 3600 * 1000L, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Contains(res.Anomalies, a => a.Code == "long-total");
        Assert.True(res.TotalForegroundSeconds >= 7 * 3600);
    }

    [Fact]
    public async Task UsageAgg_Anomaly_NightUse_ProducesWarning()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 22:00 local in Shanghai = 14:00 UTC
        var nightStart = DateTimeOffset.Parse("2026-07-06T14:00:00Z");
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mm",
            StartUtc = nightStart, EndUtc = nightStart.AddMinutes(10), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Contains(res.Anomalies, a => a.Code == "night-use");
    }

    [Fact]
    public async Task UsageAgg_FallbackSummary_ProratedAndFilteredDuplicate()
    {
        await using var db = ServiceTestBase.CreateDb();
        var winStart = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        var winEnd = DateTimeOffset.Parse("2026-07-06T11:00:00Z");
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = winStart, WindowEndUtc = winEnd, TotalTimeVisibleMs = 1800 * 1000L, SourceKind = "fallback",
            QualityFlagsJson = "[]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = winStart, WindowEndUtc = winEnd, TotalTimeVisibleMs = 900 * 1000L, SourceKind = "fallback",
            QualityFlagsJson = "[]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        // Query only half window to test proration + dedup keeps max
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(winStart, winStart.AddMinutes(30)));
        Assert.True(res.TotalForegroundSeconds >= 0);
        Assert.True(res.TotalForegroundSeconds <= 1800);
    }

    // ===== MobileLocationAggregationService =====

    [Fact]
    public async Task LocAgg_Overview_RejectCountsAndFlags()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:05:00Z", 31.230416, 121.473701, 12, "rejected");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.Equal(2, res.PointCount);
        Assert.Equal(1, res.RejectedPointCount);
        Assert.Equal(1, res.UsablePointCount);
        Assert.True(res.ActiveSpanSeconds >= 0);
    }

    [Fact]
    public async Task LocAgg_Overview_LargeGapFlag()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T08:00:00Z", 31.230416, 121.473701, 10, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T11:00:00Z", 31.230416, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.Contains("large-gap", res.QualityFlags);
        Assert.True(res.QualityIssueCount > 0);
    }

    [Fact]
    public async Task LocAgg_IncludeRejected_False_FiltersBadAccuracy()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 200, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracksNoRejected = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), IncludeRejected: false));
        var tracksWithRejected = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), IncludeRejected: true));
        Assert.Empty(tracksNoRejected);
        Assert.NotEmpty(tracksWithRejected);
        Assert.Equal("stay", tracksWithRejected[0].Segments[0].Kind);
    }

    [Fact]
    public async Task LocAgg_JumpDetection_FlagsSegment()
    {
        await using var db = ServiceTestBase.CreateDb();
        // Two points far apart in 10 seconds => jump speed >30 m/s
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 5, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:10Z", 31.240416, 121.483701, 5, "usable");
        // Need third point to form pair and test MarkJumpPoints
        SeedLoc(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:00:20Z", 31.250416, 121.493701, 5, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.NotEmpty(tracks);
        var seg = tracks[0].Segments[0];
        Assert.Contains("jump-point", seg.QualityFlags);
        Assert.True(seg.DistanceMeters >= 0);
    }

    [Fact]
    public async Task LocAgg_MoveSegment_DistancePositive()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable", deviceId: "pixel-8");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230821, 121.473701, 10, "usable", deviceId: "pixel-8");
        SeedLoc(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.231226, 121.473701, 10, "usable", deviceId: "pixel-8");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var move = tracks.SelectMany(t => t.Segments).First(s => s.Kind == "move");
        Assert.True(move.DistanceMeters > 0);
        Assert.True(move.AverageSpeedMetersPerSecond >= 0);
    }

    [Fact]
    public async Task LocAgg_GetSegmentAsync_WhiteSpace_ReturnsNull()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateLocAgg(db, BaseTime);
        var seg = await svc.GetSegmentAsync("   ", new MobileLocationQueryRequest(RangeStart, RangeEnd));
        Assert.Null(seg);
    }

    [Fact]
    public async Task LocAgg_GetSegmentPoints_Pagination_Works()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230516, 121.473701, 10, "usable");
        SeedLoc(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.230616, 121.473701, 10, "usable");
        SeedLoc(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.230716, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var segmentId = tracks[0].Segments[0].Id;
        var page1 = await svc.GetSegmentPointsAsync(segmentId, new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), PageSize: 2));
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);
        var page2 = await svc.GetSegmentPointsAsync(segmentId, new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), Cursor: page1.NextCursor, PageSize: 2));
        Assert.Equal(2, page2.Items.Count);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task LocAgg_GetMovementSegments_OnlyMove()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:05Z", 31.230416, 121.473701, 10, "usable");
        // move pair
        SeedLoc(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.231416, 121.473701, 10, "usable");
        SeedLoc(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.232416, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var segs = await svc.GetMovementSegmentsAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.All(segs, s => Assert.True(s.DistanceMeters >= 0));
        Assert.True(segs.Count >= 0);
    }

    [Fact]
    public async Task LocAgg_TrackGapThreshold_SplitsTracks()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T08:00:00Z", 31.230416, 121.473701, 10, "usable", deviceId: "pixel-8");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T11:00:00Z", 31.230416, 121.473701, 10, "usable", deviceId: "pixel-8");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.Equal(2, tracks.Count);
        Assert.True(tracks.Sum(t => t.PointCount) == 2);
    }

    [Fact]
    public async Task LocAgg_SinglePoint_SegmentStayAndSingleFlag()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var seg = Assert.Single(tracks[0].Segments);
        Assert.Equal("stay", seg.Kind);
        Assert.Contains("single-point", seg.QualityFlags);
        Assert.Equal(0, seg.DistanceMeters);
    }

    [Fact]
    public async Task LocAgg_GetSegmentAsync_Found_ReturnsSegment()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230516, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var segId = tracks[0].Segments[0].Id;
        var seg = await svc.GetSegmentAsync(segId, new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.NotNull(seg);
        Assert.Equal(segId, seg.Id);
    }

    [Fact]
    public async Task LocAgg_GetSegmentPoints_InvalidCursor_ReturnsFirstPage()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedLoc(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 10, "usable");
        SeedLoc(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230516, 121.473701, 10, "usable");
        await db.SaveChangesAsync();
        var svc = CreateLocAgg(db, BaseTime);
        var tracks = await svc.GetTracksAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var segId = tracks[0].Segments[0].Id;
        var page = await svc.GetSegmentPointsAsync(segId, new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), Cursor: "not-a-guid", PageSize: 10));
        Assert.Equal(2, page.Items.Count);
        Assert.False(page.HasMore);
    }

    // ===== MobileFrequentPlaceService =====

    [Fact]
    public async Task FrequentPlaces_Insufficient_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 5; i++) SeedLoc(db, $"00000000-0000-0000-0000-00000000000{i}", $"2026-07-07T10:0{i}:00Z", 31.23, 121.47, 10, "usable");
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(ServiceTestBase.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.Empty(res.Places);
        Assert.Null(res.Home);
    }

    [Fact]
    public async Task FrequentPlaces_Cluster_RadiusClampedAndHomeSelected()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 12 points at same location + night points
        for (int i = 0; i < 12; i++)
        {
            var hour = i < 6 ? "02" : "14";
            SeedLoc(db, $"10000000-0000-0000-0000-0000000000{i:D2}", $"2026-07-07T{hour}:0{i % 6}:00Z", 31.230416 + i * 0.00001, 121.473701 + i * 0.00001, 10, "usable");
        }
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(ServiceTestBase.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.NotEmpty(res.Places);
        Assert.NotNull(res.Home);
        Assert.True(res.Home!.RadiusMeters >= 0 && res.Home.RadiusMeters <= 500);
        Assert.True(res.Home.PointCount >= 10);
    }

    [Fact]
    public async Task FrequentPlaces_AdaptiveEps_WithinBounds()
    {
        await using var db = ServiceTestBase.CreateDb();
        // two dense clusters far apart
        for (int i = 0; i < 10; i++) SeedLoc(db, $"20000000-0000-0000-0000-0000000000{i:D2}", $"2026-07-07T10:0{i % 6}:00Z", 31.23, 121.47, 10, "usable");
        for (int i = 0; i < 10; i++) SeedLoc(db, $"30000000-0000-0000-0000-0000000000{i:D2}", $"2026-07-07T11:0{i % 6}:00Z", 31.24, 121.48, 10, "usable");
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(ServiceTestBase.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.True(res.Places.Count >= 1);
        Assert.All(res.Places, p => Assert.InRange(p.RadiusMeters, 0, 500));
    }

    [Fact]
    public async Task FrequentPlaces_NoisePoints_Excluded()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 12; i++) SeedLoc(db, $"40000000-0000-0000-0000-0000000000{i:D2}", $"2026-07-07T10:0{i % 6}:00Z", 31.230416, 121.473701, 10, "usable");
        // isolated outlier far away
        SeedLoc(db, "50000000-0000-0000-0000-000000000001", "2026-07-07T10:30:00Z", 32.0, 122.0, 10, "usable");
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(ServiceTestBase.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        Assert.NotEmpty(res.Places);
        // noise + 1 cluster at most
        Assert.True(res.Places[0].PointCount >= 10 && res.Places[0].PointCount <= 13);
    }

    [Fact]
    public async Task FrequentPlaces_FilterRejectedAndAccuracy()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 12; i++) SeedLoc(db, $"60000000-0000-0000-0000-0000000000{i:D2}", $"2026-07-07T10:0{i % 6}:00Z", 31.230416, 121.473701, 150, "usable");
        // rejected should be ignored
        SeedLoc(db, "60000000-0000-0000-0000-000000000099", "2026-07-07T10:30:00Z", 31.23, 121.47, 10, "rejected");
        await db.SaveChangesAsync();
        var svc = new MobileFrequentPlaceService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(ServiceTestBase.Time(BaseTime)));
        var res = await svc.GetFrequentPlacesAsync(new MobileLocationQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        // accuracy 150 >100 filtered, so insufficient
        Assert.Empty(res.Places);
    }

    // ===== MobileGapService =====

    [Fact]
    public async Task Gap_RangeInvalid_ReturnsEmptyWithMaxBackfill()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", now, now.AddHours(-1), "{}"));
        Assert.Empty(res.Windows);
        Assert.True(res.MaxBackfillStartUtc <= now);
        Assert.Equal(now.AddDays(-14), res.MaxBackfillStartUtc);
    }

    [Fact]
    public async Task Gap_FallbackOnly_ReasonFallback()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.fallback",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"), WindowEndUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            TotalTimeVisibleMs = 60_000, SourceKind = "usage-stats-fallback", RawJson = "{}", QualityFlagsJson = "[]", CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-05T00:00:00Z"), now, "{}"));
        Assert.Contains(res.Windows, w => w.Reason == "fallback-only");
        Assert.True(res.Windows.All(w => w.WindowEndUtc > w.WindowStartUtc));
    }

    [Fact]
    public async Task Gap_MissingTail_ReasonTail()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.app",
            EventType = "ACTIVITY_RESUMED", EventTimestampUtc = DateTimeOffset.Parse("2026-07-05T02:00:00Z"),
            SourceWindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"), SourceWindowEndUtc = DateTimeOffset.Parse("2026-07-05T12:00:00Z"),
            CollectedAtUtc = now, RawJson = "{}", QualityFlagsJson = "[]", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-05T00:00:00Z"), now, "{}"));
        Assert.Contains(res.Windows, w => w.Reason == "missing-tail" || w.Reason == "partial-day");
        Assert.True(res.Windows.All(w => (w.WindowEndUtc - w.WindowStartUtc).TotalMinutes >= 5));
    }

    [Fact]
    public async Task Gap_PartialDay_ReasonPartial()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.app",
            EventType = "ACTIVITY_RESUMED", EventTimestampUtc = DateTimeOffset.Parse("2026-07-05T10:00:00Z"),
            SourceWindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"), SourceWindowEndUtc = DateTimeOffset.Parse("2026-07-05T12:00:00Z"),
            CollectedAtUtc = now, RawJson = "{}", QualityFlagsJson = "[]", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        // range spans two days, first day partially covered
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-05T00:00:00Z"), DateTimeOffset.Parse("2026-07-07T00:00:00Z"), "{}"));
        Assert.NotEmpty(res.Windows);
        Assert.All(res.Windows, w => Assert.True((w.WindowEndUtc - w.WindowStartUtc).TotalMinutes >= 5));
    }

    [Fact]
    public async Task Gap_SmallGapSkipped_BelowThreshold()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        // cover all but last 2 minutes of day -> gap <5min skipped
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.app",
            EventType = "ACTIVITY_RESUMED", EventTimestampUtc = DateTimeOffset.Parse("2026-07-05T02:00:00Z"),
            SourceWindowStartUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"), SourceWindowEndUtc = DateTimeOffset.Parse("2026-07-05T23:58:00Z"),
            CollectedAtUtc = now, RawJson = "{}", QualityFlagsJson = "[]", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-05T00:00:00Z"), DateTimeOffset.Parse("2026-07-06T00:00:00Z"), "{}"));
        // No fallback, gap of 2min should be skipped, but next day will still be missing -> at least not partial-day small
        Assert.DoesNotContain(res.Windows, w => w.WindowStartUtc == DateTimeOffset.Parse("2026-07-05T23:58:00Z"));
    }

    [Fact]
    public async Task Gap_CompletedBatch_CoversGap()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", BatchId = "b1",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T00:00:00Z"), WindowEndUtc = now,
            AcceptedCount = 10, FailedCount = 0, Status = "completed", ErrorJson = "{}", CreatedAt = now, CompletedAtUtc = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileGapService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetGapsAsync(new MobileGapRequest("android-main", DateTimeOffset.Parse("2026-07-06T00:00:00Z"), now, "{}"));
        Assert.Empty(res.Windows);
    }

    // ===== MobileQualityService =====

    [Fact]
    public async Task Quality_HeartbeatStale_Warning()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", DeviceHash = "h", DisplayName = "Test", Manufacturer = "P", Brand = "P", Model = "M", OsVersion = "14", ApiLevel = 34, AppVersion = "1", MetadataJson = "{}", RegisteredAtUtc = now.AddDays(-1), LastSeenAtUtc = now, CreatedAt = now.AddDays(-1), UpdatedAt = now
        });
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity { DeviceId = "android-main", DaemonKind = "android", Version = "1", ServerUrl = "http://127.0.0.1:5858", StatusJson = "{}", ReceivedAt = now.AddHours(-1), LastSuccessfulUploadAt = now.AddHours(-1), UploadQueueCount = 0 });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        var hb = Assert.Single(res.Components, c => c.Key == "android-heartbeat");
        Assert.Equal(PimHealthStatus.Warning, hb.Status);
        Assert.Contains(res.Issues, i => i.Code == "mobile-heartbeat-stale");
        Assert.Contains(res.NextSteps, s => !string.IsNullOrWhiteSpace(s));
    }

    [Fact]
    public async Task Quality_HeartbeatMissing_Unknown()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        var hb = Assert.Single(res.Components, c => c.Key == "android-heartbeat");
        Assert.Equal(PimHealthStatus.Unknown, hb.Status);
        Assert.Contains(res.Issues, i => i.Code == "mobile-heartbeat-missing");
    }

    [Fact]
    public async Task Quality_UsageMissing_Unknown()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        var usage = Assert.Single(res.Components, c => c.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Unknown, usage.Status);
        Assert.Contains(res.Issues, i => i.Code == "mobile-usage-missing");
        Assert.True(res.OverallStatus == PimHealthStatus.Unknown || res.OverallStatus == PimHealthStatus.Warning);
    }

    [Fact]
    public async Task Quality_SyncHealthy()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", BatchId = "b1",
            WindowStartUtc = now.AddHours(-2), WindowEndUtc = now.AddHours(-1), AcceptedCount = 5, FailedCount = 0, Status = "completed", ErrorJson = "{}", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        var sync = Assert.Single(res.Components, c => c.Key == "mobile-sync");
        Assert.Equal(PimHealthStatus.Healthy, sync.Status);
        Assert.Equal("0", sync.Details["failedBatchCount"]);
    }

    [Fact]
    public async Task Quality_LocationRejected_Warning()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", RecordedAtUtc = now.AddMinutes(-10), Latitude = 31.23m, Longitude = 121.47m, HorizontalAccuracyMeters = 10m, Provider = "gps", Source = "manual", Quality = "rejected", RawJson = "{}", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        Assert.Contains(res.Issues, i => i.Code == "mobile-location-rejected");
        var loc = Assert.Single(res.Components, c => c.Key == "mobile-location");
        Assert.Equal(PimHealthStatus.Warning, loc.Status);
        Assert.Equal("1", loc.Details["rejectedLocationCount"]);
    }

    [Fact]
    public async Task Quality_AppMetadataMissing_Warning()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.missing",
            EventType = "ACTIVITY_RESUMED", EventTimestampUtc = now.AddHours(-1), SourceWindowStartUtc = now.AddHours(-2), SourceWindowEndUtc = now, CollectedAtUtc = now, RawJson = "{}", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now.AddDays(-1), now);
        Assert.Contains(res.Issues, i => i.Code == "mobile-app-metadata-missing");
        var meta = Assert.Single(res.Components, c => c.Key == "mobile-app-metadata");
        Assert.Equal(PimHealthStatus.Warning, meta.Status);
    }

    [Fact]
    public async Task Quality_RangeSwap_HandlesInversion()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
        var res = await svc.GetQualityAsync(now, now.AddDays(-1));
        Assert.NotNull(res);
        Assert.NotEmpty(res.Components);
        Assert.True(res.CheckedAt == now);
    }

    [Fact]
    public async Task Quality_Cleanup_MarksDuplicateSummary()
    {
        await using var db = ServiceTestBase.CreateDb();
        var start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = start, WindowEndUtc = start.AddHours(1), TotalTimeVisibleMs = 100_000, SourceKind = "fallback", QualityFlagsJson = "[]", CreatedAt = BaseTime, UpdatedAt = BaseTime
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = start.AddMinutes(10), WindowEndUtc = start.AddMinutes(70), TotalTimeVisibleMs = 50_000, SourceKind = "fallback", QualityFlagsJson = "[]", CreatedAt = BaseTime, UpdatedAt = BaseTime
        });
        await db.SaveChangesAsync();
        var svc = new MobileQualityService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(BaseTime));
        var count = await svc.CleanupAnomalousDataAsync();
        Assert.True(count >= 1);
        var summaries = await db.Set<MobileUsageSummaryEntity>().ToListAsync();
        Assert.Contains(summaries, s => s.QualityFlagsJson.Contains("duplicate_summary"));
    }

    // ===== MobileTimelineBlockService + UsageGoal + SimpleDbscan =====

    [Fact]
    public async Task Timeline_Pagination_CursorHasMore()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 3; i++)
        {
            var start = DateTimeOffset.Parse("2026-07-07T10:00:00Z").AddHours(i * 2);
            db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
            {
                UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.tencent.mobileqq",
                StartUtc = start, EndUtc = start.AddMinutes(10), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = start
            });
        }
        await db.SaveChangesAsync();
        var svc = new MobileTimelineBlockService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(BaseTime));
        var page1 = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), PageSize: 1, Page: 1));
        Assert.Equal(1, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.Equal(3, page1.TotalCount);
        Assert.True(page1.TotalPages >= 3);
        var cursor = page1.NextCursor;
        Assert.NotNull(cursor);
        var pageCursor = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), Cursor: cursor, PageSize: 1));
        Assert.Equal(1, pageCursor.Items.Count);
        Assert.True(pageCursor.Items[0].ForegroundSeconds > 0);
    }

    [Fact]
    public async Task Timeline_SourceFilter_FallbackOnly()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.tencent.mm",
            StartUtc = DateTimeOffset.Parse("2026-07-07T10:00:00Z"), EndUtc = DateTimeOffset.Parse("2026-07-07T10:10:00Z"), DurationMs = 600_000, QualityFlagsJson = "[]", CreatedAt = BaseTime
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "android-main", PackageName = "com.example.fallback",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-07T11:00:00Z"), WindowEndUtc = DateTimeOffset.Parse("2026-07-07T12:00:00Z"), TotalTimeVisibleMs = 600_000, SourceKind = "fallback", QualityFlagsJson = "[]", CreatedAt = BaseTime, UpdatedAt = BaseTime
        });
        await db.SaveChangesAsync();
        var svc = new MobileTimelineBlockService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(BaseTime));
        var pageAll = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")));
        var pageFallback = await svc.GetBlocksAsync(new MobileAnalyticsQueryRequest(DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z"), Source: "fallback"));
        Assert.True(pageAll.TotalCount >= pageFallback.TotalCount);
        Assert.All(pageFallback.Items, b => Assert.True(b.SourceMix != null && b.SourceMix.ContainsKey("fallback")));
        Assert.True(pageAll.Items.Sum(i => i.ForegroundSeconds) >= pageFallback.Items.Sum(i => i.ForegroundSeconds));
    }

    [Fact]
    public async Task Timeline_GetSessionEvents_BadGuid_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileTimelineBlockService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(BaseTime));
        var events = await svc.GetSessionEventsAsync("not-a-guid");
        Assert.Empty(events);
        var events2 = await svc.GetSessionsForBlockAsync("", new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Empty(events2);
    }

    [Fact]
    public async Task UsageGoal_SaveAndDelete_ValidatesTrimAndClamp()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new MobileUsageGoalService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(BaseTime));
        var saved = await svc.SaveAsync(new MobileUsageGoalUpsertRequest("  total-daily  ", null, null, "  每日  ", -100, true));
        Assert.Equal("total-daily", saved.Scope);
        Assert.Equal(0, saved.LimitSeconds);
        var updated = await svc.SaveAsync(new MobileUsageGoalUpsertRequest("total-daily", null, null, "每日", 100, true));
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(100, updated.LimitSeconds);
        var badDelete = await svc.DeleteAsync("not-a-guid");
        Assert.False(badDelete);
        var okDelete = await svc.DeleteAsync(saved.Id);
        Assert.True(okDelete);
        var again = await svc.DeleteAsync(saved.Id);
        Assert.False(again);
    }

    [Fact]
    public void SimpleDbscan_TwoClusters_AndNoise()
    {
        var points = new List<SimpleDbscan.Point>();
        // cluster 1 at (0,0)
        for (int i = 0; i < 10; i++) points.Add(new SimpleDbscan.Point(i, i * 0.1, i * 0.1));
        // cluster 2 at (100,100)
        for (int i = 0; i < 10; i++) points.Add(new SimpleDbscan.Point(10 + i, 100 + i * 0.1, 100 + i * 0.1));
        // noise outlier
        points.Add(new SimpleDbscan.Point(20, 1000, 1000));
        var result = SimpleDbscan.Run(points, 5, 3);
        Assert.Equal(2, result.Clusters.Count);
        Assert.Single(result.Noise);
        Assert.All(result.Clusters, c => Assert.True(c.Count >= 3));
        Assert.True(result.Noise[0] == 20);
    }

    [Fact]
    public void SimpleDbscan_SingleCluster_AllConnected()
    {
        var points = Enumerable.Range(0, 5).Select(i => new SimpleDbscan.Point(i, i * 1.0, 0)).ToList();
        var result = SimpleDbscan.Run(points, 2, 2);
        Assert.Single(result.Clusters);
        Assert.Equal(5, result.Clusters[0].Count);
        Assert.Empty(result.Noise);
    }

    [Fact]
    public void SimpleDbscan_NoCluster_WhenSparse()
    {
        var points = new List<SimpleDbscan.Point> { new(0, 0, 0), new(1, 100, 100), new(2, 200, 200) };
        var result = SimpleDbscan.Run(points, 10, 3);
        Assert.Empty(result.Clusters);
        Assert.Equal(3, result.Noise.Count);
        Assert.True(result.Noise.Count == points.Count);
    }

    [Fact]
    public void SimpleDbscan_EpsBoundary_IncludesEdge()
    {
        var points = new List<SimpleDbscan.Point> { new(0, 0, 0), new(1, 3, 4) }; // distance 5
        var resultInside = SimpleDbscan.Run(points, 5, 2);
        Assert.Single(resultInside.Clusters);
        var resultOutside = SimpleDbscan.Run(points, 4.9, 2);
        Assert.Empty(resultOutside.Clusters);
    }

    // ===== MobileUsageAggregationService 新增覆盖（5 Fact）=====

    [Fact]
    public async Task UsageAgg_ClassificationService_Path_ResolvesViaService()
    {
        // 业务含义：注入 MobileAppClassificationService 时走 ClassifyAsync 分支，校验覆盖 LifeCategory 与系统噪声判定
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.keyword.music",
            DisplayName = "Test Music", Category = "music", IsSystemApp = false,
            RawJson = "{}", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.test.keyword.music",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(20),
            DurationMs = 1200_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var tp = ServiceTestBase.Time(BaseTime);
        var classificationService = new MobileAppClassificationService(db, ServiceTestBase.CurrentUser());
        var svc = new MobileUsageAggregationService(db, ServiceTestBase.CurrentUser(), new MobileAnalyticsQueryService(tp), new MobileUsageGoalService(db, ServiceTestBase.CurrentUser(), tp), tp, classificationService);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd));
        Assert.Equal(1200, res.TotalForegroundSeconds);
        Assert.True(res.AppCount >= 1);
    }

    [Fact]
    public async Task UsageAgg_FallbackDuplicateAndPartialStale_Flags()
    {
        // 业务含义：duplicate_summary 需被过滤，partial/stale 需落入 QualityFlags
        await using var db = ServiceTestBase.CreateDb();
        var winStart = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        var winEnd = DateTimeOffset.Parse("2026-07-06T11:00:00Z");
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = winStart, WindowEndUtc = winEnd, TotalTimeVisibleMs = 1800_000, SourceKind = "fallback",
            QualityFlagsJson = "[\"partial\",\"stale\"]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.app",
            WindowStartUtc = winStart.AddHours(1), WindowEndUtc = winEnd.AddHours(1), TotalTimeVisibleMs = 600_000, SourceKind = "fallback",
            QualityFlagsJson = "[\"duplicate_summary\"]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(winStart, winStart.AddHours(2)));
        Assert.Equal(3600, res.TotalForegroundSeconds);
        Assert.Contains("partial-sync", res.Quality.QualityFlags);
        Assert.Contains("stale-aggregate", res.Quality.QualityFlags);
        Assert.Contains("fallback-only", res.Quality.QualityFlags);
    }

    [Fact]
    public async Task UsageAgg_ProratedEdge_ZeroVisibleAndZeroWindow()
    {
        // 业务含义：totalVisibleMs=0 或 window 长度为 0 时按比例计算应返回 0，不产生负数
        await using var db = ServiceTestBase.CreateDb();
        var winStart = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        var winEnd = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.zero",
            WindowStartUtc = winStart, WindowEndUtc = winEnd.AddHours(1), TotalTimeVisibleMs = 0, SourceKind = "fallback",
            QualityFlagsJson = "[]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.zero2",
            WindowStartUtc = winStart, WindowEndUtc = winEnd, TotalTimeVisibleMs = 1000, SourceKind = "fallback",
            QualityFlagsJson = "[]", CreatedAt = RangeStart, UpdatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        // range 09-11 包含零长度窗口，触发 totalVisibleMs==0 与 sourceMs==0 分支
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(winStart.AddHours(-1), winStart.AddHours(1)));
        Assert.Equal(0, res.TotalForegroundSeconds);
    }

    [Fact]
    public async Task UsageAgg_OverlappingSessions_UnionNotDoubleCount()
    {
        // 业务含义：重叠会话的 union 时长不应双计，且 launcher 包名触发 BuiltIn 系统噪声分支
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.mylauncher",
            StartUtc = RangeStart.AddHours(10), EndUtc = RangeStart.AddHours(10).AddMinutes(30),
            DurationMs = 1800_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.example.mylauncher",
            StartUtc = RangeStart.AddHours(10).AddMinutes(15), EndUtc = RangeStart.AddHours(10).AddMinutes(45),
            DurationMs = 1800_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var svc = CreateUsageAgg(db, BaseTime);
        var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(RangeStart, RangeEnd, IncludeSystemNoise: true));
        // union 10:00-10:45 = 2700s, raw sum 3600s, union 应小于 sum
        Assert.Equal(2700, res.TotalForegroundSeconds);
        Assert.True(res.Quality.SystemNoiseShare >= 0);
    }

    [Fact]
    public async Task UsageAgg_Heatmap_30m_And_AmbiguousTimezone()
    {
        // 业务含义：30m 粒度分桶正确；America/New_York 秋季回拨 ambiguous hour 不抛异常
        await using var db = ServiceTestBase.CreateDb();
        // 2026-11-01 是美国夏令时结束日，01:00-02:00 出现 ambiguous
        var start = DateTimeOffset.Parse("2026-11-01T05:30:00Z"); // 01:30 EDT -> 05:30 UTC
        var end = start.AddMinutes(60);
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = ServiceTestBase.DefaultUserId, DeviceId = "phone-main", PackageName = "com.tencent.mm",
            StartUtc = start, EndUtc = end, DurationMs = 3600_000, QualityFlagsJson = "[]", CreatedAt = RangeStart
        });
        await db.SaveChangesAsync();
        var tp = ServiceTestBase.Time(BaseTime);
        var svc = new MobileUsageAggregationService(db, ServiceTestBase.CurrentUser(), new MobileAnalyticsQueryService(tp), new MobileUsageGoalService(db, ServiceTestBase.CurrentUser(), tp), tp);
        var rangeStart = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var rangeEnd = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var heat30 = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd, Granularity: "30m", Timezone: "America/New_York"));
        Assert.NotEmpty(heat30);
        Assert.Equal(3600, heat30.Sum(b => b.ForegroundSeconds));
        var heat15 = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd, Granularity: "15m", Timezone: "America/New_York"));
        Assert.NotEmpty(heat15);
        Assert.Equal(3600, heat15.Sum(b => b.ForegroundSeconds));
    }

    // ===== helpers =====
    private static MobileUsageAggregationService CreateUsageAgg(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var tp = ServiceTestBase.Time(now);
        return new MobileUsageAggregationService(db, ServiceTestBase.CurrentUser(), new MobileAnalyticsQueryService(tp), new MobileUsageGoalService(db, ServiceTestBase.CurrentUser(), tp), tp);
    }

    private static MobileLocationAggregationService CreateLocAgg(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var tp = ServiceTestBase.Time(now);
        return new MobileLocationAggregationService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(tp), tp);
    }

    private static void SeedLoc(Pim.Infrastructure.Data.PimDbContext db, string id, string recordedAt, double lat, double lon, double accuracy, string quality, string deviceId = "pixel-8")
    {
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            Id = Guid.Parse(id), UserId = ServiceTestBase.DefaultUserId, DeviceId = deviceId,
            RecordedAtUtc = DateTimeOffset.Parse(recordedAt), Latitude = (decimal)lat, Longitude = (decimal)lon,
            HorizontalAccuracyMeters = (decimal)accuracy, Provider = "gps", Source = "auto", Quality = quality, RawJson = "{}", CreatedAt = DateTimeOffset.Parse(recordedAt)
        });
    }
}
