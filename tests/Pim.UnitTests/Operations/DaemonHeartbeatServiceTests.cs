using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Operations;

public class DaemonHeartbeatServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static StubTimeProvider StubClock(DateTimeOffset now) => new() { UtcNowValue = now };

    private static DaemonHeartbeatRequest HeartbeatRequest(string deviceId) => new(
        deviceId,
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
        "{}");

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

    [Fact]
    public async Task RecordPlannedOfflineAsync_CreatesRowWhenMissing()
    {
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var result = await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "shutdown", FixedNow), CancellationToken.None);
        var row = await db.DaemonHeartbeats.SingleAsync();
        Assert.Equal(FixedNow, row.PlannedOfflineAt);
        Assert.Equal("shutdown", row.OfflineReason);
        // 新建行时 received_at 与 planned_offline_at 同源（注入时钟），保证 planned_offline_at >= received_at 恒成立。
        Assert.Equal(FixedNow, row.ReceivedAt);
        Assert.Equal(row.PlannedOfflineAt, row.ReceivedAt);
    }

    [Fact]
    public async Task RecordPlannedOfflineAsync_NewRow_ClassifiedAsPlannedOffline()
    {
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "shutdown", FixedNow), CancellationToken.None);
        var row = await db.DaemonHeartbeats.SingleAsync();
        var lifecycle = DaemonLifecycleClassifier.Classify(row, FixedNow);
        Assert.Equal("planned-offline", lifecycle.State);
        Assert.Equal(PimHealthStatus.Healthy, lifecycle.Status);
    }

    [Fact]
    public async Task RecordPlannedOfflineAsync_UpdatesExistingRowWithoutTouchingReceivedAt()
    {
        await using var db = CreateDb();
        var existing = new DaemonHeartbeatEntity { DeviceId = "PC-1", DaemonKind = "windows", ReceivedAt = FixedNow.AddMinutes(-30) };
        db.DaemonHeartbeats.Add(existing);
        await db.SaveChangesAsync();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var result = await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow), CancellationToken.None);
        Assert.Equal(FixedNow, existing.PlannedOfflineAt);
        Assert.Equal("suspend", existing.OfflineReason);
        Assert.Equal(FixedNow.AddMinutes(-30), existing.ReceivedAt);
    }

    [Fact]
    public async Task RecordPlannedOfflineAsync_ClampsClientClockBeforeServerReceivedAt()
    {
        // 客户端时钟早于服务端最近心跳：planned_at 钳制到 received_at，避免分类器判 stale。
        await using var db = CreateDb();
        var existing = new DaemonHeartbeatEntity { DeviceId = "PC-1", DaemonKind = "windows", ReceivedAt = FixedNow };
        db.DaemonHeartbeats.Add(existing);
        await db.SaveChangesAsync();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "shutdown", FixedNow.AddMinutes(-5)), CancellationToken.None);
        Assert.Equal(FixedNow, existing.PlannedOfflineAt);
        Assert.Equal(FixedNow, existing.ReceivedAt);
        var lifecycle = DaemonLifecycleClassifier.Classify(existing, FixedNow);
        Assert.Equal("planned-offline", lifecycle.State);
    }

    [Fact]
    public async Task UpsertAsync_ClearsPlannedOfflineOnRegularHeartbeat()
    {
        await using var db = CreateDb();
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "PC-1", DaemonKind = "windows",
            PlannedOfflineAt = FixedNow.AddMinutes(-5), OfflineReason = "suspend",
            ReceivedAt = FixedNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        await service.UpsertAsync(HeartbeatRequest("PC-1"), CancellationToken.None);
        var row = await db.DaemonHeartbeats.SingleAsync();
        Assert.Null(row.PlannedOfflineAt);
        Assert.Null(row.OfflineReason);
    }
}
