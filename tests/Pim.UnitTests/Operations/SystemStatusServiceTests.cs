using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class SystemStatusServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_ReturnsUnknown_WhenDaemonHeartbeatIsMissing()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var service = new SystemStatusService(db, new FakeBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Unknown, summary.Status);
        Assert.Equal("未知", summary.Label);
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsUnknown_WhenOnlyNoopBackgroundStatusIsUnknown()
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
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new SystemStatusService(db, new NoopBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Unknown, summary.Status);
        Assert.Equal("未知", summary.Label);
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsWarning_WhenDaemonHeartbeatIsOld()
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
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
        });
        await db.SaveChangesAsync();

        var service = new SystemStatusService(db, new FakeBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal("有警告", summary.Label);
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsWarning_WhenDaemonHeartbeatIsOldAndBackgroundStatusIsUnknown()
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
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
        });
        await db.SaveChangesAsync();

        var service = new SystemStatusService(db, new NoopBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal("有警告", summary.Label);
    }

    private sealed class FakeBackgroundJobStatusService : IBackgroundJobStatusService
    {
        public Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new BackgroundJobSummaryDto(PimHealthStatus.Healthy, 0, 0, 0, 0, DateTimeOffset.UtcNow, "Background jobs healthy."));
    }
}
