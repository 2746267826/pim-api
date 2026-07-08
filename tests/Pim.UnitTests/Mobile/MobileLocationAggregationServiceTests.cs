using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileLocationAggregationServiceTests
{
    [Fact]
    public async Task GetOverviewAsync_ReturnsAcceptedMetricInputs()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:20:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:40:00Z", 31.235000, 121.480000, 18, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T11:05:00Z", 31.240000, 121.490000, 44, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.Equal(3, overview.PointCount);
        Assert.Equal(3, overview.UsablePointCount);
        Assert.True(overview.DistanceMeters > 1000);
        Assert.True(overview.AverageAccuracyMeters > 0);
    }

    [Fact]
    public async Task GetTracksAsync_SplitsLongGapsAndReturnsMoveSegments()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:20:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:40:00Z", 31.235000, 121.480000, 18, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T15:20:00Z", 31.280000, 121.520000, 20, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var tracks = await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.True(tracks.Count >= 2);
        Assert.Contains(tracks.SelectMany(track => track.Segments), segment => segment.Kind == "move");
    }

    [Fact]
    public async Task GetTracksAsync_DoesNotConnectDifferentDevices()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:20:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:25:00Z", 39.904200, 116.407400, 16, "usable", "pixel-9");
        await db.SaveChangesAsync();
        var service = Service(db);

        var tracks = await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.Equal(new[] { "pixel-8", "pixel-9" }, tracks.Select(track => track.DeviceId).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.All(tracks, track => Assert.Single(track.Segments));
    }

    [Fact]
    public async Task GetOverviewAsync_DoesNotReportLargeGapAcrossDifferentDevices()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T02:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:30:00Z", 39.904200, 116.407400, 16, "usable", "pixel-9");
        await db.SaveChangesAsync();
        var service = Service(db);

        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.DoesNotContain("large-gap", overview.QualityFlags);
        Assert.Equal(0, overview.QualityIssueCount);
    }

    [Fact]
    public async Task GetOverviewAsync_DoesNotAddDistanceAcrossDifferentDevices()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T02:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T02:05:00Z", 39.904200, 116.407400, 16, "usable", "pixel-9");
        await db.SaveChangesAsync();
        var service = Service(db);

        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.Equal(0, overview.DistanceMeters);
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsStableUrlSafeSegmentIdsAcrossFilters()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T02:00:00Z", 31.230416, 121.473701, 12, "usable", "a-phone");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T03:00:00Z", 31.230416, 121.473701, 12, "usable", "z/pixel 8");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T03:10:00Z", 31.240000, 121.490000, 16, "usable", "z/pixel 8");
        await db.SaveChangesAsync();
        var service = Service(db);
        var query = new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        var unfilteredSegmentId = (await service.GetTracksAsync(query, CancellationToken.None))
            .Single(track => track.DeviceId == "z/pixel 8")
            .Segments
            .Single()
            .Id;
        var filteredSegmentId = (await service.GetTracksAsync(query with { DeviceId = "z/pixel 8" }, CancellationToken.None))
            .Single()
            .Segments
            .Single()
            .Id;

        Assert.Equal(filteredSegmentId, unfilteredSegmentId);
        Assert.Matches("^[A-Za-z0-9_-]+$", unfilteredSegmentId);
    }

    [Fact]
    public async Task GetTracksAsync_SplitsStayMoveStayWithinContinuousTrack()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T02:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T02:12:00Z", 31.230500, 121.473800, 12, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T02:25:00Z", 31.260000, 121.510000, 16, "usable");
        SeedPoint(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T02:40:00Z", 31.260100, 121.510100, 16, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        Assert.Equal(3, track.SegmentCount);
        Assert.Equal(new[] { "stay", "move", "stay" }, track.Segments.Select(segment => segment.Kind).ToArray());
    }

    private static MobileLocationAggregationService Service(PimDbContext db)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z"))),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z")));

    private static void SeedPoint(
        PimDbContext db,
        string id,
        string recordedAt,
        double lat,
        double lon,
        double accuracy,
        string quality,
        string deviceId = "pixel-8")
    {
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            Id = Guid.Parse(id),
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = DateTimeOffset.Parse(recordedAt),
            Latitude = Convert.ToDecimal(lat),
            Longitude = Convert.ToDecimal(lon),
            HorizontalAccuracyMeters = Convert.ToDecimal(accuracy),
            Provider = "gps",
            Source = "auto",
            Quality = quality,
            RawJson = "{}",
            CreatedAt = DateTimeOffset.Parse(recordedAt),
        });
    }
}
