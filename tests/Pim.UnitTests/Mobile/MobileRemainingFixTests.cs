using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileRemainingFixTests
{
    // PIM-015 cross-source dedup
    [Fact]
    public async Task PIM015_CrossSourceDedup_SummaryOverlappingSessionIsSkipped()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T10:30:00Z");
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.tencent.mm",
            StartUtc = start,
            EndUtc = end,
            DurationMs = (long)(end - start).TotalMilliseconds,
            QualityFlagsJson = "[]",
            CreatedAt = start
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.tencent.mm",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T10:00:00Z"),
            WindowEndUtc = DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
            TotalTimeVisibleMs = 3600 * 1000L,
            SourceKind = "fallback",
            QualityFlagsJson = "[]",
            CreatedAt = start,
            UpdatedAt = end
        });
        // different package summary should not be deduped
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.example.other",
            WindowStartUtc = DateTimeOffset.Parse("2026-07-06T10:00:00Z"),
            WindowEndUtc = DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
            TotalTimeVisibleMs = 1800 * 1000L,
            SourceKind = "fallback",
            QualityFlagsJson = "[]",
            CreatedAt = start,
            UpdatedAt = end
        });
        await db.SaveChangesAsync();
        var service = CreateAggregationService(db, now);
        var overview = await service.GetOverviewAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T12:00:00Z")), CancellationToken.None);
        // session 1800 + other summary 1800 = 3600, overlapping same-package summary must be skipped
        Assert.Equal(3600, overview.TotalForegroundSeconds);
    }

    // PIM-016 bucket cap
    [Fact]
    public async Task PIM016_HeatmapBucketCapped()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        // two overlapping sessions same hour same category => raw 7200 > 3600 cap
        var s1Start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        var s1End = DateTimeOffset.Parse("2026-07-06T11:00:00Z");
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.tencent.mm",
            StartUtc = s1Start,
            EndUtc = s1End,
            DurationMs = 3600 * 1000L,
            QualityFlagsJson = "[]",
            CreatedAt = s1Start
        });
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.tencent.mobileqq",
            StartUtc = s1Start,
            EndUtc = s1End,
            DurationMs = 3600 * 1000L,
            QualityFlagsJson = "[]",
            CreatedAt = s1Start
        });
        await db.SaveChangesAsync();
        var service = CreateAggregationService(db, now);
        var heatmap = await service.GetHeatmapAsync(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-06T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T12:00:00Z")), CancellationToken.None);
        var bucket = heatmap.First(b => b.LocalHour == 18); // 10 UTC = 18 Asia/Shanghai
        Assert.True(bucket.ForegroundSeconds <= 3600, $"bucket capped to 3600 but was {bucket.ForegroundSeconds}");
        Assert.Contains("hour_overflow", bucket.QualityFlags);
    }

    // PIM-017 open session unified to RangeEnd
    [Fact]
    public async Task PIM017_OpenSession_TimelineAndAggregationUseRangeEnd()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        var rangeStart = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        var rangeEnd = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var sessionStart = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "phone-main",
            PackageName = "com.tencent.mm",
            StartUtc = sessionStart,
            EndUtc = null,
            DurationMs = null,
            QualityFlagsJson = "[]",
            CreatedAt = sessionStart
        });
        await db.SaveChangesAsync();
        var timeProvider = MobileTestHelpers.Time(now.AddHours(5));
        var aggregation = new MobileUsageAggregationService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileAnalyticsQueryService(timeProvider),
            new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), timeProvider),
            timeProvider);
        var overview = await aggregation.GetOverviewAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
        Assert.Equal(7200, overview.TotalForegroundSeconds); // 10:00 to 12:00 = 7200
        Assert.Contains("open_session", overview.Quality.QualityFlags);

        var timelineService = new MobileTimelineBlockService(db, MobileTestHelpers.CurrentUser(), timeProvider);
        var blocks = await timelineService.GetBlocksAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
        Assert.True(blocks.Items.Count > 0);
        var block = blocks.Items.First();
        Assert.Equal(rangeEnd, block.EndUtc);
        Assert.Contains("open_session", block.QualityFlags);
    }

    // PIM-018 idempotency with CollectedAtUtc and RawJson hash
    [Fact]
    public async Task PIM018_Idempotency_DifferentCollectedAtIsNotDuplicate()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(db, MobileTestHelpers.CurrentUser(), new MobileSessionInterpreter(db), MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        var eventTime = start.AddMinutes(5);
        var req1 = new MobileUsageEventsUploadRequest("android-main", "batch-1", start, end,
            [], [new MobileUsageEventDto("com.example.messages", "USER_INTERACTION", eventTime, "MainActivity", start.AddMinutes(6), "{\"event\":\"tap\"}")], []);
        var req2 = new MobileUsageEventsUploadRequest("android-main", "batch-2", start, end,
            [], [new MobileUsageEventDto("com.example.messages", "USER_INTERACTION", eventTime, "MainActivity", start.AddMinutes(10), "{\"event\":\"tap\"}")], []);
        var r1 = await service.IngestAsync(req1, CancellationToken.None);
        var r2 = await service.IngestAsync(req2, CancellationToken.None);
        Assert.Equal(1, r1.AcceptedCount);
        Assert.Equal(1, r2.AcceptedCount);
        Assert.DoesNotContain(r2.ItemResults, x => x.Outcome == "skipped");
        Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task PIM018_Idempotency_DifferentRawJsonIsNotDuplicate()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(db, MobileTestHelpers.CurrentUser(), new MobileSessionInterpreter(db), MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        var eventTime = start.AddMinutes(5);
        var collected = start.AddMinutes(6);
        var req1 = new MobileUsageEventsUploadRequest("android-main", "batch-raw-1", start, end,
            [], [new MobileUsageEventDto("com.example.messages", "USER_INTERACTION", eventTime, "MainActivity", collected, "{\"event\":\"tap1\"}")], []);
        var req2 = new MobileUsageEventsUploadRequest("android-main", "batch-raw-2", start, end,
            [], [new MobileUsageEventDto("com.example.messages", "USER_INTERACTION", eventTime, "MainActivity", collected, "{\"event\":\"tap2\"}")], []);
        var r1 = await service.IngestAsync(req1, CancellationToken.None);
        var r2 = await service.IngestAsync(req2, CancellationToken.None);
        Assert.Equal(1, r1.AcceptedCount);
        Assert.Equal(1, r2.AcceptedCount);
    }

    // PIM-019/020 validation
    [Fact]
    public async Task PIM019_020_InvalidDurationsAreRejected()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(db, MobileTestHelpers.CurrentUser(), new MobileSessionInterpreter(db), MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        // zero
        var zero = new MobileUsageSummaryDto("com.example.app", start, end, 0, end, "usage-stats-fallback", "{}");
        // exceeds window
        var exceedWindow = new MobileUsageSummaryDto("com.example.app", start, end, 7200 * 1000L, end, "usage-stats-fallback", "{}");
        // exceeds 8h
        var longWindowStart = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
        var longWindowEnd = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        var exceed8h = new MobileUsageSummaryDto("com.example.app", longWindowStart, longWindowEnd, 9L * 60 * 60 * 1000, longWindowEnd, "usage-stats-fallback", "{}");
        var req = new MobileUsageEventsUploadRequest("android-main", "batch-invalid-dur", start, end, [], [], [zero, exceedWindow, exceed8h]);
        var result = await service.IngestAsync(req, CancellationToken.None);
        Assert.Equal(3, result.RejectedCount);
        Assert.All(result.ItemResults, r => Assert.Equal("invalid-duration", r.Code));
    }

    // PIM-021 jump flag in overview qualityFlags
    [Fact]
    public async Task PIM021_JumpFlagInOverview()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // points forming a jump: far distance in short time (~ 1.5km in 30s => 50 m/s > 30)
        SeedLocation(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12);
        SeedLocation(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230820, 121.473701, 12);
        SeedLocation(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.240000, 121.490000, 12);
        SeedLocation(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.230820, 121.473800, 12);
        SeedLocation(db, "55555555-5555-5555-5555-555555555555", "2026-07-07T10:02:00Z", 31.231220, 121.473800, 12);
        await db.SaveChangesAsync();
        var service = new MobileLocationAggregationService(db, MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z"))),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z")));
        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.Contains("jump-point", overview.QualityFlags);
    }

    // PIM-022 accuracy threshold <50
    [Fact]
    public async Task PIM022_AccuracyThreshold_Exactly50IsNotUsable()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedLocation(db, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 50);
        SeedLocation(db, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "2026-07-07T10:05:00Z", 31.230500, 121.473800, 49.9);
        await db.SaveChangesAsync();
        var service = new MobileLocationAggregationService(db, MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z"))),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z")));
        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            DateTimeOffset.Parse("2026-07-07T00:00:00Z"), DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);
        Assert.Equal(1, overview.UsablePointCount);
        Assert.Equal(2, overview.PointCount);
        Assert.Contains("low-accuracy-cluster", overview.QualityFlags);
    }

    private static MobileUsageAggregationService CreateAggregationService(Pim.Infrastructure.Data.PimDbContext db, DateTimeOffset now)
    {
        var tp = MobileTestHelpers.Time(now);
        return new MobileUsageAggregationService(db, MobileTestHelpers.CurrentUser(), new MobileAnalyticsQueryService(tp), new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), tp), tp);
    }

    private static void SeedLocation(Pim.Infrastructure.Data.PimDbContext db, string id, string recordedAt, double lat, double lon, double acc)
    {
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            Id = Guid.Parse(id),
            UserId = MobileTestHelpers.UserId,
            DeviceId = "pixel-8",
            RecordedAtUtc = DateTimeOffset.Parse(recordedAt),
            Latitude = Convert.ToDecimal(lat),
            Longitude = Convert.ToDecimal(lon),
            HorizontalAccuracyMeters = Convert.ToDecimal(acc),
            Provider = "gps",
            Source = "auto",
            Quality = "usable",
            RawJson = "{}",
            CreatedAt = DateTimeOffset.Parse(recordedAt)
        });
    }
}
