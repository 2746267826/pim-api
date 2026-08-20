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
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:22:00Z", 31.235000, 121.480000, 18, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:25:00Z", 31.240000, 121.490000, 44, "usable");
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
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:22:00Z", 31.235000, 121.480000, 18, "usable");
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
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T02:12:00Z", 31.230800, 121.473701, 12, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T02:25:00Z", 31.260000, 121.510000, 16, "usable");
        SeedPoint(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T02:40:00Z", 31.260500, 121.510500, 16, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        Assert.Equal(3, track.SegmentCount);
        Assert.Equal(new[] { "stay", "move", "stay" }, track.Segments.Select(segment => segment.Kind).ToArray());
        Assert.All(track.Segments, segment => Assert.Equal(2, segment.PointCount));
        var distinctSegmentPointIds = track.Segments
            .SelectMany(segment => segment.Path)
            .Select(point => point.Id)
            .Distinct()
            .Count();
        Assert.True(
            distinctSegmentPointIds == 4,
            $"boundary points are shared between adjacent segments, never duplicated within one segment (found {distinctSegmentPointIds} distinct ids)");
    }

    [Fact]
    public async Task GetTracksAsync_AppendixARegression_StaticJitterGroupIsSingleStaySegment()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a1", "2026-08-13T07:01:14.214Z", 36.6499244, 116.9693454, 17.90, "usable", "pixel-8", "auto");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a2", "2026-08-13T07:01:14.214Z", 36.6499244, 116.9693454, 17.90, "usable", "pixel-8", "manual");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a3", "2026-08-13T07:07:25.119Z", 36.6504880, 116.9698120, 17.90, "usable", "pixel-8", "auto");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a4", "2026-08-13T07:07:43.243Z", 36.6503990, 116.9694860, 40.40, "usable", "pixel-8", "auto");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a5", "2026-08-13T07:07:43.243Z", 36.6503990, 116.9694860, 40.40, "usable", "pixel-8", "manual");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a6", "2026-08-13T07:09:09.489Z", 36.6501050, 116.9697910, 19.00, "usable", "pixel-8", "auto");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a7", "2026-08-13T07:09:09.489Z", 36.6501050, 116.9697910, 19.00, "usable", "pixel-8", "manual");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a8", "2026-08-13T07:09:20.282Z", 36.6502200, 116.9697420, 19.00, "usable", "pixel-8", "auto");
        SeedPoint(db, "00000000-0000-0000-0000-0000000000a9", "2026-08-13T07:09:20.282Z", 36.6502200, 116.9697420, 19.00, "usable", "pixel-8", "manual");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-08-13T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z")), CancellationToken.None));

        Assert.Single(track.Segments);
        var segment = track.Segments[0];
        Assert.Equal("stay", segment.Kind);
        Assert.Equal(9, segment.PointCount);
        Assert.Equal(0, segment.DistanceMeters);

        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-08-13T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-08-14T00:00:00Z")), CancellationToken.None);
        Assert.Equal(1, overview.StayCount);
        Assert.Equal(0, overview.DistanceMeters);
    }

    [Fact]
    public async Task GetTracksAsync_UniformWalkingPointsFormSingleMoveSegment()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230821, 121.473701, 12, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.231226, 121.473701, 12, "usable");
        SeedPoint(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.231631, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        Assert.Single(track.Segments);
        Assert.Equal("move", track.Segments[0].Kind);
        Assert.True(track.Segments[0].DistanceMeters > 100);
    }

    [Fact]
    public async Task GetTracksAsync_JumpPointIsFlaggedAndExcludedFromDistance()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230820, 121.473701, 12, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:00Z", 31.240000, 121.490000, 12, "usable");
        SeedPoint(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:01:30Z", 31.230820, 121.473800, 12, "usable");
        SeedPoint(db, "55555555-5555-5555-5555-555555555555", "2026-07-07T10:02:00Z", 31.231220, 121.473800, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        var segment = Assert.Single(track.Segments);
        Assert.Equal("move", segment.Kind);
        Assert.Contains("jump-point", segment.QualityFlags);
        Assert.True(segment.DistanceMeters < 120, $"distance must exclude jump pairs, was {segment.DistanceMeters}");

        var jumpPoint = segment.Path.Single(point => point.Id == "33333333-3333-3333-3333-333333333333");
        Assert.Contains("jump-point", jumpPoint.QualityFlags);
        var normalPoints = segment.Path.Where(point => point.Id != "33333333-3333-3333-3333-333333333333");
        Assert.All(normalPoints, point => Assert.DoesNotContain("jump-point", point.QualityFlags));
    }

    [Fact]
    public async Task GetTracksAsync_SinglePointSegmentIsStayWithSinglePointFlag()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        var segment = Assert.Single(track.Segments);
        Assert.Equal("stay", segment.Kind);
        Assert.Contains("single-point", segment.QualityFlags);
        Assert.Equal(0, segment.DistanceMeters);
    }

    [Fact]
    public async Task GetTracksAsync_AutoManualDuplicatePointsDoNotBreakSegmentation()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8", "auto");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8", "manual");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        var segment = Assert.Single(track.Segments);
        Assert.Equal("stay", segment.Kind);
        Assert.Equal(2, segment.PointCount);
    }

    [Fact]
    public async Task GetTracksAsync_FuzzyBandUsesMotionSignalFromRawJson()
    {
        await using var db = MobileTestHelpers.CreateDb();

        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8", "auto", "{\"motionSignal\":\"Still\"}");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:01:00Z", 31.230821, 121.473701, 12, "usable", "pixel-8", "auto", "{\"motionSignal\":\"Still\"}");
        await db.SaveChangesAsync();
        var stillTrack = Assert.Single(await Service(db).GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));
        Assert.Equal("stay", Assert.Single(stillTrack.Segments).Kind);

        await using var db2 = MobileTestHelpers.CreateDb();
        SeedPoint(db2, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable", "pixel-8", "auto", "{\"motionSignal\":\"Walking\"}");
        SeedPoint(db2, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:01:00Z", 31.230821, 121.473701, 12, "usable", "pixel-8", "auto", "{\"motionSignal\":\"Walking\"}");
        await db2.SaveChangesAsync();
        var walkingTrack = Assert.Single(await Service(db2).GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));
        Assert.Equal("move", Assert.Single(walkingTrack.Segments).Kind);
    }

    [Fact]
    public async Task GetTracksAsync_FuzzyBandCarriesPreviousEvidence()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 12, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:00:30Z", 31.230821, 121.473701, 12, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:01:30Z", 31.231226, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var track = Assert.Single(await service.GetTracksAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None));

        Assert.Single(track.Segments);
        Assert.Equal("move", track.Segments[0].Kind);
    }

    [Fact]
    public async Task GetOverviewAsync_DistanceCountsMoveSegmentsOnly()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, "11111111-1111-1111-1111-111111111111", "2026-07-07T10:00:00Z", 31.230416, 121.473701, 18, "usable");
        SeedPoint(db, "22222222-2222-2222-2222-222222222222", "2026-07-07T10:03:00Z", 31.230800, 121.473701, 18, "usable");
        SeedPoint(db, "33333333-3333-3333-3333-333333333333", "2026-07-07T10:04:00Z", 31.230821, 121.473701, 12, "usable");
        SeedPoint(db, "44444444-4444-4444-4444-444444444444", "2026-07-07T10:04:30Z", 31.231226, 121.473701, 12, "usable");
        await db.SaveChangesAsync();
        var service = Service(db);

        var overview = await service.GetOverviewAsync(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")), CancellationToken.None);

        Assert.True(overview.DistanceMeters < 100, $"stay jitter must not inflate distance, was {overview.DistanceMeters}");
        Assert.True(overview.DistanceMeters > 40, $"move distance must be counted, was {overview.DistanceMeters}");
        Assert.Equal(1, overview.StayCount);
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
        string deviceId = "pixel-8",
        string source = "auto",
        string rawJson = "{}")
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
            Source = source,
            Quality = quality,
            RawJson = rawJson,
            CreatedAt = DateTimeOffset.Parse(recordedAt),
        });
    }
}
