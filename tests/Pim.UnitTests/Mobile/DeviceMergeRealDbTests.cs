using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Pim.Core.Caching;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;
using Pim.Infrastructure.Operations;
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
        "mobile_location_point_revisions",
        "mobile_location_segment_merges",
        "mobile_sync_batches",
        "mobile_timeline_blocks",
        "mobile_usage_aggregates",
        "mobile_analytics_materializations",
        // 阶段一取证（REQ-1~REQ-4 / REQ-9）：合并与删除都会改写/清理这两张表，
        // 临时 schema 必须一起克隆，否则用例会在缺表处失败（而不是验证合并语义）。
        "mobile_forensic_events",
        "mobile_dropped_reason_daily",
        // 工单 02 C1：心跳 / 端点三张表也纳入合并与删除范围。
        // daemon_heartbeats 没有 user_id（克隆时按 device_id 借道 mobile_devices 过滤），
        // 这正是它在范围里需要"归属判断"的原因。
        "daemon_heartbeats",
        "endpoint_statuses",
        "endpoint_notification_actions",
    ];

    /// <summary>没有 <c>user_id</c> 列、只能按 device_id 归属判断的表。</summary>
    private static readonly string[] DeviceIdOnlyTables = ["daemon_heartbeats"];

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

    [SkippableFact]
    public async Task MergeAsync_OnPostgres_RemapsDuplicateNaturalKeyHistoryWithoutDanglingReferences()
    {
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();

        await using var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var schema = $"test_device_merge_history_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(admin, schema);

            var searchPathConn = new NpgsqlConnectionStringBuilder(connStr) { SearchPath = schema }.ConnectionString;
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(searchPathConn, npgsql => npgsql.EnableRetryOnFailure(3))
                .Options;

            await using var db = new PimDbContext(options);
            var recordedAt = Now.AddDays(-45);
            var targetPoint = new MobileLocationPointEntity
            {
                UserId = UserId,
                DeviceId = TargetDeviceId,
                RecordedAtUtc = recordedAt,
                Latitude = 31.2304m,
                Longitude = 121.4737m,
                HorizontalAccuracyMeters = 12.5m,
                Provider = "fused",
                Source = "android",
            };
            var sourcePoint = new MobileLocationPointEntity
            {
                UserId = UserId,
                DeviceId = SourceDeviceA,
                RecordedAtUtc = recordedAt,
                Latitude = 31.2304m,
                Longitude = 121.4737m,
                HorizontalAccuracyMeters = 12.5m,
                Provider = "fused",
                Source = "android",
            };
            var targetMerge = new MobileLocationSegmentMergeEntity
            {
                UserId = UserId,
                DeviceId = TargetDeviceId,
                BeforePointId = targetPoint.Id,
                AfterPointId = targetPoint.Id,
                Kind = "stay",
                ActorUserId = UserId,
                CreatedAt = Now.AddDays(-3),
            };
            db.Set<MobileDeviceEntity>().AddRange(
                Device(TargetDeviceId, Now),
                Device(SourceDeviceA, Now.AddDays(-60)));
            db.Set<MobileLocationPointEntity>().AddRange(targetPoint, sourcePoint);
            db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
            {
                UserId = UserId,
                PointId = sourcePoint.Id,
                DeviceId = SourceDeviceA,
                Operation = "correct",
                RevisionBefore = 0,
                RevisionAfter = 1,
                OriginalLatitude = sourcePoint.Latitude,
                OriginalLongitude = sourcePoint.Longitude,
                NewLatitude = 31.24m,
                NewLongitude = 121.48m,
                ActorUserId = UserId,
                CreatedAt = Now.AddDays(-2),
            });
            db.Set<MobileLocationSegmentMergeEntity>().AddRange(
                targetMerge,
                new MobileLocationSegmentMergeEntity
                {
                    UserId = UserId,
                    DeviceId = SourceDeviceA,
                    BeforePointId = sourcePoint.Id,
                    AfterPointId = sourcePoint.Id,
                    Kind = "move",
                    ActorUserId = UserId,
                    CreatedAt = Now.AddDays(-1),
                });
            await db.SaveChangesAsync();

            var service = new DeviceManagementService(
                db, MobileTestHelpers.CurrentUser(UserId), MobileTestHelpers.Time(Now));

            await service.MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);

            db.ChangeTracker.Clear();
            var retainedPoint = Assert.Single(await db.Set<MobileLocationPointEntity>()
                .IgnoreQueryFilters()
                .ToListAsync());
            Assert.Equal(targetPoint.Id, retainedPoint.Id);
            Assert.Equal(TargetDeviceId, retainedPoint.DeviceId);

            var revision = Assert.Single(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
            Assert.Equal(targetPoint.Id, revision.PointId);
            Assert.Equal(TargetDeviceId, revision.DeviceId);

            var merge = Assert.Single(await db.Set<MobileLocationSegmentMergeEntity>().ToListAsync());
            Assert.Equal(targetMerge.Id, merge.Id);
            Assert.Equal(TargetDeviceId, merge.DeviceId);
            Assert.Equal(targetPoint.Id, merge.BeforePointId);
            Assert.Equal(targetPoint.Id, merge.AfterPointId);
            Assert.Equal("stay", merge.Kind);

            // 直接按已删除源点 ID 做反查，确认两张历史表都没有可指向已不存在点的悬空引用。
            Assert.False(await db.Set<MobileLocationPointRevisionEntity>()
                .AnyAsync(row => row.PointId == sourcePoint.Id));
            Assert.False(await db.Set<MobileLocationSegmentMergeEntity>()
                .AnyAsync(row => row.BeforePointId == sourcePoint.Id || row.AfterPointId == sourcePoint.Id));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connStr);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 设备合并读取重复自然键快照后，人工纠正先提交（真实 200），随后合并才执行去重判断。
    ///
    /// <para>
    /// 这里用 PostgreSQL 独立连接制造确定交错：合并的重复点 JOIN 查询已经执行完毕
    /// （结果集按 READ COMMITTED 快照锁定），但尚未被 EF 消费；此时通过真实
    /// <see cref="MobileLocationCorrectionService"/> 对源点或目标点提交坐标纠正。两者都会
    /// 让“重复自然键”的快照判定过期：修复前合并会按旧 point ID 无条件删除源点，纠正返回
    /// 200 却静默丢失；修复后源点 CAS 删除或目标点 CAS 复核发现 revision 已变化，整笔合并
    /// 返回 6205 → HTTP 409，纠正与源设备都保留。随后重试合并必须成功，且纠正后的点作为
    /// 非重复点迁到目标设备。
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeAsync_OnPostgres_DoesNotDeletePointCorrectedAfterDuplicateSnapshot(
        bool correctTarget)
    {
        var connStr = RealDbTestConnection.Require();
        MobileTestHelpers.RegisterMobileModule();

        await using var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var schema = $"test_device_merge_correction_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(admin, schema, "audit_logs", "mobile_saved_places");

            var searchPathConn = new NpgsqlConnectionStringBuilder(connStr) { SearchPath = schema }.ConnectionString;
            var readOptions = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(searchPathConn, npgsql => npgsql.EnableRetryOnFailure(3))
                .Options;
            var pause = new PauseAfterDuplicateLocationSnapshotInterceptor();
            var mergeOptions = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(searchPathConn, npgsql => npgsql.EnableRetryOnFailure(3))
                .AddInterceptors(pause)
                .Options;

            var recordedAt = Now.AddDays(-45);
            var targetPoint = new MobileLocationPointEntity
            {
                UserId = UserId,
                DeviceId = TargetDeviceId,
                RecordedAtUtc = recordedAt,
                Latitude = 31.2304m,
                Longitude = 121.4737m,
                HorizontalAccuracyMeters = 12.5m,
                Provider = "fused",
                Source = "android",
            };
            var sourcePoint = new MobileLocationPointEntity
            {
                UserId = UserId,
                DeviceId = SourceDeviceA,
                RecordedAtUtc = recordedAt,
                Latitude = 31.2304m,
                Longitude = 121.4737m,
                HorizontalAccuracyMeters = 12.5m,
                Provider = "fused",
                Source = "android",
            };
            var sourceMerge = new MobileLocationSegmentMergeEntity
            {
                UserId = UserId,
                DeviceId = SourceDeviceA,
                BeforePointId = sourcePoint.Id,
                AfterPointId = sourcePoint.Id,
                Kind = "move",
                ActorUserId = UserId,
                CreatedAt = Now.AddDays(-1),
            };

            await using (var seedDb = new PimDbContext(readOptions))
            {
                seedDb.Set<MobileDeviceEntity>().AddRange(
                    Device(TargetDeviceId, Now),
                    Device(SourceDeviceA, Now.AddDays(-60)));
                seedDb.Set<MobileLocationPointEntity>().AddRange(targetPoint, sourcePoint);
                seedDb.Set<MobileLocationSegmentMergeEntity>().Add(sourceMerge);
                await seedDb.SaveChangesAsync();
            }

            await using var mergeDb = new PimDbContext(mergeOptions);
            var mergeTask = new DeviceManagementService(
                    mergeDb,
                    MobileTestHelpers.CurrentUser(UserId),
                    MobileTestHelpers.Time(Now))
                .MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);
            var paused = await Task.WhenAny(pause.Paused, mergeTask)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (paused != pause.Paused)
            {
                // 合并没走到重复点快照就失败时，把原始异常抛出来，而不是等暂停超时。
                await mergeTask;
                Assert.Fail("合并未在重复定位点快照处暂停。");
            }

            try
            {
                var correctedPointId = correctTarget ? targetPoint.Id : sourcePoint.Id;
                await using var correctionDb = new PimDbContext(readOptions);
                var corrected = await CreateCorrectionService(correctionDb).CorrectAsync(
                    correctedPointId,
                    new MobileLocationPointCorrectionRequest(
                        Latitude: 31.2401234,
                        Longitude: 121.4805678,
                        Revision: 0,
                        Excluded: false,
                        Reason: "并发坐标纠正"),
                    CancellationToken.None);
                Assert.Equal(1, corrected.Point.Revision);
                Assert.Equal(31.2401234, corrected.Point.Latitude, 7);
                Assert.Equal(121.4805678, corrected.Point.Longitude, 7);
            }
            finally
            {
                pause.Release();
            }

            var conflict = await Assert.ThrowsAsync<DomainException>(
                () => mergeTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(6205, conflict.ErrorCode);

            await using (var verifyDb = new PimDbContext(readOptions))
            {
                Assert.NotNull(await verifyDb.Set<MobileDeviceEntity>()
                    .SingleOrDefaultAsync(device => device.UserId == UserId
                        && device.DeviceId == SourceDeviceA));
                var sourceAfterConflict = await verifyDb.Set<MobileLocationPointEntity>()
                    .IgnoreQueryFilters()
                    .SingleAsync(point => point.Id == sourcePoint.Id);
                Assert.Equal(SourceDeviceA, sourceAfterConflict.DeviceId);

                var targetAfterConflict = await verifyDb.Set<MobileLocationPointEntity>()
                    .IgnoreQueryFilters()
                    .SingleAsync(point => point.Id == targetPoint.Id);
                Assert.Equal(TargetDeviceId, targetAfterConflict.DeviceId);

                var correctedAfterConflict = correctTarget ? targetAfterConflict : sourceAfterConflict;
                Assert.Equal(1, correctedAfterConflict.Revision);
                Assert.Equal(31.2401234m, correctedAfterConflict.Latitude);
                Assert.Equal(121.4805678m, correctedAfterConflict.Longitude);
                var untouchedAfterConflict = correctTarget ? sourceAfterConflict : targetAfterConflict;
                Assert.Equal(0, untouchedAfterConflict.Revision);
                Assert.Equal(31.2304m, untouchedAfterConflict.Latitude);
                Assert.Equal(121.4737m, untouchedAfterConflict.Longitude);

                var correctedPointId = correctTarget ? targetPoint.Id : sourcePoint.Id;
                var revision = Assert.Single(await verifyDb.Set<MobileLocationPointRevisionEntity>()
                    .Where(row => row.PointId == correctedPointId)
                    .ToListAsync());
                Assert.Equal(correctTarget ? TargetDeviceId : SourceDeviceA, revision.DeviceId);
                var mergeRow = Assert.Single(await verifyDb.Set<MobileLocationSegmentMergeEntity>()
                    .Where(row => row.Id == sourceMerge.Id)
                    .ToListAsync());
                Assert.Equal(SourceDeviceA, mergeRow.DeviceId);
            }

            await using (var retryDb = new PimDbContext(readOptions))
            {
                await new DeviceManagementService(
                        retryDb,
                        MobileTestHelpers.CurrentUser(UserId),
                        MobileTestHelpers.Time(Now))
                    .MergeAsync([SourceDeviceA], TargetDeviceId, CancellationToken.None);
            }

            await using (var verifyDb = new PimDbContext(readOptions))
            {
                Assert.Empty(await verifyDb.Set<MobileDeviceEntity>()
                    .Where(device => device.DeviceId == SourceDeviceA)
                    .ToListAsync());
                var points = await verifyDb.Set<MobileLocationPointEntity>()
                    .IgnoreQueryFilters()
                    .ToListAsync();
                Assert.Equal(2, points.Count);
                Assert.All(points, point => Assert.Equal(TargetDeviceId, point.DeviceId));

                var correctedPointId = correctTarget ? targetPoint.Id : sourcePoint.Id;
                var correctedAfterRetry = Assert.Single(points, point => point.Id == correctedPointId);
                Assert.Equal(1, correctedAfterRetry.Revision);
                Assert.Equal(31.2401234m, correctedAfterRetry.Latitude);
                Assert.Equal(121.4805678m, correctedAfterRetry.Longitude);
                var untouchedPointId = correctTarget ? sourcePoint.Id : targetPoint.Id;
                var untouchedAfterRetry = Assert.Single(points, point => point.Id == untouchedPointId);
                Assert.Equal(0, untouchedAfterRetry.Revision);
                Assert.Equal(31.2304m, untouchedAfterRetry.Latitude);
                Assert.Equal(121.4737m, untouchedAfterRetry.Longitude);

                var revision = Assert.Single(await verifyDb.Set<MobileLocationPointRevisionEntity>()
                    .Where(row => row.PointId == correctedPointId)
                    .ToListAsync());
                Assert.Equal(TargetDeviceId, revision.DeviceId);
                var mergeRow = Assert.Single(await verifyDb.Set<MobileLocationSegmentMergeEntity>()
                    .Where(row => row.Id == sourceMerge.Id)
                    .ToListAsync());
                Assert.Equal(TargetDeviceId, mergeRow.DeviceId);
                Assert.Equal(sourcePoint.Id, mergeRow.BeforePointId);
                Assert.Equal(sourcePoint.Id, mergeRow.AfterPointId);
            }
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connStr);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static MobileLocationCorrectionService CreateCorrectionService(PimDbContext db)
    {
        var user = MobileTestHelpers.CurrentUser(UserId);
        var time = MobileTestHelpers.Time(Now);
        var query = new MobileLocationQueryService(time);
        var aggregation = new MobileLocationAggregationService(db, user, query, time);
        var frequentPlaces = new MobileFrequentPlaceService(db, user, query);
        var recalculation = new MobileLocationRecalculationService(aggregation, frequentPlaces, time);
        var cache = new MemoryAggregateResultCache(new MemoryCache(new MemoryCacheOptions()), time);
        return new MobileLocationCorrectionService(
            db,
            user,
            query,
            aggregation,
            recalculation,
            new AuditLogService(db),
            cache,
            time);
    }

    private sealed class PauseAfterDuplicateLocationSnapshotInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _triggered;

        public Task Paused => _paused.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _triggered) == 0
                && IsDuplicateLocationSnapshotQuery(command.CommandText)
                && Interlocked.CompareExchange(ref _triggered, 1, 0) == 0)
            {
                _paused.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        /// <summary>合并中的重复自然键读取是唯一把 mobile_location_points 自连接的 SELECT。</summary>
        private static bool IsDuplicateLocationSnapshotQuery(string sql)
            => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase)
                && CountOccurrences(sql, "mobile_location_points") >= 2;

        private static int CountOccurrences(string value, string needle)
        {
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                offset += needle.Length;
            }

            return count;
        }
    }

    private static async Task CreateSchemaAsync(
        NpgsqlConnection conn,
        string schema,
        params string[] extraTables)
    {
        var tables = DeviceScopedTables
            .Concat(extraTables)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // 先显式确认目标库里有可借用的表结构；「缺表」说明这台机器没有可用的真库数据
        // （例如 CI），按 Skip 处理；其余异常（权限、磁盘、SQL 形状错误）必须让用例失败。
        await using (var probe = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relname = ANY(@tables)", conn))
        {
            probe.Parameters.AddWithValue("tables", tables);
            var found = Convert.ToInt64(await probe.ExecuteScalarAsync() ?? 0L);
            Skip.If(found < tables.Length,
                "RealDb 缺少移动端表结构（未指向已迁移的库），跳过测试。");
        }

        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", conn))
            await create.ExecuteNonQueryAsync();

        // 借用 pim 库里已存在的表结构，测试库不写 public。
        foreach (var table in tables)
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

            // 工单 02 C1：心跳与端点通道在真实数据上也不能留下源设备的残留行。
            // 期望值按合并前的真实数据推导（`>0 ? 1 : 0`），而不是写死 `<= 1` ——
            // 后者在"镜像里本来就没有心跳数据"时对 0 行恒真，属于没有鉴别力的假绿。
            var heartbeatsBefore = await db.Set<DaemonHeartbeatEntity>()
                .Where(h => h.DeviceId == target.DeviceId || sourceDeviceIds.Contains(h.DeviceId))
                .CountAsync();
            var endpointStatusesBefore = await db.Set<EndpointStatusEntity>()
                .Where(s => s.DeviceId == target.DeviceId || sourceDeviceIds.Contains(s.DeviceId))
                .CountAsync();

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

            // 源设备侧 0 行；目标设备恰好剩一行（android 心跳/端点在库里最多只有一行；
            // 没有任何相关数据时是 0 行 —— 两种期望都由合并前的真实计数推导出来）。
            Assert.Empty(await db.Set<DaemonHeartbeatEntity>()
                .Where(h => sourceDeviceIds.Contains(h.DeviceId)).ToListAsync());
            Assert.Equal(
                heartbeatsBefore > 0 ? 1 : 0,
                await db.Set<DaemonHeartbeatEntity>().CountAsync(h => h.DeviceId == target.DeviceId));
            Assert.Empty(await db.Set<EndpointStatusEntity>()
                .Where(s => sourceDeviceIds.Contains(s.DeviceId)).ToListAsync());
            Assert.Equal(
                endpointStatusesBefore > 0 ? 1 : 0,
                await db.Set<EndpointStatusEntity>().CountAsync(s => s.DeviceId == target.DeviceId));
            Assert.Empty(await db.Set<EndpointNotificationActionEntity>()
                .Where(a => sourceDeviceIds.Contains(a.DeviceId)).ToListAsync());

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
            // daemon_heartbeats 没有 user_id：只能借该用户名下的 device_id 集合筛选。
            var sql = DeviceIdOnlyTables.Contains(table)
                ? $"INSERT INTO \"{schema}\".\"{table}\" SELECT * FROM public.\"{table}\" " +
                  "WHERE device_id IN (SELECT device_id FROM public.mobile_devices WHERE user_id = @userId)"
                : $"INSERT INTO \"{schema}\".\"{table}\" SELECT * FROM public.\"{table}\" WHERE user_id = @userId";

            await using var copy = new NpgsqlCommand(sql, conn);
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
