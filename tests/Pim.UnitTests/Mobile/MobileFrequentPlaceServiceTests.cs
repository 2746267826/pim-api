using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileFrequentPlaceServiceTests
{
    // 30m 级纬度差 ≈ 0.0003°；离群噪声点用 >0.002° 差异（>220m）。
    private static readonly double[] Jitter = [0.0002, 0.0002, 0.0001];

    [Fact]
    public async Task ThreeTightClustersAndNoise_ProducesThreePlaces()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 12, "2026-07-07T04:00:00Z");
        SeedCluster(db, ref id, 31.240000, 121.480000, Jitter[1], 15, "2026-07-07T05:00:00Z");
        SeedCluster(db, ref id, 31.250000, 121.490000, Jitter[2], 10, "2026-07-07T06:00:00Z");
        SeedPoint(db, ref id, "2026-07-07T07:00:00Z", 31.280000, 121.520000, 12, "usable");
        SeedPoint(db, ref id, "2026-07-07T07:05:00Z", 31.282000, 121.518000, 12, "usable");
        SeedPoint(db, ref id, "2026-07-07T07:10:00Z", 31.278000, 121.522000, 12, "usable");
        SeedPoint(db, ref id, "2026-07-07T07:15:00Z", 31.281000, 121.521000, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var response = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);

        Assert.Equal(3, response.Places.Count);
        Assert.Equal(12, response.Places.Single(place => place.PointCount == 12).PointCount);
        Assert.Equal(15, response.Places.Single(place => place.PointCount == 15).PointCount);
        Assert.Equal(10, response.Places.Single(place => place.PointCount == 10).PointCount);
        Assert.All(response.Places, place => Assert.True(place.RadiusMeters > 0));
        Assert.True(
            Meters(31.230416, 121.473701, response.Places.Single(p => p.PointCount == 12).CenterLatitude, response.Places.Single(p => p.PointCount == 12).CenterLongitude) < 30);
        Assert.True(
            Meters(31.240000, 121.480000, response.Places.Single(p => p.PointCount == 15).CenterLatitude, response.Places.Single(p => p.PointCount == 15).CenterLongitude) < 30);
        Assert.True(
            Meters(31.250000, 121.490000, response.Places.Single(p => p.PointCount == 10).CenterLatitude, response.Places.Single(p => p.PointCount == 10).CenterLongitude) < 30);
    }

    [Fact]
    public async Task VisitDayCountCountsDistinctLocalDates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 6, "2026-07-07T04:00:00Z");
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 6, "2026-07-08T04:00:00Z");
        await db.SaveChangesAsync();
        var service = Service(db);

        var response = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);

        var place = Assert.Single(response.Places);
        Assert.Equal(12, place.PointCount);
        Assert.Equal(2, place.VisitDayCount);
    }

    [Fact]
    public async Task NightPointsPickHome()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        // A 组：12 点全部落在本地夜间（18:00Z = 本地次日 02:00）
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 12, "2026-07-07T18:00:00Z");
        // B 组：10 点全白天（04:00Z = 本地 12:00）
        SeedCluster(db, ref id, 31.240000, 121.480000, Jitter[1], 10, "2026-07-07T04:00:00Z");
        await db.SaveChangesAsync();
        var service = Service(db);

        var response = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);

        Assert.NotNull(response.Home);
        var home = response.Home!;
        Assert.True(home.IsHome);
        Assert.Equal(12, home.PointCount);
        Assert.Equal(1, response.Places.Count(place => place.IsHome));
        Assert.True(Meters(31.230416, 121.473701, home.CenterLatitude, home.CenterLongitude) < 30);
    }

    [Fact]
    public async Task NoClusterAboveMinPts_ReturnsEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 9, "2026-07-07T04:00:00Z");
        SeedPoint(db, ref id, "2026-07-07T05:00:00Z", 31.280000, 121.520000, 12, "usable");
        SeedPoint(db, ref id, "2026-07-07T05:05:00Z", 31.282000, 121.518000, 12, "usable");
        SeedPoint(db, ref id, "2026-07-07T05:10:00Z", 31.278000, 121.522000, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var response = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);

        Assert.Empty(response.Places);
        Assert.Null(response.Home);
    }

    [Fact]
    public async Task RejectedAndLowAccuracyPointsExcluded()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 12, "2026-07-07T04:00:00Z");
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 4, "2026-07-07T04:30:00Z", quality: "rejected");
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 4, "2026-07-07T05:00:00Z", accuracy: 150);
        await db.SaveChangesAsync();
        var service = Service(db);

        var response = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);

        var place = Assert.Single(response.Places);
        Assert.Equal(12, place.PointCount);
    }

    [Fact]
    public async Task CrossDevicePointsNotMerged()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 12, "2026-07-07T04:00:00Z", deviceId: "pixel-8");
        SeedCluster(db, ref id, 31.240000, 121.480000, Jitter[1], 12, "2026-07-07T05:00:00Z", deviceId: "pixel-9");
        await db.SaveChangesAsync();
        var service = Service(db);

        var filtered = await service.GetFrequentPlacesAsync(Query(deviceId: "pixel-8"), CancellationToken.None);
        var place = Assert.Single(filtered.Places);
        Assert.Equal(12, place.PointCount);

        var unfiltered = await service.GetFrequentPlacesAsync(Query(), CancellationToken.None);
        Assert.Equal(2, unfiltered.Places.Count);
    }

    [Fact]
    public async Task MovementStats_CountsOutingWhenLeavingHomeBeyondRadius()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedHome(db, ref id);
        // 走到 300m 外（0.0027° ≈ 300.6m > 150m），03:00-03:30 每 5 分钟一点，再回家
        SeedAway(db, ref id, "2026-07-08T03:00:00Z", 7);
        SeedPoint(db, ref id, "2026-07-08T03:35:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, ref id, "2026-07-08T03:40:00Z", 31.230416, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.NotNull(stats.HomeCenter);
        Assert.Equal(1, stats.OutingCount);
        Assert.Equal(1800, stats.OutingSeconds);
        var outing = Assert.Single(stats.Outings);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T03:00:00Z"), outing.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T03:30:00Z"), outing.EndUtc);
        Assert.Equal(1800, outing.Seconds);
    }

    [Fact]
    public async Task OutingTotalsNotTruncatedByDetailLimit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        // 家簇给足夜间点（三次 SeedHome = 36 个 02:00 本地点）：
        // 离家点簇跨度覆盖一个夜间窗（约 36 个夜间点），不给足会被点数决胜翻盘
        SeedHome(db, ref id);
        SeedHome(db, ref id);
        SeedHome(db, ref id);
        // 55 次出门：每次离家 10 分钟（3 点，间隔 5min）+ 回家收口；循环 25 分钟
        // （下一轮首点距上一轮末点 15 分钟 > 10 分钟桥接阈值，不会被合并）
        var baseTime = DateTimeOffset.Parse("2026-07-07T00:10:00Z");
        for (var outingIndex = 0; outingIndex < 55; outingIndex++)
        {
            var start = baseTime.AddMinutes(outingIndex * 25);
            for (var pointIndex = 0; pointIndex < 3; pointIndex++)
            {
                SeedPoint(db, ref id, start.AddMinutes(5 * pointIndex).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), 31.233116, 121.473701, 12, "usable");
            }
            SeedPoint(db, ref id, start.AddMinutes(12).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), 31.230416, 121.473701, 12, "usable");
        }
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.Equal(55, stats.OutingCount);
        Assert.Equal(55 * 600, stats.OutingSeconds);
        Assert.Equal(50, stats.Outings.Count);
        Assert.Equal(55, stats.PerDay.Sum(day => day.OutingCount));
    }

    [Fact]
    public async Task ShortExcursionUnder10MinNotCounted()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedHome(db, ref id);
        SeedAway(db, ref id, "2026-07-08T03:00:00Z", 2); // 03:00-03:05，5 分钟
        SeedPoint(db, ref id, "2026-07-08T03:10:00Z", 31.230416, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.Equal(0, stats.OutingCount);
        Assert.Equal(0, stats.OutingSeconds);
        Assert.Empty(stats.Outings);
    }

    [Fact]
    public async Task OutingGapUnder10MinMerged()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedHome(db, ref id);
        SeedAway(db, ref id, "2026-07-08T03:00:00Z", 5);      // 03:00-03:20
        SeedPoint(db, ref id, "2026-07-08T03:24:00Z", 31.230416, 121.473701, 12, "usable"); // 回家 8 分钟
        SeedAway(db, ref id, "2026-07-08T03:28:00Z", 5);      // 03:28-03:48，间隔 8 分钟 <= 10 分钟
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.Equal(1, stats.OutingCount);
        Assert.Equal(2880, stats.OutingSeconds);
        var outing = Assert.Single(stats.Outings);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T03:00:00Z"), outing.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T03:48:00Z"), outing.EndUtc);
    }

    [Fact]
    public async Task DistanceSumsMoveSegmentsOnly()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedPoint(db, ref id, "2026-07-08T03:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, ref id, "2026-07-08T03:05:00Z", 31.230866, 121.473701, 12, "usable"); // stay ~50m/300s
        SeedPoint(db, ref id, "2026-07-08T03:10:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, ref id, "2026-07-08T03:12:00Z", 31.234016, 121.473701, 12, "usable"); // move ~400m
        SeedPoint(db, ref id, "2026-07-08T03:12:30Z", 31.280000, 121.473701, 12, "usable"); // jump
        SeedPoint(db, ref id, "2026-07-08T03:14:00Z", 31.234016, 121.473701, 12, "usable");
        SeedPoint(db, ref id, "2026-07-08T03:20:00Z", 31.234016, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.True(stats.DistanceMeters > 390 && stats.DistanceMeters < 410,
            $"move 段距离应 ≈ 400m（jump 剔除、stay 不计），实际 {stats.DistanceMeters}");
    }

    [Fact]
    public async Task SpeedPeakPrefersPointSpeedField()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedHome(db, ref id);
        SeedPoint(db, ref id, "2026-07-08T03:00:00Z", 31.230416, 121.473701, 12, "usable", speed: 4.2);
        SeedPoint(db, ref id, "2026-07-08T03:02:00Z", 31.234016, 121.473701, 12, "usable", speed: 2.1); // 段速 ~3.34
        await db.SaveChangesAsync();
        var stats = await StatsService(db).GetMovementStatsAsync(Query(), CancellationToken.None);
        Assert.Equal(4.2, stats.MaxSpeedMetersPerSecond);

        // 全空时退化为段速
        await using var db2 = MobileTestHelpers.CreateDb();
        id = 0;
        SeedPoint(db2, ref id, "2026-07-08T03:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db2, ref id, "2026-07-08T03:02:00Z", 31.234016, 121.473701, 12, "usable");
        await db2.SaveChangesAsync();
        var fallback = await StatsService(db2).GetMovementStatsAsync(Query(), CancellationToken.None);
        Assert.NotNull(fallback.MaxSpeedMetersPerSecond);
        Assert.True(fallback.MaxSpeedMetersPerSecond > 3.0 && fallback.MaxSpeedMetersPerSecond < 3.6,
            $"段速 ≈ 3.34，实际 {fallback.MaxSpeedMetersPerSecond}");
    }

    [Fact]
    public async Task NoHomeReturnsZeroOutings()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 9, "2026-07-07T18:00:00Z");
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.Null(stats.HomeCenter);
        Assert.Equal(0, stats.OutingCount);
        Assert.Empty(stats.Outings);
        Assert.True(stats.DistanceMeters >= 0);
        Assert.True(stats.MaxSpeedMetersPerSecond is null || stats.MaxSpeedMetersPerSecond >= 0);
    }

    [Fact]
    public async Task PerDaySplitsByLocalCalendarDay()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var id = 0;
        SeedHome(db, ref id);
        // 本地 07-08 的出门（11:00-11:30 local）
        SeedAway(db, ref id, "2026-07-08T03:00:00Z", 7);
        // 本地 07-09 的 move 段（04:00 local = 20:00Z 前一日）
        SeedPoint(db, ref id, "2026-07-08T20:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, ref id, "2026-07-08T20:02:00Z", 31.234016, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = StatsService(db);

        var stats = await service.GetMovementStatsAsync(Query(), CancellationToken.None);

        Assert.Equal(2, stats.PerDay.Count);
        var day8 = stats.PerDay.Single(day => day.Date == "2026-07-08");
        Assert.Equal(1, day8.OutingCount);
        Assert.Equal(1800, day8.OutingSeconds);
        Assert.Equal(0, day8.DistanceMeters);
        var day9 = stats.PerDay.Single(day => day.Date == "2026-07-09");
        Assert.Equal(0, day9.OutingCount);
        Assert.True(day9.DistanceMeters > 390 && day9.DistanceMeters < 410);
    }

    private static MobileLocationQueryRequest Query(string? deviceId = null) => new(
        RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
        RangeEndUtc: DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        Timezone: "Asia/Shanghai",
        DeviceId: deviceId);

    private static void SeedHome(PimDbContext db, ref int id)
        => SeedCluster(db, ref id, 31.230416, 121.473701, Jitter[0], 12, "2026-07-07T18:00:00Z");

    private static void SeedAway(PimDbContext db, ref int id, string firstTimeUtc, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var time = DateTimeOffset.Parse(firstTimeUtc).AddMinutes(5 * index);
            SeedPoint(db, ref id, time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), 31.233116, 121.473701, 12, "usable");
        }
    }

    private static MobileFrequentPlaceService Service(PimDbContext db)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-10T00:00:00Z"))));

    private static MobileMovementStatsService StatsService(PimDbContext db)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-10T00:00:00Z"))),
            new MobileLocationAggregationService(
                db,
                MobileTestHelpers.CurrentUser(),
                new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-10T00:00:00Z"))),
                MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-10T00:00:00Z"))),
            Service(db));

    private static void SeedCluster(
        PimDbContext db,
        ref int id,
        double lat,
        double lon,
        double jitterDeg,
        int count,
        string timeUtc,
        string deviceId = "pixel-8",
        string quality = "usable",
        double accuracy = 12)
    {
        (double Lat, double Lon)[] offsets =
        [
            (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1)
        ];
        for (var index = 0; index < count; index++)
        {
            var (latOffset, lonOffset) = offsets[index % offsets.Length];
            SeedPoint(db, ref id, timeUtc, lat + latOffset * jitterDeg, lon + lonOffset * jitterDeg, accuracy, quality, deviceId);
        }
    }

    private static void SeedPoint(
        PimDbContext db,
        ref int id,
        string recordedAt,
        double lat,
        double lon,
        double accuracy,
        string quality,
        string deviceId = "pixel-8",
        string source = "auto",
        string rawJson = "{}",
        double? speed = null)
    {
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            Id = new Guid($"00000000-0000-0000-0000-{(id++):D12}"),
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = DateTimeOffset.Parse(recordedAt),
            Latitude = Convert.ToDecimal(lat),
            Longitude = Convert.ToDecimal(lon),
            HorizontalAccuracyMeters = Convert.ToDecimal(accuracy),
            SpeedMetersPerSecond = speed is null ? null : Convert.ToDecimal(speed.Value),
            Provider = "gps",
            Source = source,
            Quality = quality,
            RawJson = rawJson,
            CreatedAt = DateTimeOffset.Parse(recordedAt),
        });
    }

    private static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6371000;
        var toRadians = Math.PI / 180;
        var dLat = (lat2 - lat1) * toRadians;
        var dLon = (lon2 - lon1) * toRadians;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * toRadians) * Math.Cos(lat2 * toRadians) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
