using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Operations;

public class SystemStatusServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static SystemStatusService CreateService(PimDbContext db, IBackgroundJobStatusService backgroundJobs)
        => new(db, backgroundJobs, new StubTimeProvider { UtcNowValue = FixedNow });

    [Fact]
    public async Task GetSummaryAsync_ReturnsUnknown_WhenDaemonHeartbeatIsMissing()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);
        var service = CreateService(db, new FakeBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Unknown, summary.Status);
        Assert.Equal("未知", summary.Label);
        Assert.Equal("系统状态未知。", summary.Message);
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
            ReceivedAt = FixedNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new NoopBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Unknown, summary.Status);
        Assert.Equal("未知", summary.Label);
        Assert.Equal("系统状态未知。", summary.Message);
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
            ReceivedAt = FixedNow.AddMinutes(-20)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal("有警告", summary.Label);
        Assert.Equal("部分系统需要关注。", summary.Message);
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
            ReceivedAt = FixedNow.AddMinutes(-20)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new NoopBackgroundJobStatusService());
        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal("有警告", summary.Label);
        Assert.Equal("部分系统需要关注。", summary.Message);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsDegradedComponents_WhenDatabaseQueriesFail()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new PimDbContext(options);
        var service = CreateService(db, new FakeBackgroundJobStatusService());
        await db.DisposeAsync();

        var detail = await service.GetDetailAsync();

        Assert.Equal(PimHealthStatus.Critical, detail.Summary.Status);
        Assert.Equal("一个或多个系统正在故障。", detail.Summary.Message);

        var database = Assert.Single(detail.Components, c => c.Key == "database");
        Assert.Equal(PimHealthStatus.Critical, database.Status);
        Assert.Equal("数据库不可用。", database.Message);

        var daemon = Assert.Single(detail.Components, c => c.Key == "windows-daemon");
        Assert.Equal(PimHealthStatus.Critical, daemon.Status);
        Assert.Equal("Windows 守护程序心跳状态不可用。", daemon.Message);
        Assert.Contains("error", daemon.Details.Keys);
    }

    [Fact]
    public async Task GetDetailAsync_PlannedOfflineDaemon_ReturnsHealthyWithPlannedState()
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
            ReceivedAt = FixedNow.AddHours(-3),
            PlannedOfflineAt = FixedNow.AddMinutes(-1),
            OfflineReason = "shutdown"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeBackgroundJobStatusService());
        var detail = await service.GetDetailAsync();

        var daemon = Assert.Single(detail.Components, c => c.Key == "windows-daemon");
        Assert.Equal(PimHealthStatus.Healthy, daemon.Status);
        Assert.Equal("已关机/已休眠（正常）。", daemon.Message);
        Assert.Equal("planned-offline", daemon.Details["daemonState"]);
        Assert.Equal("shutdown", daemon.Details["offlineReason"]);
        Assert.Equal(FixedNow.AddMinutes(-1).ToString("O"), daemon.Details["plannedOfflineAt"]);
    }

    [Fact]
    public async Task GetDetailAsync_FreshDaemon_ReportsOnlineState()
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
            ReceivedAt = FixedNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeBackgroundJobStatusService());
        var detail = await service.GetDetailAsync();

        var daemon = Assert.Single(detail.Components, c => c.Key == "windows-daemon");
        Assert.Equal(PimHealthStatus.Healthy, daemon.Status);
        Assert.Equal("online", daemon.Details["daemonState"]);
    }

    private sealed class FakeBackgroundJobStatusService : IBackgroundJobStatusService
    {
        public Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new BackgroundJobSummaryDto(PimHealthStatus.Healthy, 0, 0, 0, 0, DateTimeOffset.UtcNow, "后台任务正常。"));
    }
}