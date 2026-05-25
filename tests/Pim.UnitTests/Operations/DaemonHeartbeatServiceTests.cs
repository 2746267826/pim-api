using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
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
}
