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
    public async Task SubmitAsync_AcceptsFiftyMeterAccuracyAsUsable()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var point = await service.SubmitAsync(Request(50), CancellationToken.None);

        Assert.Equal("usable", point.Quality);
        Assert.Equal(1, await db.Set<MobileLocationPointEntity>().CountAsync());
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
        Assert.Equal(0, await db.Set<MobileLocationPointEntity>().CountAsync());
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
