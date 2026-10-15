using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 设备作用域测试库：SQLite 内存库 + mobile 模块配置 + 三张此前不在范围内的设备作用域表
/// （<c>daemon_heartbeats</c> / <c>endpoint_statuses</c> / <c>endpoint_notification_actions</c>），
/// 并按 <c>PimDbContext</c> 的口径补上按用户隔离的全局查询过滤器。
///
/// <para>
/// 补隔离过滤器不是为了"更像生产"这么笼统的理由，而是因为本文件最关键的断言
/// （别的用户持有同名字符串 device_id 时不得被改动）在生产里恰好会被这条过滤器掩盖：
/// 过滤器生效时，实现里那句用来找"别的用户是否占用了这个字符串"的查询会把别的用户的行滤掉，
/// 于是恒判"独占"、污染照常发生，而"旧 device_id 是否为 0 行"的断言反而变绿。
/// 过滤器直接复用 <c>PimDbContext.ApplyUserIsolationFilters</c>（protected internal），
/// 不照抄一份以免口径漂移；并由下面 <c>Fixture_...</c> 用例自检它确实生效。
/// </para>
/// </summary>
internal sealed class DeviceScopeSqliteDbContext : PimDbContext
{
    public DeviceScopeSqliteDbContext(DbContextOptions<PimDbContext> options, ICurrentUserService? currentUser)
        : base(options, currentUser)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MobileDeviceEntity).Assembly);

        // 生产里的这两条唯一索引都在 PimDbContext.OnModelCreating 的 inline 配置里，
        // 本上下文按 mobile-only 方式建表，需要显式补上（唯一键冲突正是被测行为的一部分）。
        modelBuilder.Entity<DaemonHeartbeatEntity>(e =>
        {
            e.HasIndex(d => new { d.DeviceId, d.DaemonKind }).IsUnique();
        });
        modelBuilder.Entity<EndpointStatusEntity>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.DeviceId }).IsUnique();
        });

        ApplyUserIsolationFilters(modelBuilder);
    }
}

/// <summary>
/// 工单 02（设备合并/删除的数据范围修复）在 SQLite 上的行为断言。
///
/// 覆盖 C1（心跳 / 端点三张表纳入合并）、C3（删除范围与预览逐表一致）、
/// 以及 AC-1 最关键的跨用户防回归断言。
/// 真库上的唯一索引冲突消解与 AC-2 读路径一致性见 <c>DeviceMergeScopeRealDbTests</c>。
/// </summary>
public sealed class DeviceMergeScopeTests
{
    private const string TargetDeviceId = "android-scope-target";
    private const string SourceDeviceA = "android-scope-source-a";
    private const string SourceDeviceB = "android-scope-source-b";

    /// <summary>另一个用户持有的、与源设备同名的 device_id 字符串。</summary>
    private static readonly Guid OtherUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T02:00:00Z");

    // ---------------------------------------------------------------- C1 合并：daemon_heartbeats

    [Fact]
    public async Task MergeAsync_MovesTheSourceDaemonHeartbeatOntoTheTargetDevice()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "2026.09.504-source");
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 旧 device_id 不得再有残留行 —— 否则旧设备会继续从心跳通道冒出来。
        Assert.Equal(0, await db.Set<DaemonHeartbeatEntity>().CountAsync(h => h.DeviceId == SourceDeviceA));
        var moved = Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, moved.DeviceId);
        Assert.Equal("2026.09.504-source", moved.Version);
        Assert.Equal(Now.AddMinutes(-5), moved.ReceivedAt);
    }

    [Fact]
    public async Task MergeAsync_KeepsTheFreshestHeartbeatWhenTheTargetAlreadyHasOne()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedHeartbeat(db, TargetDeviceId, Now.AddHours(-2), version: "target-stale");
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "source-fresh");
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 唯一索引 (device_id, daemon_kind) 只允许一行：保留最近的一次心跳快照。
        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("source-fresh", row.Version);
        Assert.Equal(Now.AddMinutes(-5), row.ReceivedAt);
        Assert.Equal(0, await db.Set<DaemonHeartbeatEntity>().CountAsync(h => h.DeviceId == SourceDeviceA));
    }

    [Fact]
    public async Task MergeAsync_KeepsTheTargetsHeartbeatWhenItIsTheFreshest()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedHeartbeat(db, TargetDeviceId, Now.AddMinutes(-1), version: "target-fresh");
        SeedHeartbeat(db, SourceDeviceA, Now.AddHours(-3), version: "source-stale");
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("target-fresh", row.Version);
    }

    [Fact]
    public async Task MergeAsync_ResolvesMultipleSourceHeartbeatsDownToOneRow()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceB, Now.AddDays(-50));
        SeedHeartbeat(db, SourceDeviceA, Now.AddHours(-4), version: "a-oldest");
        SeedHeartbeat(db, SourceDeviceB, Now.AddMinutes(-30), version: "b-freshest");
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("b-freshest", row.Version);
    }

    /// <summary>
    /// 本文件最关键的跨用户断言依赖隔离过滤器真的生效，先把这个前提钉住：
    /// 过滤器生效时，当前用户查不到别的用户的同名设备行（漏写 <c>IgnoreQueryFilters()</c>
    /// 的冲突查询因此恒返回空、恒判"独占"）；忽略过滤器才看得见它。
    /// </summary>
    [Fact]
    public async Task Fixture_AppliesTheProductionUserIsolationFilterSoCrossUserAssertionsCanFail()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, OtherUserId, SourceDeviceA, Now.AddDays(-10));
        await db.SaveChangesAsync();

        // 当前用户只看得见自己的设备；忽略过滤器才看得见同名的那一台。
        Assert.Equal(0, await db.Set<MobileDeviceEntity>().CountAsync(d => d.DeviceId == SourceDeviceA));
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().IgnoreQueryFilters().CountAsync(d => d.DeviceId == SourceDeviceA));
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().CountAsync(d => d.DeviceId == TargetDeviceId));
    }

    /// <summary>
    /// AC-1 的跨用户防回归断言：另一个用户在 <c>mobile_devices</c> 里持有一模一样的
    /// device_id 字符串。直接按 device_id 改写心跳会把它的行搬走（跨用户数据污染），
    /// 而"旧 device_id 是否为 0 行"的断言完全抓不到 —— 本用例专门钉住这一点。
    ///
    /// <para>
    /// 注意心跳表的唯一索引是 <c>(device_id, daemon_kind)</c> 且**没有** user_id，
    /// 所以两个用户共享同一字符串时，全局只可能存在<b>一行</b>心跳 —— 它到底是谁的无法判定。
    /// 因此正确行为是"整行不动"：宁可让新设备少一条心跳，也不能把别人的活动心跳搬走或删掉。
    /// </para>
    /// </summary>
    [Fact]
    public async Task MergeAsync_LeavesAnotherUsersDataUntouchedWhenTheDeviceIdStringIsShared()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        // 本用户自己的、会被正常搬走的数据（证明合并确实发生了，用例不是空转）。
        SeedEvent(db, MobileTestHelpers.UserId, SourceDeviceA, "com.ours", Now.AddDays(-60));
        // 归属不明的那一行心跳。
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-3), version: "shared-ambiguous");

        // 另一个用户：同名 device_id 字符串 + 自己的移动数据。
        SeedDevice(db, OtherUserId, SourceDeviceA, Now.AddDays(-10));
        db.Set<MobileUsageEventEntity>().Add(Event(OtherUserId, SourceDeviceA, "com.other.user", Now.AddDays(-10)));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 1) 别的用户一行都没动过。
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().IgnoreQueryFilters()
            .CountAsync(d => d.UserId == OtherUserId && d.DeviceId == SourceDeviceA));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().IgnoreQueryFilters()
            .CountAsync(e => e.UserId == OtherUserId && e.DeviceId == SourceDeviceA));

        // 2) 归属不明的心跳行原样留在原 device_id 上，没有被搬到我们的目标设备。
        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SourceDeviceA, row.DeviceId);
        Assert.Equal("shared-ambiguous", row.Version);

        // 3) 合并本身照常生效（本用户的数据该搬的都搬了）。
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().CountAsync(e => e.DeviceId == TargetDeviceId && e.PackageName == "com.ours"));
        Assert.Equal(0, await db.Set<MobileDeviceEntity>().CountAsync(d => d.DeviceId == SourceDeviceA));
    }

    /// <summary>
    /// 多台源设备里只有一台的字符串被别的用户占用：归属判断必须**逐台**做 ——
    /// 独占的那台照常并入，共享的那台整行不动，预览数字也要如实反映这个差别，
    /// 否则"预览 == 实际"在混合样本下就不成立。
    /// </summary>
    [Fact]
    public async Task MergeAsync_ScopesHeartbeatsPerSourceDeviceWhenOnlyOneSourceIdIsShared()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceB, Now.AddDays(-50));
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "a-exclusive");
        SeedHeartbeat(db, SourceDeviceB, Now.AddMinutes(-3), version: "b-shared");
        SeedDevice(db, OtherUserId, SourceDeviceB, Now.AddDays(-10));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var preview = await service.PreviewMergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);
        // 预览只数"会动的那一台"的心跳：共享的那台不在范围内。
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.DaemonHeartbeats).RowCount);

        await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

        var rows = await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters().OrderBy(h => h.DeviceId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(SourceDeviceB, rows[0].DeviceId);
        Assert.Equal("b-shared", rows[0].Version);
        Assert.Equal(TargetDeviceId, rows[1].DeviceId);
        Assert.Equal("a-exclusive", rows[1].Version);
    }

    /// <summary>
    /// 目标 device_id 也被别的用户占用：那个 id 下的心跳行归属不明，因此**两头都不写** ——
    /// 既不能删（可能是别人的活动心跳），也不能把我们的源行改挂上去（别的用户会因此看到
    /// 我们的设备状态）。此时本用户源设备的心跳行是我们自己的数据、设备又即将删除，
    /// 必须清掉，否则就是工单要消灭的"旧设备残留"。
    /// </summary>
    [Fact]
    public async Task MergeAsync_DoesNotWriteToASharedTargetDeviceIdAndClearsTheOwnSourceHeartbeat()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "ours-newer");
        // 目标 device_id 也被另一个用户持有，且那个 id 下已有一行（归属不明）。
        SeedDevice(db, OtherUserId, TargetDeviceId, Now.AddDays(-10));
        SeedHeartbeat(db, TargetDeviceId, Now.AddHours(-4), version: "ambiguous-older");
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 归属不明的那一行原样保留（版本、时刻都没被我们的更新数据顶掉）。
        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("ambiguous-older", row.Version);
        Assert.Equal(Now.AddHours(-4), row.ReceivedAt);
        // 我们自己的源心跳被清掉（设备已删除，不留孤儿行）。
        Assert.Equal(0, await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters()
            .CountAsync(h => h.DeviceId == SourceDeviceA));
    }

    /// <summary>
    /// 目标 device_id 被别的用户占用、且该 id 下**还没有**心跳行：也不能把源行迁进去 ——
    /// 那会让别的用户在自己的设备列表里看到我们的设备状态。源行必须清掉。
    /// </summary>
    [Fact]
    public async Task MergeAsync_DoesNotCreateAHeartbeatUnderASharedTargetDeviceId()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "ours");
        SeedDevice(db, OtherUserId, TargetDeviceId, Now.AddDays(-10));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Empty(await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters().ToListAsync());
    }

    /// <summary>
    /// 端点行还没收到过心跳（<c>LastHeartbeatAt == null</c>，由
    /// <c>EndpointStatusService.GetOrCreateStateAsync</c> 建出）时，源侧的有效心跳必须胜出。
    /// C# 的可空比较在有 null 时一律返回 false，不显式处理就会把源状态整份丢掉。
    /// </summary>
    [Fact]
    public async Task MergeAsync_LetsAValidSourceHeartbeatWinOverTheTargetsNullHeartbeat()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEndpointStatus(db, TargetDeviceId, Now.AddDays(-2), platform: "android", blocked: 1, uploadStatus: "Unknown");
        await db.SaveChangesAsync();
        var target = await db.Set<EndpointStatusEntity>().SingleAsync();
        target.LastHeartbeatAt = null;
        target.CollectionCacheCount = 0;
        SeedEndpointStatus(db, SourceDeviceA, Now.AddMinutes(-2), platform: "android", blocked: 4, uploadStatus: "Healthy", cacheCount: 7);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<EndpointStatusEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("Healthy", row.UploadStatus);
        Assert.Equal(7, row.CollectionCacheCount);
        Assert.Equal(Now.AddMinutes(-2), row.LastHeartbeatAt);
        Assert.Equal(5, row.OnlineOnlyBlockedCount); // 累计量相加
    }

    /// <summary>
    /// 反向：目标有有效心跳、源没有（<c>null</c>）。目标状态与时刻保持不变，源侧的累计量仍然并入。
    /// </summary>
    [Fact]
    public async Task MergeAsync_KeepsTheTargetsValidHeartbeatWhenTheSourceHasNone()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEndpointStatus(db, TargetDeviceId, Now.AddMinutes(-2), platform: "android", blocked: 1, uploadStatus: "Healthy", cacheCount: 7);
        SeedEndpointStatus(db, SourceDeviceA, Now.AddDays(-3), platform: "android", blocked: 4, uploadStatus: "Unknown");
        await db.SaveChangesAsync();
        var source = await db.Set<EndpointStatusEntity>().SingleAsync(s => s.DeviceId == SourceDeviceA);
        source.LastHeartbeatAt = null;
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<EndpointStatusEntity>().ToListAsync());
        Assert.Equal("Healthy", row.UploadStatus);
        Assert.Equal(7, row.CollectionCacheCount);
        Assert.Equal(Now.AddMinutes(-2), row.LastHeartbeatAt);
        Assert.Equal(5, row.OnlineOnlyBlockedCount);
    }

    /// <summary>双方都没有心跳时刻（都是 null）时：不因 null 比较作出任何状态替换，计数仍相加。</summary>
    [Fact]
    public async Task MergeAsync_HandlesTwoEndpointRowsThatBothLackAHeartbeat()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEndpointStatus(db, TargetDeviceId, Now.AddDays(-2), platform: "android", blocked: 1, uploadStatus: "TargetOnly", cacheCount: 1);
        SeedEndpointStatus(db, SourceDeviceA, Now.AddDays(-2), platform: "android", blocked: 4, uploadStatus: "SourceOnly", cacheCount: 2);
        await db.SaveChangesAsync();
        foreach (var state in await db.Set<EndpointStatusEntity>().ToListAsync()) state.LastHeartbeatAt = null;
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<EndpointStatusEntity>().ToListAsync());
        Assert.Null(row.LastHeartbeatAt);
        Assert.Equal("TargetOnly", row.UploadStatus);
        Assert.Equal(5, row.OnlineOnlyBlockedCount);
    }

    // ---------------------------------------------------------------- C1 合并：endpoint_statuses

    [Fact]
    public async Task MergeAsync_MovesTheEndpointStatusOntoTheTargetDevice()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEndpointStatus(db, SourceDeviceA, Now.AddMinutes(-4), platform: "android", blocked: 3);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Equal(0, await db.Set<EndpointStatusEntity>().CountAsync(s => s.DeviceId == SourceDeviceA));
        var row = Assert.Single(await db.Set<EndpointStatusEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal(3, row.OnlineOnlyBlockedCount);
    }

    [Fact]
    public async Task MergeAsync_ResolvesTheEndpointStatusUniqueKeyByKeepingTheFreshestAndSummingCounters()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEndpointStatus(db, TargetDeviceId, Now.AddHours(-2), platform: "android", blocked: 5, uploadStatus: "Stale", cacheCount: 9);
        SeedEndpointStatus(db, SourceDeviceA, Now.AddMinutes(-2), platform: "android", blocked: 7, uploadStatus: "Healthy", cacheCount: 2);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 唯一键 (user_id, device_id)：必须消解成一行，否则整体改写会撞唯一索引。
        var row = Assert.Single(await db.Set<EndpointStatusEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("Healthy", row.UploadStatus);
        Assert.Equal(2, row.CollectionCacheCount);
        Assert.Equal(Now.AddMinutes(-2), row.LastHeartbeatAt);
        // 累计量相加：两台端点各自被拦截的次数在合并后应当是总和。
        Assert.Equal(12, row.OnlineOnlyBlockedCount);
    }

    [Fact]
    public async Task MergeAsync_MovesEndpointNotificationActionsOntoTheTargetDevice()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        db.Set<EndpointNotificationActionEntity>().AddRange(
            NotificationAction(SourceDeviceA, "open-settings"),
            NotificationAction(SourceDeviceA, "snooze"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Equal(0, await db.Set<EndpointNotificationActionEntity>().CountAsync(a => a.DeviceId == SourceDeviceA));
        Assert.Equal(2, await db.Set<EndpointNotificationActionEntity>().CountAsync(a => a.DeviceId == TargetDeviceId));
    }

    /// <summary>
    /// <c>mobile_location_points</c> 的天然键是 (user, device, recorded_at, lat, lon) 且带唯一索引
    /// （#246）。旧注释声称它"没有唯一索引"，于是重装后重传同一批点的源设备在合并时必然撞索引
    /// 报 23505（生产表现仍是 HTTP 500）。本用例钉住"消解后照常合并、且不丢点"。
    /// </summary>
    [Fact]
    public async Task MergeAsync_DoesNotCollideOnLocationPointsThatShareTheNaturalKey()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        var sharedMoment = Now.AddDays(-55);
        SeedLocation(db, TargetDeviceId, sharedMoment, 31.2304m, 121.4737m);
        SeedLocation(db, SourceDeviceA, sharedMoment, 31.2304m, 121.4737m);
        SeedLocation(db, SourceDeviceA, sharedMoment.AddMinutes(5), 31.2404m, 121.4837m);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Equal(0, await db.Set<MobileLocationPointEntity>().CountAsync(p => p.DeviceId == SourceDeviceA));
        // 重复的那一个被去除，独特的那一个必须留下。
        Assert.Equal(2, await db.Set<MobileLocationPointEntity>().CountAsync(p => p.DeviceId == TargetDeviceId));
    }

    // ---------------------------------------------------------------- C3 删除：范围与预览

    [Fact]
    public async Task DeleteAsync_RemovesEveryDeviceScopedRowIncludingHeartbeatsAndEndpoints()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        // 15 张表全部种上数据，否则 ResidualTablesAsync 的空断言可能是"因为根本没数据"而恒真。
        SeedEvent(db, MobileTestHelpers.UserId, TargetDeviceId, "com.a", Now.AddDays(-1));
        SeedSession(db, MobileTestHelpers.UserId, TargetDeviceId, "com.a", Now.AddDays(-1));
        SeedSummary(db, MobileTestHelpers.UserId, TargetDeviceId, "com.a", Now.AddDays(-1));
        SeedLocation(db, TargetDeviceId, Now.AddDays(-1), 31.23m, 121.47m);
        SeedBatch(db, MobileTestHelpers.UserId, TargetDeviceId, "batch-1", Now.AddDays(-1));
        SeedForensic(db, MobileTestHelpers.UserId, TargetDeviceId, "key-1", Now.AddDays(-1));
        SeedDroppedReason(db, MobileTestHelpers.UserId, TargetDeviceId, "2026-09-12", "low-accuracy");
        SeedCatalog(db, MobileTestHelpers.UserId, TargetDeviceId, "com.a", "A", "tools", Now.AddDays(-1));
        SeedTimelineBlock(db, MobileTestHelpers.UserId, TargetDeviceId, Now.AddDays(-1));
        SeedAggregate(db, MobileTestHelpers.UserId, TargetDeviceId, Now.AddDays(-1));
        SeedMaterialization(db, MobileTestHelpers.UserId, TargetDeviceId, Now.AddDays(-1));
        SeedHeartbeat(db, TargetDeviceId, Now.AddMinutes(-1), version: "bye");
        SeedEndpointStatus(db, TargetDeviceId, Now.AddMinutes(-1), platform: "android", blocked: 1);
        db.Set<EndpointNotificationActionEntity>().Add(NotificationAction(TargetDeviceId, "open-settings"));
        await db.SaveChangesAsync();

        // 删除前这张清单必须是满的（15 张表里除了 mobile_devices 之外都应有数据）。
        var before = await ResidualTablesAsync(db, MobileTestHelpers.UserId, TargetDeviceId);
        Assert.Equal(15, before.Count);

        var service = CreateService(db);
        var result = await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        // 删除后不得留下"只有心跳没有设备"这类悬空记录。
        Assert.Empty(await ResidualTablesAsync(db, MobileTestHelpers.UserId, TargetDeviceId));
        var byTable = result.Tables.ToDictionary(t => t.Table, t => t.RowCount, StringComparer.Ordinal);
        foreach (var table in before)
            Assert.Equal(1, byTable[table]);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotDeleteAnotherUsersHeartbeatForTheSameDeviceIdString()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedEvent(db, MobileTestHelpers.UserId, TargetDeviceId, "com.ours", Now.AddMinutes(-1));
        // 唯一索引 (device_id, daemon_kind) 没有 user_id，两个用户同名时全局只有这一行。
        SeedHeartbeat(db, TargetDeviceId, Now.AddMinutes(-2), version: "shared-ambiguous");
        SeedDevice(db, OtherUserId, TargetDeviceId, Now.AddDays(-10));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        // 本用户的数据该删的删干净了……
        Assert.Equal(0, await db.Set<MobileUsageEventEntity>().CountAsync(e => e.DeviceId == TargetDeviceId));
        Assert.Equal(0, await db.Set<MobileDeviceEntity>().CountAsync(d => d.DeviceId == TargetDeviceId));
        // ……但归属不明的心跳行不能删（删了就动了别的用户的活动心跳），预览/结果也如实报 0。
        Assert.Equal(0, result.Tables.Single(t => t.Table == DeviceScopedTables.DaemonHeartbeats).RowCount);
        var row = Assert.Single(await db.Set<DaemonHeartbeatEntity>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().IgnoreQueryFilters().CountAsync(d => d.UserId == OtherUserId));
    }

    [Fact]
    public async Task MergePreview_ReportsThePerTableScopeOfTheSourceDevices()
    {
        await using var ctx = await DeviceScopeTestDb.CreateAsync(MobileTestHelpers.UserId);
        var db = ctx.Db;
        SeedDevice(db, MobileTestHelpers.UserId, TargetDeviceId, Now);
        SeedDevice(db, MobileTestHelpers.UserId, SourceDeviceA, Now.AddDays(-60));
        SeedEvent(db, MobileTestHelpers.UserId, SourceDeviceA, "com.a", Now.AddDays(-60));
        SeedEvent(db, MobileTestHelpers.UserId, SourceDeviceA, "com.b", Now.AddDays(-60));
        SeedHeartbeat(db, SourceDeviceA, Now.AddMinutes(-5), version: "source");
        SeedHeartbeat(db, TargetDeviceId, Now.AddMinutes(-1), version: "target");
        SeedEndpointStatus(db, SourceDeviceA, Now.AddMinutes(-5), platform: "android", blocked: 1);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var preview = await service.PreviewMergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 预览范围 = 源设备一侧（这些行会被改写/相加/删除）；目标设备的行不算"源数据"。
        Assert.Equal(2, preview.Tables.Single(t => t.Table == DeviceScopedTables.UsageEvents).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.DaemonHeartbeats).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.EndpointStatuses).RowCount);
        Assert.Equal(1, preview.Tables.Single(t => t.Table == DeviceScopedTables.Devices).RowCount);
        Assert.Equal(0, preview.Tables.Single(t => t.Table == DeviceScopedTables.UsageSessions).RowCount);
        Assert.Equal(2, preview.Total);
    }

    // ---------------------------------------------------------------- helpers

    private static DeviceManagementService CreateService(PimDbContext db)
        => new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    /// <summary>
    /// 扫描全部设备作用域表，返回仍然挂着该 device_id 的表名（AC-1 的"所有相关表 0 行"断言）。
    /// </summary>
    private static async Task<List<string>> ResidualTablesAsync(PimDbContext db, Guid userId, string deviceId)
    {
        var hits = new List<string>();
        if (await db.Set<MobileUsageEventEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.UsageEvents);
        if (await db.Set<MobileUsageSessionEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.UsageSessions);
        if (await db.Set<MobileUsageSummaryEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.UsageSummaries);
        if (await db.Set<MobileLocationPointEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.LocationPoints);
        if (await db.Set<MobileSyncBatchEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.SyncBatches);
        if (await db.Set<MobileForensicEventEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.ForensicEvents);
        if (await db.Set<MobileDroppedReasonDailyEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.DroppedReasonDaily);
        if (await db.Set<MobileAppCatalogEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.AppCatalog);
        if (await db.Set<MobileTimelineBlockEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.TimelineBlocks);
        if (await db.Set<MobileUsageAggregateEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.UsageAggregates);
        if (await db.Set<MobileAnalyticsMaterializationEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.AnalyticsMaterializations);
        if (await db.Set<EndpointStatusEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.EndpointStatuses);
        if (await db.Set<EndpointNotificationActionEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.EndpointNotificationActions);
        if (await db.Set<DaemonHeartbeatEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.DaemonHeartbeats);
        if (await db.Set<MobileDeviceEntity>().IgnoreQueryFilters().AnyAsync(e => e.UserId == userId && e.DeviceId == deviceId)) hits.Add(DeviceScopedTables.Devices);
        return hits;
    }

    /// <summary>
    /// 直接对库表计数（不经过 EF 查询管道）。<c>daemon_heartbeats</c> 没有 user_id，
    /// 因此按表名决定要不要带 user_id 条件 —— 这也是删除范围里那条例外的直接体现。
    /// </summary>
    private static async Task<int> RawCountAsync(SqliteConnection connection, string table, Guid userId, string deviceId)
    {
        var hasUserId = table != DeviceScopedTables.DaemonHeartbeats;
        var sql = hasUserId
            ? $"SELECT COUNT(*) FROM \"{table}\" WHERE user_id = @userId AND device_id = @deviceId"
            : $"SELECT COUNT(*) FROM \"{table}\" WHERE device_id = @deviceId";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@deviceId", deviceId);
        if (hasUserId) command.Parameters.AddWithValue("@userId", userId.ToString());
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static void SeedDevice(PimDbContext db, Guid userId, string deviceId, DateTimeOffset lastSeen)
        => db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            DeviceHash = $"hash-{userId}-{deviceId}",
            DisplayName = "OPPO PLG110",
            Brand = "OPPO",
            Model = "PLG110",
            OsVersion = "15",
            AppVersion = "2026.09.504",
            RegisteredAtUtc = lastSeen,
            LastSeenAtUtc = lastSeen,
        });

    private static void SeedHeartbeat(PimDbContext db, string deviceId, DateTimeOffset receivedAt, string version)
        => db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity
        {
            DeviceId = deviceId,
            DaemonKind = MobileDaemonKind.Android,
            Version = version,
            ServerUrl = "http://127.0.0.1:5858",
            StatusJson = "{}",
            ReceivedAt = receivedAt,
            ActivityWatchState = "running",
            KeyStatsState = "running",
        });

    private static void SeedEndpointStatus(
        PimDbContext db,
        string deviceId,
        DateTimeOffset lastHeartbeatAt,
        string platform,
        int blocked,
        string uploadStatus = "Healthy",
        int cacheCount = 0)
        => db.Set<EndpointStatusEntity>().Add(new EndpointStatusEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            Platform = platform,
            AppVersion = "2026.09.504",
            UploadStatus = uploadStatus,
            CollectionCacheCount = cacheCount,
            OnlineOnlyBlockedCount = blocked,
            LastHeartbeatAt = lastHeartbeatAt,
            CreatedAt = lastHeartbeatAt.AddDays(-1),
            UpdatedAt = lastHeartbeatAt,
        });

    private static EndpointNotificationActionEntity NotificationAction(string deviceId, string action)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            Action = action,
            RiskLevel = "Low",
            Result = "Executed",
            CreatedAt = Now.AddMinutes(-1),
        };

    private static MobileUsageEventEntity Event(Guid userId, string deviceId, string packageName, DateTimeOffset timestamp)
        => new()
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            EventType = "foreground",
            ClassName = string.Empty,
            EventTimestampUtc = timestamp,
            SourceWindowStartUtc = timestamp,
            SourceWindowEndUtc = timestamp.AddMinutes(1),
            CollectedAtUtc = timestamp.AddMinutes(1),
        };

    private static void SeedEvent(PimDbContext db, Guid userId, string deviceId, string packageName, DateTimeOffset timestamp)
        => db.Set<MobileUsageEventEntity>().Add(Event(userId, deviceId, packageName, timestamp));

    private static void SeedSession(PimDbContext db, Guid userId, string deviceId, string packageName, DateTimeOffset start)
        => db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            StartUtc = start,
            EndUtc = start.AddMinutes(5),
            DurationMs = 300_000,
        });

    private static void SeedSummary(PimDbContext db, Guid userId, string deviceId, string packageName, DateTimeOffset windowStart)
        => db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            TotalTimeVisibleMs = 300_000,
            LastTimeUsedUtc = windowStart.AddMinutes(15),
            SourceKind = "usage-summary",
        });

    private static void SeedLocation(PimDbContext db, string deviceId, DateTimeOffset recordedAt, decimal lat, decimal lon)
        => db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = lat,
            Longitude = lon,
            HorizontalAccuracyMeters = 12.5m,
            Provider = "fused",
            Source = "android",
        });

    private static void SeedBatch(PimDbContext db, Guid userId, string deviceId, string batchId, DateTimeOffset windowStart)
        => db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            BatchId = batchId,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            AcceptedCount = 1,
            Status = "completed",
            CompletedAtUtc = windowStart.AddMinutes(15),
        });

    private static void SeedForensic(PimDbContext db, Guid userId, string deviceId, string clientItemKey, DateTimeOffset occurredAt)
        => db.Set<MobileForensicEventEntity>().Add(new MobileForensicEventEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            EventType = "process-exit",
            ClientItemKey = clientItemKey,
            OccurredAtUtc = occurredAt,
            ReceivedAtUtc = occurredAt,
        });

    private static void SeedDroppedReason(PimDbContext db, Guid userId, string deviceId, string localDate, string reason)
        => db.Set<MobileDroppedReasonDailyEntity>().Add(new MobileDroppedReasonDailyEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            LocalDate = localDate,
            Reason = reason,
            Count = 2,
            ReceivedAtUtc = Now,
        });

    private static void SeedCatalog(
        PimDbContext db, Guid userId, string deviceId, string packageName, string displayName, string category, DateTimeOffset updatedAt)
        => db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            PackageName = packageName,
            DisplayName = displayName,
            Category = category,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        });

    private static void SeedTimelineBlock(PimDbContext db, Guid userId, string deviceId, DateTimeOffset start)
        => db.Set<MobileTimelineBlockEntity>().Add(new MobileTimelineBlockEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            StartUtc = start,
            EndUtc = start.AddMinutes(15),
            LocalDate = start.ToString("yyyy-MM-dd"),
            ForegroundSeconds = 300,
            SessionCount = 1,
            AppCount = 1,
        });

    private static void SeedAggregate(PimDbContext db, Guid userId, string deviceId, DateTimeOffset start)
        => db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            Granularity = MobileAnalyticsDefaults.HourGranularity,
            BucketStartUtc = start,
            BucketEndUtc = start.AddHours(1),
            PackageName = "com.a",
            DisplayName = "A",
            LifeCategory = MobileLifeCategories.Uncategorized,
            ForegroundSeconds = 60,
            CreatedAt = start,
            UpdatedAt = start,
        });

    private static void SeedMaterialization(PimDbContext db, Guid userId, string deviceId, DateTimeOffset start)
        => db.Set<MobileAnalyticsMaterializationEntity>().Add(new MobileAnalyticsMaterializationEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            Timezone = MobileAnalyticsDefaults.DefaultTimezone,
            CoveredFromUtc = start,
            CoveredToUtc = start.AddDays(1),
            GeneratedAt = start,
            CreatedAt = start,
            UpdatedAt = start,
        });
}

/// <summary>SQLite 内存库 + 生产同款"声明支持重试"的执行策略。</summary>
internal static class DeviceScopeTestDb
{
    public static async Task<ScopeContext> CreateAsync(Guid actingUserId)
    {
        MobileTestHelpers.RegisterMobileModule();
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseSqlite(connection, o => o.ExecutionStrategy(
                d => new DeviceManagementTestDb.DeclaredRetryingExecutionStrategy(d)))
            .Options;
        var db = new DeviceScopeSqliteDbContext(options, MobileTestHelpers.CurrentUser(actingUserId));
        await db.Database.EnsureCreatedAsync();
        return new ScopeContext(db, connection);
    }

    internal sealed class ScopeContext(PimDbContext db, SqliteConnection connection) : IAsyncDisposable
    {
        public PimDbContext Db { get; } = db;
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
