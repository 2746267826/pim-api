using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileLocationServiceTests
{
    [Fact]
    public async Task SubmitAsync_AcceptsAccuracyUnderFiftyMetersAsUsable()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var point = await service.SubmitAsync(Request(49.9), CancellationToken.None);

        Assert.Equal("usable", point.Quality);
        Assert.Equal(1, await db.Set<MobileLocationPointEntity>().CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_RejectsFiftyMeterAccuracy()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(Request(50), CancellationToken.None));

        Assert.Equal(6202, error.ErrorCode);
    }

    [Fact]
    public async Task SubmitAsync_RejectsAccuracyGreaterThanFiftyMeters()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(Request(50.01), CancellationToken.None));

        Assert.Equal(6202, error.ErrorCode);
        var rejected = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal("rejected", rejected.Quality);
        Assert.Equal(50.01m, rejected.HorizontalAccuracyMeters);
    }

    [Fact]
    public async Task SubmitAsync_RejectsInvalidCoordinates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var request = Request(10) with { Latitude = 91 };
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(6201, error.ErrorCode);
    }

    [Fact]
    public async Task SubmitAsync_AcceptsNullAltitudeWithQualityFlagInRawJson()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        const string rawJson = "{\"qualityFlags\":[\"altitude-missing-timeout\"]}";

        var point = await service.SubmitAsync(
            Request(18) with
            {
                AltitudeMeters = null,
                RawJson = rawJson
            },
            CancellationToken.None);

        Assert.Null(point.AltitudeMeters);
        Assert.Equal(rawJson, point.RawJson);
    }

    [Fact]
    public async Task GetHistoryAsync_ExcludesRejectedAndNonStrictAccuracyPoints()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var accepted = await service.SubmitAsync(Request(49.9), CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(() => service.SubmitAsync(Request(50), CancellationToken.None));
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            RecordedAtUtc = DateTimeOffset.Parse("2026-07-06T11:59:00Z"),
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = 50m,
            Provider = "gps",
            Source = "manual",
            RawJson = "{}",
            Quality = "usable",
            CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
        });
        await db.SaveChangesAsync();

        var history = await service.GetHistoryAsync(
            "android-main",
            DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T12:30:00Z"),
            50,
            CancellationToken.None);

        var point = Assert.Single(history);
        Assert.Equal(accepted.Id, point.Id);
        Assert.Equal("usable", point.Quality);
        Assert.True(point.HorizontalAccuracyMeters < 50);
    }

    private static MobileLocationPointRequest Request(double accuracy) => new(
        "android-main",
        DateTimeOffset.Parse("2026-07-06T11:58:00Z"),
        31.230416,
        121.473701,
        accuracy,
        "gps",
        "manual",
        4.2,
        6.0,
        1.1,
        0.5,
        90,
        1.5,
        false,
        "{\"provider\":\"gps\"}");
}
