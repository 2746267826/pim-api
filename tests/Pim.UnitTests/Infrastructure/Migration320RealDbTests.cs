using System.Threading.Tasks;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// issue #320 的真库回归（Trait DataSource=RealDb）：实体/快照漂移的 12 个对象
/// （8 列 + 4 表）归运行时 <c>PcTrackerSchemaInitializer</c> 幂等维护，快照同步迁移必须是安全 no-op。
///
/// <para>
/// 复现真实存量库形态：历史停在同步迁移之前、initializer 已把对象建好 —— 若有人把
/// <c>dotnet ef migrations add</c> 生成的 12 个对象「照单吸收」进正式迁移（#319/#321 两次差点发生），
/// 这里的 <c>MigrateAsync</c> 会撞 42701（列已存在）/ 42P07（表已存在）→ 启动失败（#271 同款）。
/// 空迁移下则必须：链跑到底、对象保持 initializer 的形态、重复启动仍是 no-op。
/// </para>
///
/// <para>无 PostgreSQL（未设置 <c>PIM_TEST_CONN</c> 或连不上）时显式 Skip。</para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class Migration320RealDbTests
{
    /// <summary>快照同步迁移之前最后一条已应用迁移（真实存量库的历史起点）。</summary>
    private const string LastAppliedBeforeSync = "20260919092837_AddFileTextSnapshots";

    /// <summary>同步迁移的名称（历史行带时间戳前缀，按后缀匹配）。</summary>
    private const string SyncMigrationSuffix = "SyncPcTrackerModelSnapshot";

    /// <summary>initializer 的浏览器站点表（关键唯一索引归 initializer 所有，EF 不碰它）。</summary>
    private const string BrowserSiteUniqueIndex = "ux_pc_browser_site_daily";

    [SkippableFact]
    public async Task Migration_OnDatabaseWhereInitializerCreatedDriftObjects_IsSafeNoOp()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_migration_320");

        // 1) 历史停在同步迁移之前。
        await database.MigrateToAsync(LastAppliedBeforeSync);

        // 2) 运行时 PcTrackerSchemaInitializer 已把这批对象建好（幂等 SQL，真实生产调用路径）。
        await database.RunPcTrackerSchemaInitializerAsync();

        // 前置断言：确认这就是 #320 描述的「对象已存在、同步迁移未应用」状态。
        foreach (var (table, column) in DriftColumns)
        {
            Assert.True(
                await database.ColumnExistsAsync(table, column),
                $"前置：{table}.{column} 应已由运行时 initializer 建好");
        }

        foreach (var table in DriftTables)
        {
            Assert.True(await database.TableExistsAsync(table), $"前置：表 {table} 应已存在");
        }

        Assert.False(await database.HasMigrationLikeAsync(SyncMigrationSuffix));

        // 3) 重放整条迁移链 —— 吸收版迁移在这里抛 42701/42P07，空迁移必须无事发生。
        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        // 4) 链跑到底，且包含快照同步迁移。
        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
        Assert.True(await database.HasMigrationLikeAsync(SyncMigrationSuffix));

        // 5) 终态：12 个对象仍在（形态归 initializer 所有，迁移没有动它们）。
        foreach (var (table, column) in DriftColumns)
        {
            Assert.True(await database.ColumnExistsAsync(table, column), $"{table}.{column} 不应被迁移删除");
        }

        foreach (var table in DriftTables)
        {
            Assert.True(await database.TableExistsAsync(table), $"表 {table} 不应被迁移删除");
        }

        Assert.True(await database.IndexExistsAsync(BrowserSiteUniqueIndex));

        // 6) 再跑一遍迁移（supervisord 反复拉起同一容器的真实路径）：仍是 no-op。
        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
    }

    private static readonly (string Table, string Column)[] DriftColumns =
    [
        ("pc_tracker_health", "site_connected"),
        ("pc_tracker_health", "site_last_event_age_seconds"),
        ("pc_tracker_health", "site_events_uploaded"),
        ("pc_tracker_health", "site_last_error"),
        ("pc_activity_classifications", "app_name"),
        ("pc_activity_classifications", "app_display_name"),
        ("pc_activity_classifications", "window_title"),
        ("pc_activity_classification_settings", "daily_productive_hours_goal"),
    ];

    private static readonly string[] DriftTables =
    [
        "pc_browser_site_daily",
        "pc_browser_site_tick",
        "pc_browser_site_meta",
        "pc_suggestion_feedback",
    ];
}
