using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 真库端到端验证（issue #230 / #231）。无 PostgreSQL（或未设置 <c>PIM_TEST_CONN</c>）时
/// 用 <c>Skip.If</c> 显式跳过，报告显示 Skipped；源码里不内置口令。
///
/// 覆盖两件 SQLite 覆盖不了的事：
/// 1. 用生产同款配置 <c>EnableRetryOnFailure(3)</c>（NpgsqlRetryingExecutionStrategy）跑合并，
///    修复前这里会抛出与生产 500 完全一致的 InvalidOperationException；
/// 2. 合并后用真实的 <see cref="MobileUsageQueryService"/> 按目标设备解析 App 名称/分类
///    （该查询对 DateTimeOffset 排序，SQLite 不支持，真库支持）。
///
/// 所有写入都发生在临时 schema 内，结束时 DROP SCHEMA CASCADE，不碰 public。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DeviceMergeRealDbTests
{
    private static readonly string[] DeviceScopedTables =
    [
        "mobile_devices",
        "mobile_app_catalog",
        "mobile_usage_events",
        "mobile_usage_sessions",
        "mobile_usage_summaries",
        "mobile_location_points",
        "mobile_sync_batches",
        "mobile_timeline_blocks",
        "mobile_usage_aggregates",
        "mobile_analytics_materializations",
        // 阶段一取证（REQ-1~REQ-4 / REQ-9）：合并与删除都会改写/清理这两张表，
        // 临时 schema 必须一起克隆，否则用例会在缺表处失败（而不是验证合并语义）。
        "mobile_forensic_events",
        "mobile_dropped_reason_daily",
    ];

    private const string TargetDeviceId = "android-e2e-target";
    private const string SourceDeviceA = "android-e2e-source-a";
    private const string SourceDeviceB = "android-e2e-source-b";
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T02:00:00Z");

    [SkippableFact]
    public async Task MergeAsync_WithProductionRetryStrategy_MovesEverythingAndKeepsAppNamesResolvable()
    {
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();

        await using var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var schema = $"test_device_merge_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(admin, schema);

            var searchPathConn = new NpgsqlConnectionStringBuilder(connStr) { SearchPath = schema }.ConnectionString;
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(searchPathConn, npgsql => npgsql.EnableRetryOnFailure(3))
                .Options;

            await using var db = new PimDbContext(options);
            await SeedAsync(db);

            var service = new DeviceManagementService(
                db, MobileTestHelpers.CurrentUser(UserId), MobileTestHelpers.Time(Now));

            // 修复前：NpgsqlRetryingExecutionStrategy 在事务内第一条命令即抛
            // "does not support user-initiated transactions"（生产实测 HTTP 500，耗时 53ms）。
            await service.MergeAsync([SourceDeviceA, SourceDeviceB], TargetDeviceId, CancellationToken.None);

            // 1) 源设备整体消失，6 张业务表 + catalog 全部改挂目标设备
            Assert.Empty(await db.Set<MobileDeviceEntity>()
                .Where(d => d.DeviceId == SourceDeviceA || d.DeviceId == SourceDeviceB).ToListAsync());
            Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileUsageSessionEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileUsageSummaryEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileLocationPointEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileSyncBatchEntity>().CountAsync());

            // 2) catalog 无孤儿、无重复：目标设备每个包名恰好一行
            var catalog = await db.Set<MobileAppCatalogEntity>().ToListAsync();
            Assert.All(catalog, row => Assert.Equal(TargetDeviceId, row.DeviceId));
            Assert.Equal(catalog.Count, catalog.Select(row => row.PackageName).Distinct().Count());

            // 3) 真实查询链路：按目标设备解析并入记录的 App 名称与分类
            var query = new MobileUsageQueryService(
                db, MobileTestHelpers.CurrentUser(UserId), MobileTestHelpers.Time(Now));
            var summary = await query.GetSummaryAsync(
                new MobileSummaryQuery(TargetDeviceId, null, null),
                CancellationToken.None);
            var app = Assert.Single(summary.AppRanking);
            Assert.Equal("com.legacy.app", app.PackageName);
            Assert.Equal("Legacy App", app.DisplayName);
            Assert.Equal("tools", app.CategoryName);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connStr);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection conn, string schema)
    {
        // 先显式确认目标库里有可借用的表结构；「缺表」说明这台机器没有可用的真库数据
        // （例如 CI），按 Skip 处理；其余异常（权限、磁盘、SQL 形状错误）必须让用例失败。
        await using (var probe = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relname = ANY(@tables)", conn))
        {
            probe.Parameters.AddWithValue("tables", DeviceScopedTables);
            var found = Convert.ToInt64(await probe.ExecuteScalarAsync() ?? 0L);
            Skip.If(found < DeviceScopedTables.Length,
                "RealDb 缺少移动端表结构（未指向已迁移的库），跳过测试。");
        }

        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", conn))
            await create.ExecuteNonQueryAsync();

        // 借用 pim 库里已存在的表结构，测试库不写 public。
        foreach (var table in DeviceScopedTables)
        {
            await using var copy = new NpgsqlCommand(
                $"CREATE TABLE \"{schema}\".\"{table}\" (LIKE public.\"{table}\" INCLUDING ALL)", conn);
            await copy.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 生产真实形状的端到端验证：把本机 pim 库里「设备最多」那个用户的移动端数据
    /// 原样复制进临时 schema，再把这批设备合并成一台。
    ///
    /// 这条用例覆盖的是合成数据造不出来的情况：重装 App 后用新 device_id 重传同一批记录，
    /// 于是同一个业务键同时存在于多台设备上（实测 events 有 72,147 个键 / 127,583 行、
    /// catalog 有 144 个包名重复）。只按 device_id 整体改写的实现在这里会撞唯一索引报 23505，
    /// 用户看到的仍然是 HTTP 500。
    /// </summary>
    [SkippableFact]
    public async Task MergeAsync_OnProductionShapedData_ResolvesCrossDeviceDuplicateKeys()
    {
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();

        await using var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var schema = $"test_device_merge_real_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(admin, schema);
            await CopyBusiestUserAsync(admin, schema);

            var searchPathConn = new NpgsqlConnectionStringBuilder(connStr) { SearchPath = schema }.ConnectionString;
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(searchPathConn, npgsql => npgsql.EnableRetryOnFailure(3))
                .Options;

            await using var db = new PimDbContext(options);

            var target = await db.Set<MobileDeviceEntity>().OrderByDescending(d => d.LastSeenAtUtc).FirstAsync();
            var sourceDeviceIds = await db.Set<MobileDeviceEntity>()
                .Where(d => d.DeviceId != target.DeviceId)
                .Select(d => d.DeviceId)
                .ToListAsync();
            Assert.True(sourceDeviceIds.Count > 0, "用例前提：镜像里同一个用户至少要有两台设备");

            var duplicatesBefore = await CountDuplicateEventKeysAsync(db);
            Assert.True(duplicatesBefore > 0, "用例前提：真实数据里应存在跨设备的重复业务键");
            // 合并后「每个唯一键恰好剩一行」——同时钉住欠删（唯一键冲突）与过删（丢数据）。
            var distinctEventKeysBefore = await DistinctEventKeyCountAsync(db);
            var distinctSummaryKeysBefore = await DistinctSummaryKeyCountAsync(db);
            var distinctBatchKeysBefore = await DistinctBatchKeyCountAsync(db);
            // 合并前所有设备 catalog 覆盖到的包名集合：合并后必须一个都不能少（#231）。
            var catalogPackagesBefore = await db.Set<MobileAppCatalogEntity>()
                .Select(c => c.PackageName)
                .Distinct()
                .ToListAsync();

            var service = new DeviceManagementService(
                db, MobileTestHelpers.CurrentUser(target.UserId), MobileTestHelpers.Time(Now));

            await service.MergeAsync(sourceDeviceIds, target.DeviceId, CancellationToken.None);

            Assert.Equal(distinctEventKeysBefore, await db.Set<MobileUsageEventEntity>().CountAsync());
            Assert.Equal(distinctSummaryKeysBefore, await db.Set<MobileUsageSummaryEntity>().CountAsync());
            Assert.Equal(distinctBatchKeysBefore, await db.Set<MobileSyncBatchEntity>().CountAsync());
            Assert.Equal(0, await CountDuplicateEventKeysAsync(db));
            Assert.Equal(1, await db.Set<MobileDeviceEntity>().CountAsync());
            Assert.Empty(await db.Set<MobileAppCatalogEntity>()
                .Where(c => sourceDeviceIds.Contains(c.DeviceId)).ToListAsync());
            Assert.Equal(
                await db.Set<MobileAppCatalogEntity>().CountAsync(),
                await db.Set<MobileAppCatalogEntity>().Select(c => c.PackageName).Distinct().CountAsync());

            // #231 的真实数据验收：合并前任何设备能解析出的 App 名称，合并后目标设备都要能解析。
            // （注：真实数据里本来就有约 180 个「出现在 summaries、从不在任何 catalog」的包名，
            //  那是合并之前就存在的数据缺口，不属于本次修复范围。）
            var catalogPackagesAfter = await db.Set<MobileAppCatalogEntity>()
                .Select(c => c.PackageName)
                .Distinct()
                .ToListAsync();
            var lost = catalogPackagesBefore.Except(catalogPackagesAfter).ToList();
            Assert.True(lost.Count == 0, $"合并丢失了 catalog 覆盖：{string.Join(", ", lost.Take(10))}");
            Assert.Equal(1, await db.Set<MobileAppCatalogEntity>().Select(c => c.DeviceId).Distinct().CountAsync());
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connStr);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Task<int> CountDuplicateEventKeysAsync(PimDbContext db)
        => db.Set<MobileUsageEventEntity>()
            .GroupBy(e => new { e.PackageName, e.EventType, e.EventTimestampUtc, e.ClassName })
            .Where(g => g.Count() > 1)
            .CountAsync();

    private static Task<int> DistinctEventKeyCountAsync(PimDbContext db)
        => db.Set<MobileUsageEventEntity>()
            .Select(e => new { e.PackageName, e.EventType, e.EventTimestampUtc, e.ClassName })
            .Distinct()
            .CountAsync();

    private static Task<int> DistinctSummaryKeyCountAsync(PimDbContext db)
        => db.Set<MobileUsageSummaryEntity>()
            .Select(s => new { s.PackageName, s.WindowStartUtc, s.WindowEndUtc, s.SourceKind })
            .Distinct()
            .CountAsync();

    private static Task<int> DistinctBatchKeyCountAsync(PimDbContext db)
        => db.Set<MobileSyncBatchEntity>()
            .Select(b => b.BatchId)
            .Distinct()
            .CountAsync();

    /// <summary>把真实库中「设备数最多」的那个用户的移动端数据复制进临时 schema；库中没有数据时 Skip。</summary>
    private static async Task CopyBusiestUserAsync(NpgsqlConnection conn, string schema)
    {
        Guid userId;
        await using (var pick = new NpgsqlCommand(
            "SELECT user_id FROM public.mobile_devices GROUP BY user_id ORDER BY count(*) DESC LIMIT 1", conn))
        {
            var result = await pick.ExecuteScalarAsync();
            Skip.If(result is not Guid, "RealDb 里没有移动端设备数据，跳过生产形状用例。");
            userId = (Guid)result!;
        }

        // 复制过程中的异常不吞：拷不动就让用例失败，否则它会静默绿灯。
        foreach (var table in DeviceScopedTables)
        {
            await using var copy = new NpgsqlCommand(
                $"INSERT INTO \"{schema}\".\"{table}\" SELECT * FROM public.\"{table}\" WHERE user_id = @userId", conn);
            copy.CommandTimeout = 300;
            copy.Parameters.AddWithValue("userId", userId);
            await copy.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(PimDbContext db)
    {
        db.Set<MobileDeviceEntity>().AddRange(
            Device(TargetDeviceId, Now),
            Device(SourceDeviceA, Now.AddDays(-60)),
            Device(SourceDeviceB, Now.AddDays(-50)));

        db.Set<MobileUsageEventEntity>().AddRange(
            Event(SourceDeviceA, "com.legacy.app", Now.AddDays(-60)),
            Event(SourceDeviceB, "com.legacy.app", Now.AddDays(-50)));

        db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
        {
            UserId = UserId,
            DeviceId = SourceDeviceA,
            PackageName = "com.legacy.app",
            StartUtc = Now.AddDays(-60),
            EndUtc = Now.AddDays(-60).AddMinutes(5),
            DurationMs = 300_000,
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = UserId,
            DeviceId = SourceDeviceA,
            PackageName = "com.legacy.app",
            WindowStartUtc = Now.AddDays(-60),
            WindowEndUtc = Now.AddDays(-60).AddMinutes(15),
            TotalTimeVisibleMs = 300_000,
            LastTimeUsedUtc = Now.AddDays(-60).AddMinutes(15),
            SourceKind = "usage-summary",
        });
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = UserId,
            DeviceId = SourceDeviceA,
            RecordedAtUtc = Now.AddDays(-60),
            Latitude = 31.2304m,
            Longitude = 121.4737m,
            HorizontalAccuracyMeters = 12.5m,
            Provider = "fused",
            Source = "android",
        });
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = UserId,
            DeviceId = SourceDeviceA,
            BatchId = "e2e-batch-a",
            WindowStartUtc = Now.AddDays(-60),
            WindowEndUtc = Now.AddDays(-60).AddMinutes(15),
            AcceptedCount = 1,
            Status = "completed",
            CompletedAtUtc = Now.AddDays(-60).AddMinutes(15),
        });

        // 目标设备已有同名包（生产实测 594 条重复），源设备的条目更新 -> 元数据应被覆盖。
        db.Set<MobileAppCatalogEntity>().AddRange(
            Catalog(TargetDeviceId, "com.legacy.app", "Legacy App (stale)", "uncategorized", Now.AddDays(-90)),
            Catalog(SourceDeviceA, "com.legacy.app", "Legacy App", "tools", Now.AddDays(-30)),
            Catalog(SourceDeviceB, "com.unique.b.app", "Unique B", "social", Now.AddDays(-50)));

        await db.SaveChangesAsync();
    }

    private static MobileDeviceEntity Device(string deviceId, DateTimeOffset lastSeen) => new()
    {
        UserId = UserId,
        DeviceId = deviceId,
        DeviceHash = $"hash-{deviceId}",
        DisplayName = "OPPO PLG110",
        Brand = "OPPO",
        Model = "PLG110",
        OsVersion = "15",
        AppVersion = "2026.09.504",
        RegisteredAtUtc = lastSeen,
        LastSeenAtUtc = lastSeen,
    };

    private static MobileUsageEventEntity Event(string deviceId, string packageName, DateTimeOffset timestamp) => new()
    {
        UserId = UserId,
        DeviceId = deviceId,
        PackageName = packageName,
        EventType = "foreground",
        EventTimestampUtc = timestamp,
        SourceWindowStartUtc = timestamp,
        SourceWindowEndUtc = timestamp.AddMinutes(1),
        CollectedAtUtc = timestamp.AddMinutes(1),
    };

    private static MobileAppCatalogEntity Catalog(
        string deviceId, string packageName, string displayName, string category, DateTimeOffset updatedAt) => new()
    {
        UserId = UserId,
        DeviceId = deviceId,
        PackageName = packageName,
        DisplayName = displayName,
        Category = category,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
    };
}
