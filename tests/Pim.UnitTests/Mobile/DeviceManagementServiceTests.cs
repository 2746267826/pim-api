using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 关系型测试库：SQLite 内存库 + 一个「永不真正重试但声明支持重试」的执行策略。
///
/// 为什么需要它：
/// 1. <c>DeviceManagementService</c> 的合并/删除只走 <c>ExecuteUpdate/ExecuteDelete</c>，
///    InMemory 提供程序不支持这两个 API，无法覆盖真实代码路径；
/// 2. 生产故障（issue #230）是 EF Core 在 <c>RetriesOnFailure == true</c> 且存在
///    用户发起事务时抛出的提供程序无关异常，只要执行策略声明支持重试即可复现，
///    不需要真的连 Npgsql。
/// </summary>
internal static class DeviceManagementTestDb
{
    public static async Task<SqliteContext> CreateAsync()
    {
        MobileTestHelpers.RegisterMobileModule();
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseSqlite(connection, o => o.ExecutionStrategy(d => new DeclaredRetryingExecutionStrategy(d)))
            .Options;
        var db = new MobileSqliteDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new SqliteContext(db, connection);
    }

    /// <summary>
    /// 只映射 mobile 模块的 DbContext。
    ///
    /// 不能直接用 <see cref="PimDbContext"/> + EnsureCreated：模型会带上所有「已注册模块」的
    /// 实体配置，而其它测试可能已经注册了 PcTracker，其配置里有 Postgres 专有的默认值
    /// （<c>'[]'::jsonb</c>），SQLite 建表脚本会因此抛 "unrecognized token"。
    /// 那会让本文件的结果取决于测试执行顺序。这里只应用 mobile 程序集的配置，
    /// 让建表脚本与执行顺序无关。
    /// </summary>
    private sealed class MobileSqliteDbContext(DbContextOptions<PimDbContext> options) : PimDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfigurationsFromAssembly(typeof(MobileDeviceEntity).Assembly);
    }

    internal sealed class DeclaredRetryingExecutionStrategy : ExecutionStrategy
    {
        public DeclaredRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1))
        {
        }

        // 不真的重试（SQLite 内存库重试意义不大），但保留 RetriesOnFailure == true，
        // 从而触发与 NpgsqlRetryingExecutionStrategy 完全相同的 EF 校验分支。
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    internal sealed class SqliteContext(PimDbContext db, SqliteConnection connection) : IAsyncDisposable
    {
        public PimDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

/// <summary>
/// issue #230：显式事务与重试执行策略冲突（生产必现 HTTP 500）。
/// issue #231：合并/删除不迁移 mobile_app_catalog，历史记录 App 名称/分类解析丢失并留下孤儿行。
/// </summary>
public sealed class DeviceManagementServiceTests
{
    private const string TargetDeviceId = "android-target";
    private const string SourceDeviceA = "android-source-a";
    private const string SourceDeviceB = "android-source-b";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T02:00:00Z");

    // ---------------------------------------------------------------- #230

    [Fact]
    public async Task MergeAsync_RunsInsideExecutionStrategy_SoRetryingProvidersDoNotRejectTheUserTransaction()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedEvent(db, SourceDeviceA, "com.old.app", Now.AddDays(-60));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // 修复前：NpgsqlRetryingExecutionStrategy（以及任何声明支持重试的策略）会在
        // 事务内执行第一条命令时抛 InvalidOperationException，生产上表现为 HTTP 500。
        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Empty(await db.Set<MobileDeviceEntity>().Where(d => d.DeviceId == SourceDeviceA).ToListAsync());
        Assert.Single(await db.Set<MobileUsageEventEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_RunsInsideExecutionStrategy_SoRetryingProvidersDoNotRejectTheUserTransaction()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedEvent(db, TargetDeviceId, "com.old.app", Now.AddDays(-1));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        Assert.Empty(await db.Set<MobileDeviceEntity>().Where(d => d.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Empty(await db.Set<MobileUsageEventEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
    }

    [Fact]
    public void MergeAndDelete_StartTheirUserTransactionsThroughTheConfiguredExecutionStrategy()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("src", "modules", "Pim.Module.Mobile", "Services", "DeviceManagementService.cs")));

        Assert.Contains("CreateExecutionStrategy", source);
        Assert.Contains("strategy.ExecuteAsync", source);
        Assert.DoesNotContain("await using var tx = await _db.Database.BeginTransactionAsync(ct);", source);
    }

    // ---------------------------------------------------------------- #231

    [Fact]
    public async Task MergeAsync_MovesEveryDeviceScopedRowIncludingAppCatalog()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedEvent(db, SourceDeviceA, "com.old.app", Now.AddDays(-60));
        SeedSession(db, SourceDeviceA, "com.old.app", Now.AddDays(-60));
        SeedSummary(db, SourceDeviceA, "com.old.app", Now.AddDays(-60));
        SeedLocation(db, SourceDeviceA, Now.AddDays(-60));
        SeedBatch(db, SourceDeviceA, "batch-a", Now.AddDays(-60));
        SeedTimelineBlock(db, SourceDeviceA, Now.AddDays(-60));
        SeedCatalog(db, SourceDeviceA, "com.old.app", "Old App", "tools", Now.AddDays(-60));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        Assert.Empty(await RowsStillOwnedByAsync(db, SourceDeviceA));
        Assert.Single(await db.Set<MobileUsageEventEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Single(await db.Set<MobileUsageSessionEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Single(await db.Set<MobileUsageSummaryEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Single(await db.Set<MobileLocationPointEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Single(await db.Set<MobileSyncBatchEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        Assert.Single(await db.Set<MobileTimelineBlockEntity>().Where(e => e.DeviceId == TargetDeviceId).ToListAsync());
        var catalog = await db.Set<MobileAppCatalogEntity>().SingleAsync();
        Assert.Equal(TargetDeviceId, catalog.DeviceId);
        Assert.Equal("com.old.app", catalog.PackageName);
    }

    [Fact]
    public async Task MergeAsync_MergesCatalogEntriesThatCollideOnTheTargetByPackageName()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        // 生产实测：目标设备 157 条、5 台源设备各 141~144 条，其中 594 条与目标包名重复，
        // 直接改写 device_id 会撞 (user_id, device_id, package_name) 唯一索引。
        SeedCatalog(db, TargetDeviceId, "com.shared.app", "Shared Old", "tools", Now.AddDays(-90));
        SeedCatalog(db, SourceDeviceA, "com.shared.app", "Shared New", "social", Now.AddDays(-30));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var rows = await db.Set<MobileAppCatalogEntity>().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("Shared New", row.DisplayName);
        Assert.Equal("social", row.Category);
        // 保留的是「元数据采集时间」，不是合并时刻：改成合并时刻会让目标行
        // 永远比后续源设备新，多次合并后新采集到的名称/分类会被丢弃。
        Assert.Equal(Now.AddDays(-30), row.UpdatedAt);
    }

    [Fact]
    public async Task MergeAsync_StillAcceptsNewerCatalogMetadataOnALaterMerge()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedCatalog(db, TargetDeviceId, "com.shared.app", "T-Old", "uncategorized", Now.AddDays(-90));
        SeedCatalog(db, SourceDeviceA, "com.shared.app", "S1", "tools", Now.AddDays(-30));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);
        Assert.Equal("S1", (await db.Set<MobileAppCatalogEntity>().SingleAsync()).DisplayName);

        // 第二次合并：S2 的元数据采集时间更晚，必须能覆盖第一次合并的结果。
        SeedDevice(db, SourceDeviceB, Now.AddDays(-50));
        SeedCatalog(db, SourceDeviceB, "com.shared.app", "S2-Newest", "social", Now.AddDays(-10));
        await db.SaveChangesAsync();

        await service.MergeAsync([SourceDeviceB], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<MobileAppCatalogEntity>().ToListAsync());
        Assert.Equal("S2-Newest", row.DisplayName);
        Assert.Equal("social", row.Category);
        Assert.Equal(Now.AddDays(-10), row.UpdatedAt);
    }

    [Fact]
    public async Task MergeAsync_MergesCatalogEntriesThatCollideAcrossMultipleSources()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedDevice(db, SourceDeviceB, Now.AddDays(-50));
        SeedCatalog(db, SourceDeviceA, "com.only.on.sources", "Older", "tools", Now.AddDays(-40));
        SeedCatalog(db, SourceDeviceB, "com.only.on.sources", "Newer", "social", Now.AddDays(-20));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<MobileAppCatalogEntity>().ToListAsync());
        Assert.Equal(TargetDeviceId, row.DeviceId);
        Assert.Equal("Newer", row.DisplayName);
    }

    [Fact]
    public async Task MergeAsync_KeepsTheTargetEntryWhenItsOwnCatalogRowIsAtLeastAsFresh()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedCatalog(db, TargetDeviceId, "com.shared.app", "Target Newest", "tools", Now.AddDays(-1));
        SeedCatalog(db, SourceDeviceA, "com.shared.app", "Source Older", "social", Now.AddDays(-30));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<MobileAppCatalogEntity>().ToListAsync());
        Assert.Equal("Target Newest", row.DisplayName);
        Assert.Equal("tools", row.Category);
    }

    [Fact]
    public async Task MergeAsync_RemovesDuplicateRowsThatShareAUniqueKeyAcrossSourceDevices()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedDevice(db, SourceDeviceB, Now.AddDays(-50));
        // 重装 App 后用新 device_id 重传同一批记录：业务键完全相同的行同时存在于多台源设备。
        // 生产库实测 events 有 72,147 个键 / 127,583 行这类重复，逐条改写 device_id 会撞
        // (user_id, device_id, package_name, event_type, event_timestamp_utc, class_name) 唯一索引。
        var timestamp = Now.AddDays(-55);
        SeedEvent(db, SourceDeviceA, "com.dup.app", timestamp);
        SeedEvent(db, SourceDeviceB, "com.dup.app", timestamp);
        SeedSummary(db, SourceDeviceA, "com.dup.app", timestamp);
        SeedSummary(db, SourceDeviceB, "com.dup.app", timestamp);
        SeedBatch(db, SourceDeviceA, "batch-dup", timestamp);
        SeedBatch(db, SourceDeviceB, "batch-dup", timestamp);
        // 不重复的行必须原样保留下来
        SeedEvent(db, SourceDeviceA, "com.unique.app", timestamp);
        SeedEvent(db, SourceDeviceB, "com.other.app", timestamp.AddMinutes(5));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

        Assert.Empty(await RowsStillOwnedByAsync(db, SourceDeviceA));
        Assert.Empty(await RowsStillOwnedByAsync(db, SourceDeviceB));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().CountAsync(e => e.PackageName == "com.dup.app"));
        Assert.Equal(1, await db.Set<MobileUsageSummaryEntity>().CountAsync(s => s.PackageName == "com.dup.app"));
        Assert.Equal(1, await db.Set<MobileSyncBatchEntity>().CountAsync(b => b.BatchId == "batch-dup"));
        Assert.Equal(3, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task MergeAsync_KeepsTheTargetsOwnRowWhenASourceRepeatsItsUniqueKey()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        var timestamp = Now.AddDays(-55);
        SeedEvent(db, TargetDeviceId, "com.dup.app", timestamp);
        SeedEvent(db, SourceDeviceA, "com.dup.app", timestamp);
        SeedCatalog(db, TargetDeviceId, "com.dup.app", "Target Copy", "tools", Now.AddDays(-90));
        SeedCatalog(db, SourceDeviceA, "com.dup.app", "Source Copy", "social", Now.AddDays(-30));
        await db.SaveChangesAsync();
        var targetRowId = await db.Set<MobileUsageEventEntity>().Where(e => e.DeviceId == TargetDeviceId).Select(e => e.Id).SingleAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var remaining = Assert.Single(await db.Set<MobileUsageEventEntity>().ToListAsync());
        Assert.Equal(targetRowId, remaining.Id);
        Assert.Equal(1, await db.Set<MobileAppCatalogEntity>().CountAsync());
    }

    [Fact]
    public async Task PreviewMergeAsync_RejectsAnEmptyOrNullSourceList()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(
            () => service.PreviewMergeAsync([], TargetDeviceId, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(
            () => service.PreviewMergeAsync(null!, TargetDeviceId, CancellationToken.None));
    }

    [Fact]
    public async Task MergeAsync_RejectsAnEmptyOrNullSourceList()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(
            () => service.MergeAsync([], TargetDeviceId, CancellationToken.None));
        // 请求体省略 sourceDeviceIds 时字段为 null；不能让它变成 NRE → HTTP 500。
        await Assert.ThrowsAsync<DomainException>(
            () => service.MergeAsync(null!, TargetDeviceId, CancellationToken.None));
        Assert.Equal(1, await db.Set<MobileDeviceEntity>().CountAsync());
    }

    [Fact]
    public async Task MergeAsync_BreaksCatalogTiesTheSameWayTheReadPathDoes()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        // UpdatedAt 完全相同，但源设备那条的 LastUpdateTimeUtc 更新：
        // 查询侧（MobileAppClassificationService）按 UpdatedAt → LastUpdateTimeUtc → …
        // 取最新，合并必须保留同一行，否则合并会改变用户看到的 App 名称。
        var sameMoment = Now.AddDays(-10);
        var winnerCreatedAt = Now.AddDays(-5);
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = TargetDeviceId,
            PackageName = "com.tie.app",
            DisplayName = "TARGET-OLD",
            Category = "uncategorized",
            CreatedAt = Now.AddDays(-40),
            UpdatedAt = sameMoment,
            LastUpdateTimeUtc = Now.AddDays(-20),
        });
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = SourceDeviceA,
            PackageName = "com.tie.app",
            DisplayName = "SOURCE-NEW",
            Category = "tools",
            CreatedAt = winnerCreatedAt,
            UpdatedAt = sameMoment,
            LastUpdateTimeUtc = Now.AddDays(-1),
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        var row = Assert.Single(await db.Set<MobileAppCatalogEntity>().ToListAsync());
        Assert.Equal("SOURCE-NEW", row.DisplayName);
        Assert.Equal("tools", row.Category);
        // 取舍链前三级都要跟着胜者走，否则未参与合并的设备可能在合并后反超。
        Assert.Equal(sameMoment, row.UpdatedAt);
        Assert.Equal(winnerCreatedAt, row.CreatedAt);
    }

    [Fact]
    public async Task MergeAsync_KeepsCatalogCoverageForEveryPackageMergedIntoTheTargetDevice()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedSummary(db, SourceDeviceA, "com.old.app", Now.AddDays(-60));
        SeedSummary(db, SourceDeviceA, "com.shared.app", Now.AddDays(-60));
        SeedCatalog(db, TargetDeviceId, "com.shared.app", "Shared", "tools", Now.AddDays(-90));
        SeedCatalog(db, SourceDeviceA, "com.old.app", "Old App", "tools", Now.AddDays(-60));
        SeedCatalog(db, SourceDeviceA, "com.shared.app", "Shared Newer", "social", Now.AddDays(-30));
        await db.SaveChangesAsync();
        var merge = CreateService(db);

        await merge.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

        // 查询侧按 device_id 过滤 catalog 解析 App 名称/分类，所以目标设备必须覆盖
        // 它名下所有包名——这正是修复前丢失的部分（issue #231）。
        var targetPackages = await db.Set<MobileUsageSummaryEntity>()
            .Where(s => s.DeviceId == TargetDeviceId)
            .Select(s => s.PackageName)
            .Distinct()
            .ToListAsync();
        var catalog = await db.Set<MobileAppCatalogEntity>()
            .Where(c => c.DeviceId == TargetDeviceId)
            .ToDictionaryAsync(c => c.PackageName);

        Assert.Equal(2, targetPackages.Count);
        Assert.All(targetPackages, package => Assert.True(
            catalog.ContainsKey(package),
            $"package '{package}' has no catalog entry under the target device"));
        Assert.Equal("Old App", catalog["com.old.app"].DisplayName);
        Assert.Equal("Shared Newer", catalog["com.shared.app"].DisplayName);
        Assert.Equal("social", catalog["com.shared.app"].Category);
    }

    [Fact]
    public async Task MergeAsync_LeavesNoCatalogRowsPointingAtRemovedDevices()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedDevice(db, SourceDeviceA, Now.AddDays(-60));
        SeedDevice(db, SourceDeviceB, Now.AddDays(-50));
        SeedCatalog(db, SourceDeviceA, "com.a.app", "A", "tools", Now.AddDays(-60));
        SeedCatalog(db, SourceDeviceB, "com.b.app", "B", "tools", Now.AddDays(-50));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

        var remainingDeviceIds = await db.Set<MobileDeviceEntity>().Select(d => d.DeviceId).ToListAsync();
        var catalogDeviceIds = await db.Set<MobileAppCatalogEntity>().Select(c => c.DeviceId).Distinct().ToListAsync();
        Assert.All(catalogDeviceIds, id => Assert.Contains(id, remainingDeviceIds));
        Assert.Equal(2, await db.Set<MobileAppCatalogEntity>().CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesCatalogRowsSoNoOrphansSurvive()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, TargetDeviceId, Now);
        SeedCatalog(db, TargetDeviceId, "com.only.app", "Only App", "tools", Now.AddDays(-1));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.DeleteAsync(TargetDeviceId, CancellationToken.None);

        Assert.Empty(await db.Set<MobileAppCatalogEntity>().ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private static DeviceManagementService CreateService(PimDbContext db)
        => new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    private static async Task<List<string>> RowsStillOwnedByAsync(PimDbContext db, string deviceId)
    {
        var hits = new List<string>();
        if (await db.Set<MobileUsageEventEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("events");
        if (await db.Set<MobileUsageSessionEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("sessions");
        if (await db.Set<MobileUsageSummaryEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("summaries");
        if (await db.Set<MobileLocationPointEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("locations");
        if (await db.Set<MobileSyncBatchEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("batches");
        if (await db.Set<MobileTimelineBlockEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("timeline");
        if (await db.Set<MobileAppCatalogEntity>().AnyAsync(e => e.DeviceId == deviceId)) hits.Add("catalog");
        return hits;
    }

    private static void SeedDevice(PimDbContext db, string deviceId, DateTimeOffset lastSeen)
        => db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            DeviceHash = $"hash-{deviceId}",
            DisplayName = "OPPO PLG110",
            Brand = "OPPO",
            Model = "PLG110",
            OsVersion = "15",
            AppVersion = "2026.09.504",
            RegisteredAtUtc = lastSeen,
            LastSeenAtUtc = lastSeen,
        });

    private static void SeedCatalog(
        PimDbContext db, string deviceId, string packageName, string displayName, string category, DateTimeOffset updatedAt)
        => db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            DisplayName = displayName,
            Category = category,
            UpdatedAt = updatedAt,
            CreatedAt = updatedAt,
        });

    private static void SeedEvent(PimDbContext db, string deviceId, string packageName, DateTimeOffset timestamp)
        => db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            EventType = "foreground",
            // 唯一索引含 class_name；生产数据里没有 NULL（NULL 会被唯一索引视为互不相同，
            // 反而绕过约束），这里同样给空串以贴近真实形态。
            ClassName = string.Empty,
            EventTimestampUtc = timestamp,
            SourceWindowStartUtc = timestamp,
            SourceWindowEndUtc = timestamp.AddMinutes(1),
            CollectedAtUtc = timestamp.AddMinutes(1),
        });

    private static void SeedSession(PimDbContext db, string deviceId, string packageName, DateTimeOffset start)
        => db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            StartUtc = start,
            EndUtc = start.AddMinutes(5),
            DurationMs = 300_000,
        });

    private static void SeedSummary(PimDbContext db, string deviceId, string packageName, DateTimeOffset windowStart)
        => db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            TotalTimeVisibleMs = 300_000,
            LastTimeUsedUtc = windowStart.AddMinutes(15),
            SourceKind = "usage-summary",
        });

    private static void SeedLocation(PimDbContext db, string deviceId, DateTimeOffset recordedAt)
        => db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = 31.2304m,
            Longitude = 121.4737m,
            HorizontalAccuracyMeters = 12.5m,
            Provider = "fused",
            Source = "android",
        });

    private static void SeedBatch(PimDbContext db, string deviceId, string batchId, DateTimeOffset windowStart)
        => db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            BatchId = batchId,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowStart.AddMinutes(15),
            AcceptedCount = 1,
            Status = "completed",
            CompletedAtUtc = windowStart.AddMinutes(15),
        });

    private static void SeedTimelineBlock(PimDbContext db, string deviceId, DateTimeOffset start)
        => db.Set<MobileTimelineBlockEntity>().Add(new MobileTimelineBlockEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            StartUtc = start,
            EndUtc = start.AddMinutes(15),
            LocalDate = start.ToString("yyyy-MM-dd"),
            ForegroundSeconds = 300,
            SessionCount = 1,
            AppCount = 1,
        });

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
