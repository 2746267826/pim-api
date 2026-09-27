using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// issue #349 的真库回归（<c>Trait DataSource=RealDb</c>）：S7「断档必须在时间轴上被标记」
/// 的取数 SQL 必须在**极小 duration（科学计数法形态）**的行上也能执行。
///
/// <para>
/// 缺陷根因：取数用字符串拼接 interval —— <c>timestamp + (duration || ' seconds')::interval</c>。
/// PostgreSQL 把极小的 float8 转 text 时输出科学计数法（<c>7.9e-05</c>），拼成
/// <c>'7.9e-05 seconds'</c> 后 interval 解析器不接受 → 整条查询报 <c>22007</c> →
/// S7 恒为 <c>unknown</c>，整条尺子不可用。
/// </para>
///
/// <para>
/// 本用例在一次性库上重放真实迁移链 + 运行时 initializer，写入真实的生产 duration 形态，
/// 再跑一次完整体检，断言 S7 **不因取数异常退化为 unknown**。
/// </para>
///
/// <para>
/// 只断言"取数跑通"，不断言 S7 的最终红/绿结论 —— 那取决于业务数据形状，
/// 与 #349 无关（#349 的验收就是"不再 unknown / 不再 22007"）。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DataReliabilityS7RealDbTests
{
    /// <summary>插入一行 pc_tracker_events（参数化，避免 raw_json 的花括号被当成格式占位符）。</summary>
    private static async Task InsertEventAsync(
        TempMigrationDatabase database,
        string deviceId,
        DateTimeOffset timestamp,
        double duration,
        string eventType)
    {
        var connection = (NpgsqlConnection)database.Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pc_tracker_events
                (device_id, timestamp, duration, event_type, app_name, display_name, window_title,
                 raw_json, is_idle, is_media_active, page_visit_count, date)
            VALUES (@device, @ts, @duration, @type, 'Code.exe', 'Code', 'probe',
                    '{}'::jsonb, false, false, 0, (@ts AT TIME ZONE 'Asia/Shanghai')::date);
            """,
            connection);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("ts", timestamp);
        command.Parameters.AddWithValue("duration", duration);
        command.Parameters.AddWithValue("type", eventType);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetS7DetailAsync(TempMigrationDatabase database)
    {
        var inspector = new DataReliabilityQualityInspector(
            database.Db,
            Options.Create(new InvariantOptions()),
            NullLogger<DataReliabilityQualityInspector>.Instance);

        var report = await inspector.InspectReportAsync(DateTimeOffset.UtcNow);
        var s7 = Assert.Single(report.Rules, rule => rule.Code == "S7");
        return $"{s7.Status}|{s7.Detail}";
    }

    [SkippableFact]
    public async Task S7Fetch_WithScientificNotationDuration_DoesNotDegradeToUnknown()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_issue_349");
        await database.MigrateAsync();
        await database.RunPcTrackerSchemaInitializerAsync();

        // 前置条件：确认 PostgreSQL 确实以科学计数法渲染这个量级的 float8。
        // 若将来改成定点输出，本用例就失去复现能力，应当失败提醒而不是静默变绿。
        var connection = (NpgsqlConnection)database.Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var probe = new NpgsqlCommand("SELECT (7.9e-05::float8)::text", connection))
        {
            var rendered = (string)(await probe.ExecuteScalarAsync() ?? string.Empty);
            Assert.Contains("e", rendered, StringComparison.OrdinalIgnoreCase);
        }

        // issue #349 生产实测的两行：5.19e-05 与 7.9e-05 秒（window 类型）。
        await InsertEventAsync(database, "device-1", new DateTimeOffset(2026, 9, 23, 4, 33, 32, TimeSpan.Zero), 5.19e-05, "window");
        await InsertEventAsync(database, "device-1", new DateTimeOffset(2026, 9, 23, 10, 23, 59, TimeSpan.Zero), 7.9e-05, "window");

        var detail = await GetS7DetailAsync(database);

        // #349 的验收：不得再因取数崩溃而 unknown。
        Assert.DoesNotContain("取数执行异常", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("取数超时", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("22007", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid input syntax for type interval", detail, StringComparison.Ordinal);
        Assert.False(detail.StartsWith("unknown|", StringComparison.Ordinal), $"S7 不应退化为 unknown：{detail}");
    }

    /// <summary>
    /// 对照用例：非科学计数法的亚毫秒 duration（issue #349 里"当前不触发"的那一类，
    /// 如 <c>0.0008477</c>）同样必须能跑通，确保修复没有把正常形态弄坏。
    /// </summary>
    [SkippableFact]
    public async Task S7Fetch_WithPlainSubMillisecondDuration_StillExecutes()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_issue_349_plain");
        await database.MigrateAsync();
        await database.RunPcTrackerSchemaInitializerAsync();

        await InsertEventAsync(database, "device-1", new DateTimeOffset(2026, 9, 23, 4, 33, 32, TimeSpan.Zero), 0.0008477, "window");

        var detail = await GetS7DetailAsync(database);

        Assert.DoesNotContain("取数执行异常", detail, StringComparison.Ordinal);
        Assert.False(detail.StartsWith("unknown|", StringComparison.Ordinal), $"S7 不应退化为 unknown：{detail}");
    }
}
