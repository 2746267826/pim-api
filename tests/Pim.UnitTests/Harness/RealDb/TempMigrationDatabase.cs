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

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 一次性临时库 + 真实迁移链回放（用 <c>MigrateAsync</c> 而非 EnsureCreated），
/// 供 InfrastructureCoverage 各 issue 的真库回归共用（#271、#320…）。
///
/// <para>
/// 每次运行在 <c>PIM_TEST_CONN</c> 指向的服务器上建一个 <c>{前缀}_{GUID}</c> 库，
/// 结束时 <c>DROP DATABASE ... WITH (FORCE)</c> 删掉；生产/镜像库永远不会被触碰。
/// </para>
/// </summary>
internal sealed class TempMigrationDatabase : IAsyncDisposable
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

    /// <summary>
    /// 无 PostgreSQL（未设置 PIM_TEST_CONN 或连不上）时抛 SkipException 跳过。
    ///
    /// <para>
    /// 前置条件：<c>PIM_TEST_CONN</c> 指向的账号需要 <c>CREATEDB</c> 权限（本用例会在每次运行时
    /// 建一个一次性库并在结束时删掉），且服务端为 PostgreSQL 13+（清理用
    /// <c>DROP DATABASE ... WITH (FORCE)</c>）。条件不满足时用例会失败而不是静默通过，
    /// 以免把环境问题伪装成绿色。
    /// </para>
    /// </summary>
    public static async Task<TempMigrationDatabase> CreateAsync(string databaseNamePrefix)
    {
        var connStr = RealDbTestConnection.Require();

        var admin = new NpgsqlConnection(connStr);
        await admin.OpenAsync();

        var database = $"{databaseNamePrefix}_{Guid.NewGuid():N}";
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

    /// <summary>执行任意 SQL（用于构造缺表等异常库状态）。</summary>
    public Task ExecuteAsync(string sql) => _db.Database.ExecuteSqlRawAsync(sql);

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

    /// <summary>按名称后缀查历史（迁移 ID 前缀是生成时间戳，后缀才是迁移名）。</summary>
    public async Task<bool> HasMigrationLikeAsync(string migrationNameSuffix)
    {
        if (!await TableExistsAsync("__EFMigrationsHistory"))
        {
            return false;
        }

        var count = await ScalarAsync(
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%' || @name",
            ("name", migrationNameSuffix));
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
