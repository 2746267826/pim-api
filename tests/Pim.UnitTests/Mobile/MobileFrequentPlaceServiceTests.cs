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

    private static MobileLocationQueryRequest Query(string? deviceId = null) => new(
        RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
        RangeEndUtc: DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        Timezone: "Asia/Shanghai",
        DeviceId: deviceId);

    private static MobileFrequentPlaceService Service(PimDbContext db)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-10T00:00:00Z"))));

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
