using System;
using System.Threading.Tasks;
using Npgsql;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 验证 ux_tracker_events_dedup 的 NULL 语义（3.5 缺陷修复）。
/// PostgreSQL 中 NULL 彼此不等，若唯一索引直接含可空列，老客户端(NULL browser/instance_id)
/// 跨请求重复上报会绕过去重。修复采用 COALESCE 表达式索引。
/// 该测试在独立的临时 schema 中建表建索引，验证语义后即清理；连不上库时 Skip。
/// </summary>
public sealed class PcTrackerDedupRealDbTests
{
    private const string ConnStr = "Host=127.0.0.1;Database=pim;Username=opencode;Password=62f0a50bb963bb648f8e400399def95a;CommandTimeout=30";

    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task DedupIndex_Coalesce_PreservesNullDedupSemantics()
    {
        await using var conn = new NpgsqlConnection(ConnStr);
        try { await conn.OpenAsync(); }
        catch (Exception ex)
        {
            throw new SkipException($"RealDb unavailable, skipping test: {ex.Message}");
        }

        var schema = $"test_tracker_dedup_{Guid.NewGuid():N}";
        try
        {
            await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn))
                await cmd.ExecuteNonQueryAsync();

            var sql = PcTrackerSchemaInitializer.SchemaSql;
            var createTable = ExtractCreateTable(sql);
            var createIndex = ExtractCreateIndex(sql);

            await using (var cmd = new NpgsqlCommand($"SET search_path TO {schema}", conn))
                await cmd.ExecuteNonQueryAsync();

            await using (var cmd = new NpgsqlCommand(createTable, conn))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand(createIndex, conn))
                await cmd.ExecuteNonQueryAsync();

            // 1) Old format: browser/instance_id NULL duplicate must be blocked.
            await Insert(conn, "dev-null", null, null);
            await AssertDuplicateBlockedAsync(conn, "dev-null", null, null);

            // 2) Same instance id duplicate must be blocked.
            await Insert(conn, "dev-same", "chrome", "ext_123");
            await AssertDuplicateBlockedAsync(conn, "dev-same", "chrome", "ext_123");

            // 3) Different instance ids must coexist.
            await Insert(conn, "dev-diff", "chrome", "ext_1");
            await Insert(conn, "dev-diff", "chrome", "ext_2");

            // 4) NULL row and browser/instance_id row are distinct (no false dedup).
            await Insert(conn, "dev-mixed", null, null);
            await Insert(conn, "dev-mixed", "edge", "ext_9");
        }
        finally
        {
            await using (var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", conn))
                await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task Insert(NpgsqlConnection conn, string deviceId, string? browser, string? instanceId)
    {
        const string insert = """
            INSERT INTO pc_tracker_events
                (device_id, timestamp, duration, event_type, app_name, browser, instance_id, date)
            VALUES
                (@device, '2026-08-20T06:30:00+00:00', 10, 'window', 'App', @browser, @instance, '2026-08-20')
            """;
        await using var cmd = new NpgsqlCommand(insert, conn);
        cmd.Parameters.AddWithValue("device", deviceId);
        cmd.Parameters.AddWithValue("browser", (object?)browser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("instance", (object?)instanceId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AssertDuplicateBlockedAsync(NpgsqlConnection conn, string deviceId, string? browser, string? instanceId)
    {
        var insert = """
            INSERT INTO pc_tracker_events
                (device_id, timestamp, duration, event_type, app_name, browser, instance_id, date)
            VALUES
                (@device, '2026-08-20T06:30:00+00:00', 10, 'window', 'App', @browser, @instance, '2026-08-20')
            """;
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand(insert, conn);
            cmd.Parameters.AddWithValue("device", deviceId);
            cmd.Parameters.AddWithValue("browser", (object?)browser ?? DBNull.Value);
            cmd.Parameters.AddWithValue("instance", (object?)instanceId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("23505", ex.SqlState);
    }

    private static string ExtractCreateTable(string sql)
    {
        var start = sql.IndexOf("CREATE TABLE IF NOT EXISTS pc_tracker_events", StringComparison.Ordinal);
        Assert.True(start >= 0, "Schema SQL must define pc_tracker_events");
        var end = sql.IndexOf(");", start, StringComparison.Ordinal);
        var table = sql.Substring(start, end - start + 2);
        // Trim the trailing columns so the scratch table stays minimal but valid.
        return table;
    }

    private static string ExtractCreateIndex(string sql)
    {
        var needle = "CREATE UNIQUE INDEX IF NOT EXISTS ux_tracker_events_dedup";
        var start = sql.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(start >= 0, "Schema SQL must define ux_tracker_events_dedup with COALESCE");
        var end = sql.IndexOf(';', start);
        var index = sql.Substring(start, end - start + 1);
        Assert.Contains("COALESCE(browser,'')", index);
        Assert.Contains("COALESCE(instance_id,'')", index);
        return index;
    }
}