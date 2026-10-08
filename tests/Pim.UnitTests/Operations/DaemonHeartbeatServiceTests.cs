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
    public async Task RecordPlannedOfflineAsync_KeepsClientReportedInstant_DoesNotRewriteIt()
    {
        // 复审 Important（#398）：声明时刻必须**原样存储**，不得改写成"最近一次心跳时刻"。
        // 改写会让 S6 拿一个被挪动的时点去匹配空档（可能落进另一段真实断档的 ±5 分钟宽限里把它涂绿），
        // 也会破坏 AC-4.5「planned_offline_at 仍等于休眠起点」。
        await using var db = CreateDb();
        var existing = new DaemonHeartbeatEntity { DeviceId = "PC-1", DaemonKind = "windows", ReceivedAt = FixedNow };
        db.DaemonHeartbeats.Add(existing);
        await db.SaveChangesAsync();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow.AddMinutes(-5)), CancellationToken.None);
        Assert.Equal(FixedNow.AddMinutes(-5), existing.PlannedOfflineAt);
        Assert.Equal(FixedNow, existing.ReceivedAt);   // 心跳时刻不受声明影响

        // 时钟偏慢的客户端：声明时刻早于最近心跳，"此刻是否计划内离线"由比较判断，不改写存储时刻。
        var lifecycle = DaemonLifecycleClassifier.Classify(existing, FixedNow);
        Assert.NotEqual("planned-offline", lifecycle.State);
    }

    [Fact]
    public async Task RecordPlannedOfflineAsync_LateRequestAfterWakeIsStillAccepted()
    {
        // WO-ISSUES-396-400-20261007 REQ-4 / #398：休眠钩子里发出、进程被系统冻结、唤醒后才送达的
        // "迟到声明"不得被丢弃。旧实现有一条 5 分钟陈旧窗口，把这种形态直接扔掉 —— 声明没了，
        // 整段休眠在 S6 里就变成「无声明空档」。
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));

        // 行不存在 → 迟到 6 分钟也要建行并写入。
        var created = await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow.AddMinutes(-6)), CancellationToken.None);
        Assert.NotNull(created);
        var row = await db.DaemonHeartbeats.SingleAsync();
        Assert.Equal(FixedNow.AddMinutes(-6), row.PlannedOfflineAt);
        Assert.Equal("suspend", row.OfflineReason);

        // 行已存在 → 同样要写入 planned 字段。
        var existing = await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow.AddHours(-2)), CancellationToken.None);
        Assert.NotNull(existing);
        row = await db.DaemonHeartbeats.SingleAsync();
        Assert.NotNull(row.PlannedOfflineAt);
        Assert.Equal("suspend", row.OfflineReason);
    }

    [Fact]
    public async Task RecordPlannedOfflineAsync_FreshRequestStillWorks()
    {
        // 新鲜 planned 请求（窗口内）→ 正常写入（回归）。
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var result = await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow.AddMinutes(-1)), CancellationToken.None);
        Assert.NotNull(result);
        var row = await db.DaemonHeartbeats.SingleAsync();
        Assert.Equal(FixedNow.AddMinutes(-1), row.PlannedOfflineAt);
        Assert.Equal("suspend", row.OfflineReason);
    }

    [Fact]
    public async Task UpsertAsync_KeepsPlannedOfflineAcrossRegularHeartbeats()
    {
        // WO-ISSUES-396-400-20261007 REQ-4 ② / AC-4.5：唤醒后（含首次心跳之后）声明必须仍能被判定读取到。
        // 旧实现每次普通心跳都把 planned_offline_at / offline_reason 置空，唤醒后第一跳就把声明吃掉。
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
        Assert.Equal(FixedNow.AddMinutes(-5), row.PlannedOfflineAt);
        Assert.Equal("suspend", row.OfflineReason);
        Assert.Equal(FixedNow, row.ReceivedAt);   // 心跳只刷新 received_at
    }

    [Fact]
    public async Task UpsertAsync_AfterWake_DeclarationIsReadableButDeviceIsNotPermanentlyOffline()
    {
        // 反面行为：保留声明不得把设备当成"永久离线"。
        // 生命周期分类器要求 planned_offline_at >= received_at 才算「计划内下线」，
        // 唤醒后 received_at 已经晚于声明时刻 → 必须回到在线/退化分支。
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow.AddHours(-3)));
        await service.RecordPlannedOfflineAsync(
            new PlannedOfflineRequest("PC-1", "windows", "suspend", FixedNow.AddHours(-3)), CancellationToken.None);
        var offlineRow = await db.DaemonHeartbeats.SingleAsync();
        Assert.Equal("planned-offline", DaemonLifecycleClassifier.Classify(offlineRow, FixedNow.AddHours(-3)).State);

        // 唤醒：3 小时后的一跳普通心跳
        var wake = new DaemonHeartbeatService(db, StubClock(FixedNow));
        await wake.UpsertAsync(HeartbeatRequest("PC-1"), CancellationToken.None);

        var row = await db.DaemonHeartbeats.SingleAsync();
        Assert.Equal(FixedNow.AddHours(-3), row.PlannedOfflineAt);          // 声明仍可读
        Assert.Equal("online", DaemonLifecycleClassifier.Classify(row, FixedNow).State); // 但不是永久离线
    }

    [Fact]
    public async Task UpsertAsync_WhenDuplicateRowsExist_HealsDuplicatesAndUpdatesLatestWithout500()
    {
        await using var db = CreateDb();
        // 模拟生产环境无唯一索引时并发插入产生的重复脏数据
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "PC-1",
            DaemonKind = "windows",
            Version = "1.0.0",
            ReceivedAt = FixedNow.AddMinutes(-30)
        });
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "PC-1",
            DaemonKind = "windows",
            Version = "1.0.1",
            ReceivedAt = FixedNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var req = new DaemonHeartbeatRequest(
            "PC-1",
            "windows",
            "1.0.2",
            "http://127.0.0.1:5858",
            FixedNow,
            FixedNow,
            null,
            0,
            DaemonSourceState.Available,
            DaemonSourceState.Available,
            false,
            "{}");

        // 在修复前，SingleOrDefaultAsync 会抛出 InvalidOperationException: Sequence contains more than one element
        var result = await service.UpsertAsync(req, CancellationToken.None);

        Assert.Equal("1.0.2", result.Version);
        var remaining = await db.DaemonHeartbeats.Where(d => d.DeviceId == "PC-1" && d.DaemonKind == "windows").ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("1.0.2", remaining[0].Version);
        Assert.Equal(FixedNow, remaining[0].ReceivedAt);
    }

    [Fact]
    public async Task UpsertAsync_WhenVersionOrUrlTooLong_TruncatesSafely()
    {
        await using var db = CreateDb();
        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var longVersion = new string('v', 100); // MaxLength is 64
        var longUrl = "http://example.com/" + new string('a', 600); // MaxLength is 512

        var req = new DaemonHeartbeatRequest(
            "PC-1",
            "windows",
            longVersion,
            longUrl,
            null,
            null,
            null,
            0,
            DaemonSourceState.Available,
            DaemonSourceState.Available,
            false,
            "{}");

        var result = await service.UpsertAsync(req, CancellationToken.None);

        Assert.Equal(64, result.Version.Length);
        Assert.Equal(512, result.ServerUrl.Length);
    }
    [Fact]
    public async Task ListAsync_ReturnsLatestPerDeviceAndKind()
    {
        await using var db = CreateDb();
        db.DaemonHeartbeats.AddRange(
            new DaemonHeartbeatEntity { DeviceId = "PC-1", DaemonKind = "windows", Version = "1.0.0", ReceivedAt = FixedNow.AddMinutes(-20) },
            new DaemonHeartbeatEntity { DeviceId = "PC-1", DaemonKind = "windows", Version = "1.0.1", ReceivedAt = FixedNow.AddMinutes(-5) },
            new DaemonHeartbeatEntity { DeviceId = "PC-2", DaemonKind = "windows", Version = "2.0.0", ReceivedAt = FixedNow.AddMinutes(-1) },
            new DaemonHeartbeatEntity { DeviceId = "MOBILE-1", DaemonKind = "android", Version = "1.0.0", ReceivedAt = FixedNow }
        );
        await db.SaveChangesAsync();

        var service = new DaemonHeartbeatService(db, StubClock(FixedNow));
        var list = await service.ListAsync();

        Assert.Equal(3, list.Count);
        var pc1 = Assert.Single(list, d => d.DeviceId == "PC-1" && d.DaemonKind == "windows");
        Assert.Equal("1.0.1", pc1.Version);
    }
}
