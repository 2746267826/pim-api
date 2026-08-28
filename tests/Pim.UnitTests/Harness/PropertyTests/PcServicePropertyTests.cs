using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class PcServicePropertyTests
{
    private static readonly DateTime TestDate = new(2026, 7, 7);
    private static readonly DateTimeOffset DayStart = PcTrackerService.GetBusinessDayStartForQuery(TestDate);

    private static AwEventEntity MakeWindow(string app, DateTimeOffset ts, double dur, string? normalized = null)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = ts,
            Duration = dur,
            EventType = "window",
            AppName = app,
            AppNameNormalized = normalized ?? app.ToLowerInvariant(),
            WindowTitle = app + " title",
            AfkStatus = null,
            DataJson = "{}",
            BucketType = "currentwindow",
            CreatedAt = ts,
            UpdatedAt = ts,
        };

    private static void SeedKeystats(PimDbContext db, DateTime date, int keyPresses = 1000)
    {
        db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity
        {
            DeviceId = "pc-1",
            SnapshotDate = date.Date,
            KeyPresses = keyPresses,
            LeftClicks = 200,
            RightClicks = 50,
            MiddleClicks = 10,
            MouseDistance = 123.4,
            ScrollDistance = 56.7,
            PeakKps = 5,
            PeakCps = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCounts = new List<KeystatsKeyCountEntity> { new() { KeyName = "a", Count = 100 } },
            AppBreakdowns = new List<KeystatsAppBreakdownEntity>
            {
                new() { AppName = "code.exe", DisplayName = "VS Code", KeyPresses = 600, LeftClicks = 100, RightClicks = 20, MiddleClicks = 5, ScrollDistance = 10 },
                new() { AppName = "chrome.exe", DisplayName = "Chrome", KeyPresses = 400, LeftClicks = 100, RightClicks = 30, MiddleClicks = 5, ScrollDistance = 20 },
            }
        });
    }

    // === PcTrackerService ===

    [Fact]
    public async Task PcTracker_GetSummaryAsync_EmptyDb_ReturnsDefaults()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(24, res.Heatmap.Count);
        Assert.Empty(res.Timeline);
        Assert.Empty(res.Sessions);
    }

    [Fact]
    public async Task PcTracker_GetSummaryAsync_WithWindowEvent_ReturnsHeatmapAndTimeline()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(6), 600));
        db.Set<AwEventEntity>().Add(MakeWindow("chrome.exe", DayStart.AddHours(6).AddMinutes(12), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);
        Assert.NotNull(res);
        Assert.True(res.Heatmap.Any(b => b.TotalEvents > 0));
        Assert.NotEmpty(res.Timeline);
        Assert.All(res.Heatmap, b => Assert.InRange(b.IntensityScore, 0, 5));
    }

    [Fact]
    public async Task PcTracker_GetTimelineAsync_Empty_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetTimelineAsync(TestDate, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Empty(res);
    }

    [Fact]
    public async Task PcTracker_GetTimelineAsync_WithEvents_ReturnsNormalized()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(9), 300));
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(9).AddMinutes(5), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetTimelineAsync(TestDate, CancellationToken.None);
        Assert.NotEmpty(res);
        Assert.All(res, t => Assert.True(t.DurationMinutes > 0));
    }

    [Fact]
    public async Task PcTracker_GetHeatmapAsync_Range_Returns24PerDay()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(2), 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var start = TestDate;
        var end = TestDate.AddDays(1);
        var res = await svc.GetHeatmapAsync(start, end, CancellationToken.None);
        Assert.Equal(48, res.Count);
        Assert.All(res, b => Assert.InRange(b.IntensityScore, 0, 5));
    }

    [Fact]
    public async Task PcTracker_GetCategorySummariesAsync_WithKeystats_ReturnsCategories()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetCategorySummariesAsync(TestDate, CancellationToken.None);
        Assert.NotNull(res);
        Assert.NotEmpty(res);
        Assert.True(res.Sum(c => c.Share) <= 100.5);
    }

    [Fact]
    public async Task PcTracker_QueryDetailAsync_Empty_ReturnsEmptyPage()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.QueryDetailAsync(new DetailQueryParams(null, null, null, null, null, null, null, null, null, null, 1, 10), CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(0, res.TotalCount);
        Assert.Empty(res.Items);
    }

    [Fact]
    public async Task PcTracker_QueryDetailAsync_WithKeystats_FiltersByAppName()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.QueryDetailAsync(new DetailQueryParams(null, null, null, null, "code", null, null, null, null, null, 1, 10), CancellationToken.None);
        Assert.Equal(1, res.TotalCount);
        Assert.Single(res.Items);
    }

    [Fact]
    public async Task PcTracker_QueryCompleteDetailAsync_Interpreted_ReturnsRecords()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(10), 120));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.QueryCompleteDetailAsync(new DetailQueryParams(TestDate.ToString("yyyy-MM-dd"), TestDate.ToString("yyyy-MM-dd"), null, null, null, null, null, null, "date", "asc", 1, 200, View: "interpreted"), CancellationToken.None);
        Assert.NotNull(res);
        Assert.True(res.TotalCount >= 1);
        Assert.All(res.Items, r => Assert.False(string.IsNullOrWhiteSpace(r.RecordType)));
    }

    [Fact]
    public async Task PcTracker_GetHeatmapGridAsync_Day_ReturnsGrid()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate, 2000);
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(1), 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(TestDate, TestDate, "hour", CancellationToken.None);
        Assert.NotNull(res);
        Assert.Single(res.Grid);
        Assert.Equal(24, res.Grid[0].Count);
    }

    // === PcActivityAggregationService ===

    [Fact]
    public async Task PcAggregation_GetFocusBlocksAsync_Empty_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.NotNull(res);
        Assert.Empty(res.Items);
    }

    [Fact]
    public async Task PcAggregation_GetFocusBlocksAsync_SingleBlock_MeetsMinDuration()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(9), 600));
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(9).AddMinutes(11), 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.NotNull(res);
        Assert.All(res.Items, b => Assert.True(b.DurationMinutes >= 10));
    }

    [Fact]
    public async Task PcAggregation_GetAppUsageAsync_WithEvents_ReturnsRanking()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(8), 600));
        db.Set<AwEventEntity>().Add(MakeWindow("chrome.exe", DayStart.AddHours(9), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), null, CancellationToken.None);
        Assert.NotNull(res);
        Assert.True(res.TotalMinutes > 0);
        Assert.NotEmpty(res.Items);
        Assert.All(res.Items, i => Assert.True(i.Percentage >= 0 && i.Percentage <= 100));
    }

    [Fact]
    public async Task PcAggregation_GetLateNightAsync_WithNightEvent_Counts()
    {
        await using var db = ServiceTestBase.CreateDb();
        var late = DayStart.AddHours(20); // 00:00 next day in shanghai ~ 16:00Z? use 19:30 shanghai = 11:30Z
        // Business day: DayStart is 04:00 local. Late night is 23:30 local => DayStart+19.5h
        var lateNight = DayStart.AddHours(19).AddMinutes(45);
        db.Set<AwEventEntity>().Add(MakeWindow("game.exe", lateNight, 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetLateNightAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.NotNull(res);
        Assert.Single(res.Items);
        Assert.True(res.Items[0].Minutes >= 10);
    }

    [Fact]
    public async Task PcAggregation_GetCategoryDistributionAsync_WithSnapshot_ReturnsDistribution()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = "k1",
            RecordType = "window",
            DeviceId = "pc-1",
            StartedAt = DayStart.AddHours(10),
            EndedAt = DayStart.AddHours(10).AddMinutes(20),
            CategoryName = "工作",
            CategoryColor = "#10b981",
            Confidence = 0.9,
            Source = "rule",
            ClassifierVersion = "local-v1",
            ClassifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.NotEmpty(res.Items);
        Assert.True(Math.Abs(res.Items.Sum(i => i.Percentage) - 100) <= 1.0);
    }

    // === PcActivityAnalysisService ===

    [Fact]
    public async Task PcActivityAnalysis_GetDailyAnalysisAsync_Empty_ReturnsBlocks()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcActivityAnalysisService(db);
        var res = await svc.GetDailyAnalysisAsync(TestDate, 60, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(24, res.Blocks.Count);
        Assert.All(res.Blocks, b => Assert.InRange(b.IntensityScore, 0, 4));
    }

    [Fact]
    public async Task PcActivityAnalysis_GetDailyAnalysisAsync_WithRecords_IntensityInRange()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(10), 1800));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcActivityAnalysisService(db);
        var res = await svc.GetDailyAnalysisAsync(TestDate, 60, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Contains(res.Blocks, b => b.ActiveDurationSeconds > 0);
        Assert.All(res.Blocks, b => Assert.True(b.ActiveDurationSeconds >= 0));
    }

    [Fact]
    public async Task PcActivityAnalysis_GetDailyAnalysisAsync_InvalidBlock_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcActivityAnalysisService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetDailyAnalysisAsync(TestDate, 5, CancellationToken.None));
    }

    // === PcTrackerQualityService ===

    [Fact]
    public async Task PcQuality_GetQualityAsync_Empty_ReturnsComponents()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.NotNull(res);
        Assert.NotEmpty(res.Components);
        Assert.True(res.CheckedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task PcQuality_GetQualityAsync_WithEvents_ReportsStatus()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(MakeWindow("code.exe", DayStart.AddHours(10), 120));
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "pc-1",
            SampledAtUtc = DayStart.AddHours(10),
            StatsDate = TestDate.Date,
            StatsTimezoneOffsetMinutes = 480,
            KeyPresses = 100,
            LeftClicks = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCountsJson = "{}",
            AppStatsJson = "{}",
            RawJson = "{}",
        });
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "pc-1",
            SampledAtUtc = DayStart.AddHours(10).AddMinutes(1),
            StatsDate = TestDate.Date,
            StatsTimezoneOffsetMinutes = 480,
            KeyPresses = 110,
            LeftClicks = 12,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCountsJson = "{}",
            AppStatsJson = "{}",
            RawJson = "{}",
        });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Contains(res.Components, c => c.Key == "aw-events");
    }

    // === PcProductivityService ===

    [Fact]
    public async Task PcProductivity_GetDashboardAsync_Empty_ReturnsZeroScore()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(0, res.TodayScore);
        Assert.Equal(7, res.WeeklyTrend.Count);
    }

    [Fact]
    public async Task PcProductivity_GetDashboardAsync_WithClassifications_ProductiveRatio()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k2", RecordType = "window", DeviceId = "pc-1",
            StartedAt = new DateTimeOffset(day.AddHours(10), TimeSpan.Zero),
            EndedAt = new DateTimeOffset(day.AddHours(11), TimeSpan.Zero),
            CategoryName = "工作", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "local-v1", ClassifiedAt = DateTimeOffset.UtcNow,
        });
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k3", RecordType = "window", DeviceId = "pc-1",
            StartedAt = new DateTimeOffset(day.AddHours(14), TimeSpan.Zero),
            EndedAt = new DateTimeOffset(day.AddHours(14).AddMinutes(30), TimeSpan.Zero),
            CategoryName = "游戏", CategoryColor = "#ef4444", Confidence = 0.8, Source = "rule", ClassifierVersion = "local-v1", ClassifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);
        Assert.True(res.ProductiveHours > 0);
        Assert.True(res.DistractingHours > 0);
        Assert.InRange(res.TodayScore, 0, 100);
    }

    [Fact]
    public async Task PcProductivity_GetTimelineV2Async_WithClassifications_ReturnsItems()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k4", RecordType = "window", DeviceId = "pc-1",
            StartedAt = new DateTimeOffset(day.AddHours(9), TimeSpan.Zero),
            EndedAt = new DateTimeOffset(day.AddHours(9).AddMinutes(15), TimeSpan.Zero),
            CategoryName = "工作", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "local-v1", ClassifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);
        Assert.Single(res);
        Assert.Equal("productive", res[0].Productivity);
    }

    // === ActivityLabelingService ===

    [Fact]
    public async Task ActivityLabeling_BuildQueueAsync_Empty_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        var res = await svc.BuildQueueAsync(10, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Empty(res.Items);
    }

    [Fact]
    public async Task ActivityLabeling_BuildQueueAsync_WithAppEvents_ReturnsCandidate()
    {
        await using var db = ServiceTestBase.CreateDb();
        // Seed 20 minutes for code app (>=10min threshold)
        for (int i = 0; i < 4; i++)
            db.Set<AwEventEntity>().Add(MakeWindow("unknown_app.exe", DayStart.AddHours(8).AddMinutes(i * 5), 300, "unknown_app"));
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        var res = await svc.BuildQueueAsync(10, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Contains(res.Items, x => x.Target == "unknown_app");
    }
}
