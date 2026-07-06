using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class DaemonHeartbeatServiceTests
{
    [Fact]
    public async Task UpsertAsync_ReplacesExistingDeviceHeartbeat()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var service = new DaemonHeartbeatService(db);

        await service.UpsertAsync(new DaemonHeartbeatRequest(
            "pc-main",
            "windows",
            "1.0.0",
            "http://127.0.0.1:5858",
            null,
            null,
            null,
            0,
            DaemonSourceState.Available,
            DaemonSourceState.Available,
            false,
            "{}"));

        await service.UpsertAsync(new DaemonHeartbeatRequest(
            "pc-main",
            "windows",
            "1.0.1",
            "http://127.0.0.1:5858",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            2,
            DaemonSourceState.Available,
            DaemonSourceState.Unavailable,
            false,
            "{\"note\":\"second\"}"));

        var latest = await service.GetLatestWindowsAsync();

        Assert.Equal(1, await db.DaemonHeartbeats.CountAsync());
        Assert.Equal("1.0.1", latest!.Version);
        Assert.Equal(DaemonSourceState.Unavailable, latest.KeyStatsState);
    }

    [Fact]
    public async Task UpsertAsync_RejectsInvalidStatusJson()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var service = new DaemonHeartbeatService(db);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.UpsertAsync(new DaemonHeartbeatRequest(
                "pc-main",
                "windows",
                "1.0.0",
                "http://127.0.0.1:5858",
                null,
                null,
                null,
                0,
                DaemonSourceState.Available,
                DaemonSourceState.Available,
                false,
                "{invalid")));

        Assert.Equal(3010, error.ErrorCode);
        Assert.Equal(0, await db.DaemonHeartbeats.CountAsync());
    }

    [Fact]
    public async Task UpsertAsync_KeepsAndroidAndWindowsHeartbeatsIndependentForSameDevice()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var service = new DaemonHeartbeatService(db);

        await service.UpsertAsync(new DaemonHeartbeatRequest(
            "shared-device",
            "windows",
            "win-1.0.0",
            "http://127.0.0.1:5858",
            null,
            null,
            null,
            0,
            DaemonSourceState.Available,
            DaemonSourceState.Available,
            false,
            "{\"platform\":\"windows\"}"));

        await service.UpsertAsync(new DaemonHeartbeatRequest(
            "shared-device",
            "android",
            "android-1.0.0",
            "http://127.0.0.1:5858",
            null,
            null,
            null,
            3,
            DaemonSourceState.Unknown,
            DaemonSourceState.Unknown,
            false,
            "{\"platform\":\"android\"}"));

        var rows = await db.DaemonHeartbeats
            .OrderBy(heartbeat => heartbeat.DaemonKind)
            .ToListAsync();
        var latestWindows = await service.GetLatestWindowsAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.DeviceId == "shared-device" && row.DaemonKind == "android");
        Assert.Contains(rows, row => row.DeviceId == "shared-device" && row.DaemonKind == "windows");
        Assert.Equal("win-1.0.0", latestWindows!.Version);
    }

    [Fact]
    public async Task GetLatestWindowsAsync_ToleratesMalformedPersistedSourceStates()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "pc-main",
            DaemonKind = "windows",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            ActivityWatchState = "available",
            KeyStatsState = "not-real",
            StatusJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new DaemonHeartbeatService(db);
        var latest = await service.GetLatestWindowsAsync();

        Assert.Equal(DaemonSourceState.Available, latest!.ActivityWatchState);
        Assert.Equal(DaemonSourceState.Unknown, latest.KeyStatsState);
    }
}
