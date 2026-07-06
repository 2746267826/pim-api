using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileDeviceServiceTests
{
    [Fact]
    public async Task RegisterAsync_UpsertsDeviceByUserAndDeviceId()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileDeviceService(db, MobileTestHelpers.CurrentUser());

        await service.RegisterAsync(Request(displayName: "Pixel One", model: "Pixel 8"), CancellationToken.None);
        var updated = await service.RegisterAsync(Request(displayName: "Pixel Renamed", model: "Pixel 8 Pro"), CancellationToken.None);

        var devices = await db.Set<MobileDeviceEntity>().ToListAsync();
        Assert.Single(devices);
        Assert.Equal("android-main", devices[0].DeviceId);
        Assert.Equal("Pixel Renamed", devices[0].DisplayName);
        Assert.Equal("Pixel 8 Pro", devices[0].Model);
        Assert.Equal(devices[0].Id, updated.Id);
    }

    private static MobileDeviceRegisterRequest Request(string displayName, string model) => new(
        "android-main",
        "hash-1",
        displayName,
        "Google",
        "google",
        model,
        "15",
        35,
        "1.0.0",
        "{\"profile\":\"test\"}");
}
