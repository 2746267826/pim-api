using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar;
using Pim.Module.Files;
using Pim.Module.Mcp;
using Pim.Module.Mobile;
using Pim.Module.PcTracker;
using Pim.Module.QuickNotes;
using Pim.Module.Stats;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 快照同步护栏（issue #320）：断言 <c>PimDbContextModelSnapshot</c> 与当前 EF 模型<b>零差异</b>。
///
/// <para>
/// <c>dotnet ef migrations add</c> 正是用同一份 diff（快照模型 vs 当前模型）决定新迁移的内容；
/// diff 非空就意味着这些操作会被「吸收」进下一条迁移。#320 的漂移形态正是：实体/运行时
/// initializer 已有 8 列 + 4 表（归 <c>PcTrackerSchemaInitializer</c> 幂等维护），快照却没有 ——
/// 于是每个新生成的迁移都会把它们卷进来，照单吸收会让存量库在启动迁移时撞
/// 42701（列已存在）/ 42P07（表已存在）→ 进程退出（#271 同款事故）。
/// </para>
///
/// <para>
/// 模型构建必须先注册与生产/dotnet-ef design-time 相同的模块程序集
/// （<see cref="PimDbContext.RegisterModuleAssembly"/>，见各 <c>*Module.RegisterServices</c>），
/// 否则模型缺整个模块面，diff 全是噪音。
/// </para>
/// </summary>
public sealed class MigrationSnapshotSyncTests
{
    /// <summary>#319/#321 两次实测被卷入的 8 个漂移列。</summary>
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

    /// <summary>#321 实测被卷入的 4 张整表。</summary>
    private static readonly string[] DriftTables =
    [
        "pc_browser_site_daily",
        "pc_browser_site_tick",
        "pc_browser_site_meta",
        "pc_suggestion_feedback",
    ];

    [Fact]
    public void ModelSnapshot_IsInSyncWithCurrentModel()
    {
        using var db = CreateDbContext();

        var snapshotModel = GetSnapshotModel(db);

        // 目标侧用 design-time 模型：differ 需要读 Collation 等仅存在于 design-time 模型的配置
        //（dotnet ef 的 MigrationsOperations 也是拿 IDesignTimeModel 来差分的）。
        var targetModel = db.GetService<IDesignTimeModel>().Model;

        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), targetModel.GetRelationalModel());

        Assert.True(
            operations.Count == 0,
            "EF 模型快照（PimDbContextModelSnapshot）与当前模型存在差异 —— 下一条 dotnet ef migrations add"
            + " 会把这些操作吸收进新迁移（issue #320 的污染形态）。若是新实体变更，请同步生成快照；"
            + "若对象归运行时 PcTrackerSchemaInitializer 所有，请走「空迁移 + 保留 Designer/快照」先例"
            + "（20260906132048 / issue #320）："
            + Environment.NewLine + string.Join(Environment.NewLine, operations.Select(Describe)));
    }

    /// <summary>
    /// #320 的定点回归：12 个「实体有、快照无」的漂移对象必须已进快照。
    /// 独立于整体 diff 断言，让失败信息直接指向 issue 里的对象清单。
    /// </summary>
    [Fact]
    public void ModelSnapshot_ContainsRuntimeInitializedPcTrackerObjects()
    {
        using var db = CreateDbContext();

        var snapshotModel = GetSnapshotModel(db);

        var missing = new List<string>();
        missing.AddRange(DriftColumns
            .Where(c => !HasColumn(snapshotModel!, c.Table, c.Column))
            .Select(c => $"列 {c.Table}.{c.Column}"));
        missing.AddRange(DriftTables
            .Where(t => !HasTable(snapshotModel!, t))
            .Select(t => $"表 {t}"));

        Assert.True(
            missing.Count == 0,
            "以下 PcTracker 对象存在于实体/运行时 initializer，但 EF 模型快照里没有"
            + "（issue #320：新迁移生成会被吸收，照单吸收会让存量库启动迁移崩溃）："
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// 反向自检：HasColumn/HasTable 的读法必须真的能找到已知对象，否则上面的断言
    /// 会因为「查不到任何东西」而静默变红/变绿（与 MigrationGuardedDdlTests 的扫描面自检同理）。
    /// </summary>
    [Fact]
    public void SnapshotModelLookup_FindKnownObjects()
    {
        using var db = CreateDbContext();

        var snapshotModel = GetSnapshotModel(db);

        Assert.True(HasTable(snapshotModel, "pc_tracker_health"));
        Assert.True(HasColumn(snapshotModel, "pc_tracker_health", "device_id"));
        Assert.True(HasTable(snapshotModel, "pc_activity_classifications"));
        Assert.True(HasColumn(snapshotModel, "pc_activity_classifications", "category_name"));
    }

    /// <summary>
    /// 构建快照模型：与 dotnet ef 的 MigrationsOperations 相同路径 —— 先取 ModelSnapshot.Model，
    /// 再经 <see cref="IModelRuntimeInitializer.Initialize"/> 初始化（未初始化的模型不能
    /// GetRelationalModel()，差分也无从谈起）。
    /// </summary>
    private static IModel GetSnapshotModel(PimDbContext db)
    {
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        return db.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot!.Model, designTime: true);
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FilesModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(McpModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(MobileModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(PcTrackerModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(QuickNotesModule).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(StatsModule).Assembly);

        // 连接串不会真的被使用：模型与 diff 都在内存里构建，不访问数据库。
        return new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=unused;Username=unused;Password=unused")
            .Options);
    }

    private static bool HasTable(IModel model, string table) =>
        model.GetRelationalModel().FindTable(table, null) is not null;

    private static bool HasColumn(IModel model, string table, string column) =>
        model.GetRelationalModel().FindTable(table, null)?
            .Columns.Any(c => c.Name == column) == true;

    /// <summary>把 diff 操作翻译成人话，让测试失败信息能直接定位漂移对象。</summary>
    private static string Describe(MigrationOperation operation) => operation switch
    {
        AddColumnOperation addColumn => $"AddColumn {addColumn.Table}.{addColumn.Name}",
        AlterColumnOperation alterColumn => $"AlterColumn {alterColumn.Table}.{alterColumn.Name}",
        DropColumnOperation dropColumn => $"DropColumn {dropColumn.Table}.{dropColumn.Name}",
        CreateTableOperation createTable => $"CreateTable {createTable.Name}",
        DropTableOperation dropTable => $"DropTable {dropTable.Name}",
        CreateIndexOperation createIndex => $"CreateIndex {createIndex.Table} ({string.Join(", ", createIndex.Columns)})",
        DropIndexOperation dropIndex => $"DropIndex {dropIndex.Table} {dropIndex.Name}",
        AddForeignKeyOperation addForeignKey => $"AddForeignKey {addForeignKey.Table} -> {addForeignKey.PrincipalTable}",
        AddPrimaryKeyOperation addPrimaryKey => $"AddPrimaryKey {addPrimaryKey.Table}",
        _ => operation.GetType().Name,
    };
}
