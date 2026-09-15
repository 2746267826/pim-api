using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// issue #271 的真库复现与回归（Trait DataSource=RealDb）：在一次性数据库里重放<b>真实迁移链</b>，
/// 覆盖升级路径上会让 API「启动即退出」的两种库状态。
///
/// <para>
/// 事故根因是「运行时 <c>PcTrackerSchemaInitializer</c> 与 EF 迁移同时维护同一批对象」：
/// <c>pc_tracker_events</c> 及其 browser/instance_id 列、两个索引都由运行时幂等 SQL 维护、
/// 不在 EF 模型里，但迁移 <c>20260906132048_AddDaemonHeartbeatsUniqueIndex</c> 却对它们执行了
/// 非幂等的 <c>AddColumn</c>/<c>CreateIndex</c>。于是：
/// </para>
/// <list type="number">
///   <item><description>
///     <b>存量库（真实生产形态）</b>：对象都已存在，但该迁移从未写入 <c>__EFMigrationsHistory</c>；
///     <c>AddColumn</c> 撞 <c>42701 列已存在</c> → 启动迁移失败 → 进程退出 → supervisord 反复拉起。
///   </description></item>
///   <item><description>
///     <b>全新空库</b>：没有任何迁移 <c>CreateTable</c> 过 <c>pc_tracker_events</c>（该表归运行时
///     initializer 所有，且它在迁移之后才执行），<c>AddColumn</c> 撞 <c>42P01 关系不存在</c>，
///     同样中断整条迁移链。
///   </description></item>
/// </list>
///
/// <para>修复后两种状态都必须：迁移链跑到底、写入迁移历史、且目标 schema 终态正确。</para>
///
/// <para>无 PostgreSQL（未设置 <c>PIM_TEST_CONN</c> 或连不上）时显式 Skip，报告里显示 Skipped。</para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class Migration271RealDbTests
{
    private const string FailingMigrationId = "20260906132048_AddDaemonHeartbeatsUniqueIndex";

    /// <summary>#271 之前最后一条已应用迁移（真实生产库的历史就停在这里）。</summary>
    private const string LastAppliedBeforeFailing = "20260901012419_AddMcpClients";

    private const string TrackerTable = "pc_tracker_events";

    /// <summary>
    /// 存量库升级：#271 的真实生产形态 —— 「对象已由运行时建好、迁移未写入历史」。
    /// 修复前这里必抛 <c>42701 column "browser" ... already exists</c>。
    /// </summary>
    [SkippableFact]
    public async Task Migration_OnExistingDatabaseWithRuntimeInitializedTrackerSchema_CompletesChain()
    {
        await using var database = await TempMigrationDatabase.CreateAsync();

        // 1) 重建 v510 存量库：迁移历史停在 #271 之前。
        await database.MigrateToAsync(LastAppliedBeforeFailing);

        // 2) 运行时 PcTrackerSchemaInitializer 已把这批对象建好（幂等 SQL，走真实生产调用路径）。
        await database.RunPcTrackerSchemaInitializerAsync();

        // 前置断言：确认这就是 #271 描述的「对象已存在、迁移未应用」状态。
        Assert.True(await database.ColumnExistsAsync(TrackerTable, "browser"));
        Assert.True(await database.ColumnExistsAsync(TrackerTable, "instance_id"));
        Assert.True(await database.IndexExistsAsync("idx_tracker_events_browser"));
        Assert.True(await database.IndexExistsAsync("idx_tracker_events_instance"));
        Assert.True(await database.IsUniqueIndexAsync("IX_daemon_heartbeats_device_id_daemon_kind"));
        Assert.False(await database.HasMigrationAsync(FailingMigrationId));

        // 3) 重放整条迁移链 —— 修复前在 ADD COLUMN browser 处抛 42701。
        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        // 4) 迁移历史补齐（含 #271 之后的 3 条 mobile 迁移）。
        Assert.True(await database.HasMigrationAsync(FailingMigrationId));

        // 5) 终态：对象一个不少，且唯一索引仍然是唯一索引。
        Assert.True(await database.ColumnExistsAsync(TrackerTable, "browser"));
        Assert.True(await database.ColumnExistsAsync(TrackerTable, "instance_id"));
        Assert.True(await database.IndexExistsAsync("idx_tracker_events_browser"));
        Assert.True(await database.IndexExistsAsync("idx_tracker_events_instance"));
        Assert.True(await database.IsUniqueIndexAsync("IX_daemon_heartbeats_device_id_daemon_kind"));
    }

    /// <summary>
    /// 全新空库：没有迁移建过 <c>pc_tracker_events</c>，修复前这里抛 <c>42P01 关系不存在</c>。
    /// </summary>
    [SkippableFact]
    public async Task Migration_OnFreshDatabase_CompletesChainWithoutMissingRelation()
    {
        await using var database = await TempMigrationDatabase.CreateAsync();

        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        Assert.True(await database.HasMigrationAsync(FailingMigrationId));
        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());

        // 迁移阶段绝不代运行时 initializer 建表：全新库上该表此时仍不该存在。
        Assert.False(await database.TableExistsAsync(TrackerTable));

        // 随后运行时 initializer 正常把它建出来（含 browser/instance_id 与去重索引）。
        await database.RunPcTrackerSchemaInitializerAsync();
        Assert.True(await database.TableExistsAsync(TrackerTable));
        Assert.True(await database.ColumnExistsAsync(TrackerTable, "browser"));
        Assert.True(await database.IndexExistsAsync("ux_tracker_events_dedup"));

        // 再跑一次迁移：必须仍是 no-op（supervisord 反复拉起同一容器的真实路径）。
        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
    }

    /// <summary>
    /// 把整条链重放两遍（迁移 + initializer + 迁移）：任何一步都不该因为「对象已存在」而失败。
    /// </summary>
    [SkippableFact]
    public async Task MigrationAndInitializer_AreIdempotentWhenReplayedTwice()
    {
        await using var database = await TempMigrationDatabase.CreateAsync();

        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
        Assert.Null(await Record.ExceptionAsync(database.RunPcTrackerSchemaInitializerAsync));
        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
        Assert.Null(await Record.ExceptionAsync(database.RunPcTrackerSchemaInitializerAsync));

        Assert.True(await database.IndexExistsAsync("ux_tracker_events_dedup"));
        Assert.True(await database.IsUniqueIndexAsync("IX_daemon_heartbeats_device_id_daemon_kind"));
    }

    /// <summary>一次性临时库 + 真实迁移链（用 <c>MigrateAsync</c> 而非 EnsureCreated）。</summary>
    private sealed class TempMigrationDatabase : IAsyncDisposable
    {
        private readonly string _database;
        private readonly NpgsqlConnection _admin;
        private readonly PimDbContext _db;

        private TempMigrationDatabase(string database, NpgsqlConnection admin, PimDbContext db)
        {
            _database = database;
            _admin = admin;
            _db = db;
        }

        /// <summary>无 PostgreSQL（未设置 PIM_TEST_CONN 或连不上）时抛 SkipException 跳过。</summary>
        public static async Task<TempMigrationDatabase> CreateAsync()
        {
            var connStr = RealDbTestConnection.Require();

            var admin = new NpgsqlConnection(connStr);
            await admin.OpenAsync();

            var database = $"test_migration_271_{Guid.NewGuid():N}";
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            {
                await create.ExecuteNonQueryAsync();
            }

            var scoped = new NpgsqlConnectionStringBuilder(connStr) { Database = database }.ConnectionString;
            var options = new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(scoped).Options;
            return new TempMigrationDatabase(database, admin, new PimDbContext(options));
        }

        public Task MigrateAsync() => _db.Database.MigrateAsync();

        /// <summary>迁移到指定迁移（含），用来重建「该迁移之前」的历史起点。</summary>
        public Task MigrateToAsync(string migrationId) =>
            ((IInfrastructure<IServiceProvider>)_db).Instance.GetRequiredService<IMigrator>()
                .MigrateAsync(migrationId);

        /// <summary>走真实生产路径（<c>ExecuteSqlRawAsync</c>）执行运行时 schema initializer。</summary>
        public Task RunPcTrackerSchemaInitializerAsync() =>
            new Pim.Module.PcTracker.Services.PcTrackerSchemaInitializer(_db).InitializeAsync();

        public async Task<bool> HasMigrationAsync(string migrationId)
        {
            if (!await TableExistsAsync("__EFMigrationsHistory"))
            {
                return false;
            }

            var count = await ScalarAsync(
                "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = @id",
                ("id", migrationId));
            return count > 0;
        }

        public async Task<long> AppliedMigrationCountAsync()
        {
            if (!await TableExistsAsync("__EFMigrationsHistory"))
            {
                return 0;
            }

            return await ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\"");
        }

        /// <summary>磁盘上的迁移条数，用来断言「链跑到底」而不是停在中间。</summary>
        public Task<long> MigrationsOnDiskAsync()
        {
            var count = typeof(PimDbContext).Assembly.GetTypes()
                .Count(t => !t.IsAbstract && t.IsSubclassOf(typeof(Migration)));
            return Task.FromResult((long)count);
        }

        public async Task<bool> TableExistsAsync(string table)
        {
            var count = await ScalarAsync(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name=@t",
                ("t", table));
            return count > 0;
        }

        public async Task<bool> ColumnExistsAsync(string table, string column)
        {
            var count = await ScalarAsync(
                "SELECT count(*) FROM information_schema.columns "
                + "WHERE table_schema='public' AND table_name=@t AND column_name=@c",
                ("t", table), ("c", column));
            return count > 0;
        }

        public async Task<bool> IndexExistsAsync(string indexName)
        {
            var count = await ScalarAsync(
                "SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname=@i",
                ("i", indexName));
            return count > 0;
        }

        public async Task<bool> IsUniqueIndexAsync(string indexName)
        {
            var count = await ScalarAsync(
                "SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid "
                + "WHERE c.relname = @i AND i.indisunique",
                ("i", indexName));
            return count > 0;
        }

        private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            await using var cmd = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var scalar = await cmd.ExecuteScalarAsync();
            return scalar is null or DBNull ? 0 : Convert.ToInt64(scalar);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _admin))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }
}
