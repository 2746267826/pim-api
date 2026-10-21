using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Infrastructure.Data;
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

    /// <summary>
    /// 生产模块清单 = <c>Pim.Api</c> 的程序集引用 ∪ 输出目录里的 <c>Pim.Module.*.dll</c>，
    /// 避免手工镜像清单在新模块接入后失真（失真 = 新模块的漂移被静默过滤）。
    ///
    /// <para>
    /// 只按 <c>Pim.Api</c> 的 AssemblyRef 推导是不够的：<c>Mobile</c> / <c>QuickNotes</c>
    /// 在 <c>Pim.Api.csproj</c> 里有 ProjectReference，但 Pim.Api 源码没有直接引用它们的类型，
    /// 编译产物里就没有对应的 AssemblyRef。实测只按引用推导只有 4 个模块，15 张 <c>mobile_*</c>
    /// 表与 2 张 <c>quick_note*</c> 表会被 <see cref="InProductionScope"/> 静默排除。
    /// 扫描输出目录与生产 <c>ModuleRegistry</c> 的口径一致。
    /// </para>
    /// </summary>
    private static readonly string[] ProductionModules = DiscoverProductionModules();

    private static string[] DiscoverProductionModules()
    {
        var names = typeof(Pim.Api.ModuleRegistry).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("Pim.Module.", StringComparison.Ordinal))
            .ToList();

        names.AddRange(Directory
            .GetFiles(AppContext.BaseDirectory, "Pim.Module.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => name.StartsWith("Pim.Module.", StringComparison.Ordinal)));

        return names
            .Select(name => name["Pim.Module.".Length..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 定点回归：快照护栏必须覆盖**随 API 一起发布的所有模块**，而不只是 Pim.Api 源码
    /// 直接引用类型的那几个。Mobile / QuickNotes 没有直接 AssemblyRef，只按引用推导时
    /// 它们的表会被静默排除（review 发现）。
    /// </summary>
    [Fact]
    public void ProductionModules_CoversEveryModuleShippedWithTheApi()
    {
        var onDisk = Directory
            .GetFiles(AppContext.BaseDirectory, "Pim.Module.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Select(name => name["Pim.Module.".Length..])
            .ToArray();

        Assert.NotEmpty(onDisk);
        Assert.All(onDisk, module => Assert.Contains(module, ProductionModules));
        Assert.Contains("Mobile", ProductionModules);
        Assert.Contains("QuickNotes", ProductionModules);
    }

    [Fact]
    public void ModelSnapshot_IsInSyncWithCurrentModel()
    {
        using var db = CreateDbContext();

        var snapshotModel = GetSnapshotModel(db);

        // 目标侧用 design-time 模型：differ 需要读 Collation 等仅存在于 design-time 模型的配置
        //（dotnet ef 的 MigrationsOperations 也是拿 IDesignTimeModel 来差分的）。
        var targetModel = db.GetService<IDesignTimeModel>().Model;

        var snapshotTables = ProductionTableNames(snapshotModel);
        var targetTables = ProductionTableNames(targetModel);

        // 哨兵：目标模型必须真的包含全部生产模块的实体。模型缓存按
        // ModuleAssemblySignature 隔离、模块列表只增不减，正常情况下恒成立；
        // 若因任何原因拿到「部分加载」的陈旧模型，这里大声失败，
        // 而不是让它以一条莫名的 diff（如幽灵 DropForeignKey）出现。
        var targetNamespaces = targetModel.GetEntityTypes()
            .Select(e => e.ClrType.Namespace ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var missingModules = ProductionModules
            .Where(m => !targetNamespaces.Contains($"Pim.Module.{m}"))
            .Where(m => !targetNamespaces.Any(ns => ns.StartsWith($"Pim.Module.{m}.", StringComparison.Ordinal)))
            .ToList();
        Assert.True(
            missingModules.Count == 0,
            "目标模型缺少生产模块的实体：" + string.Join(", ", missingModules)
            + "（模型缓存或模块注册异常，diff 断言不可信）");

        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), targetModel.GetRelationalModel())
            .Where(op => InProductionScope(op, snapshotTables, targetTables))
            .ToList();

        Assert.True(
            operations.Count == 0,
            "EF 模型快照（PimDbContextModelSnapshot）与当前模型存在差异 —— 下一条 dotnet ef migrations add"
            + " 会把这些操作吸收进新迁移（issue #320 的污染形态）。若是新实体变更，请同步生成快照；"
            + "若对象归运行时 PcTrackerSchemaInitializer 所有，请走「空迁移 + 保留 Designer/快照」先例"
            + "（20260906132048 / issue #320）："
            + Environment.NewLine + string.Join(Environment.NewLine, operations.Select(Describe)));
    }

    /// <summary>
    /// <see cref="InProductionScope"/> 的定点回归：**不依赖**同进程里是否恰好有其他测试注册了
    /// 非生产实体（那取决于 xUnit collection 的执行顺序）。用合成的迁移操作直接断言：
    /// 生产表保留、金丝雀表过滤、识别不了的操作类型按范围内（fail-loud）。
    /// 这条用例保证「过滤」这一环本身不会静默失效（例如被改成恒 true）。
    /// </summary>
    [Fact]
    public void InProductionScope_FiltersKnownCanaryAndKeepsProductionTables()
    {
        // 两侧故意不完全相同：只出现在目标模型的表（新实体、快照未更新）与只出现在快照里的表
        // （实体已删、快照未更新）都必须纳入 —— 这正是 issue #320 的 fail-loud 方向。
        var snapshotTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "calendar_tasks", "snapshot_only_table",
        };
        var targetTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "calendar_tasks", "target_only_table",
        };

        Assert.True(InProductionScope(new CreateTableOperation { Name = "calendar_tasks" }, snapshotTables, targetTables));
        Assert.True(InProductionScope(new CreateTableOperation { Name = "target_only_table" }, snapshotTables, targetTables));
        Assert.True(InProductionScope(new DropTableOperation { Name = "snapshot_only_table" }, snapshotTables, targetTables));
        Assert.False(InProductionScope(new CreateTableOperation { Name = "model_cache_canaries" }, snapshotTables, targetTables));
        Assert.False(InProductionScope(new DropTableOperation { Name = "model_cache_canaries" }, snapshotTables, targetTables));
        Assert.False(InProductionScope(
            new AddColumnOperation { Table = "model_cache_canaries", Name = "value" }, snapshotTables, targetTables));
        // 未知操作类型：宁可误报也不漏报
        Assert.True(InProductionScope(new SqlOperation { Sql = "select 1" }, snapshotTables, targetTables));
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
        // 生产范围推导自 Pim.Api 的程序集引用：推导失效（空集）会让 diff 护栏静默放行，
        // 必须在这里大声失败。
        Assert.NotEmpty(ProductionModules);
        Assert.Contains("PcTracker", ProductionModules);

        using var db = CreateDbContext();

        var snapshotModel = GetSnapshotModel(db);

        Assert.True(HasTable(snapshotModel, "pc_tracker_health"));
        Assert.True(HasColumn(snapshotModel, "pc_tracker_health", "device_id"));
        Assert.True(HasTable(snapshotModel, "pc_activity_classifications"));
        Assert.True(HasColumn(snapshotModel, "pc_activity_classifications", "category_name"));
    }

    /// <summary>
    /// 迁移 Designer 的同步护栏（工单 05 review 的 Important 项）：**最后一条迁移**的
    /// <c>BuildTargetModel</c>（即 <see cref="Migration.TargetModel"/>）必须与当前模型零差异。
    ///
    /// <para><b>为什么要单独盯 Designer，而不是只看快照</b>：<c>dotnet ef migrations remove</c>
    /// 用的是「上一条迁移的 Designer」而不是快照来重建快照。若最后一条迁移的 Designer 落后于实体
    /// （工单 05 实测：Designer 缺 <c>calendars.default_muted</c> / <c>events.is_muted</c>，快照里却有），
    /// remove 会**静默把这两列从快照里抹掉**，紧接着 <c>migrations add</c> 又会把它们当作新列
    /// 写进下一条迁移 → 存量库启动迁移撞 42701（列已存在）。与 #320 同款事故，只是入口换成了 Designer。</para>
    /// </summary>
    [Fact]
    public void LatestMigrationDesigner_TargetModelIsInSyncWithCurrentModel()
    {
        using var db = CreateDbContext();

        var designerModel = GetLatestMigrationTargetModel(db);
        var targetModel = db.GetService<IDesignTimeModel>().Model;

        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(designerModel.GetRelationalModel(), targetModel.GetRelationalModel())
            .Where(op => InProductionScope(op, ProductionTableNames(designerModel), ProductionTableNames(targetModel)))
            .ToList();

        Assert.True(
            operations.Count == 0,
            "最后一条迁移的 Designer（BuildTargetModel）与当前 EF 模型存在差异 —— "
            + "dotnet ef migrations remove 会据此重写快照并静默丢掉这些差异，"
            + "随后 migrations add 又把它们当新变更写进下一条迁移："
            + Environment.NewLine + string.Join(Environment.NewLine, operations.Select(Describe)));
    }

    /// <summary>
    /// 上一条护栏的定点回归（工单 05 review Important 的原样复现）+ 反向自检：
    /// 迁移「之后」的 Designer 必须既含多账号/静音列，又已不含 F3 删掉的 Nextcloud 死字段。
    /// </summary>
    [Fact]
    public void LatestMigrationDesigner_ReflectsMultiProviderMuteAndDroppedColumns()
    {
        using var db = CreateDbContext();

        var model = GetLatestMigrationTargetModel(db);

        // 曾在 Designer 里缺失、只在快照里存在的两列（review 的原样复现点）
        Assert.True(HasColumn(model, "calendars", "default_muted"), "Designer 缺 calendars.default_muted");
        Assert.True(HasColumn(model, "events", "is_muted"), "Designer 缺 events.is_muted");

        // 本次迁移自身新增的列
        Assert.True(HasColumn(model, "file_providers", "is_default"), "Designer 缺 file_providers.is_default");
        Assert.True(HasColumn(model, "file_providers", "deferred_block_count"), "Designer 缺 file_providers.deferred_block_count");
        Assert.True(HasColumn(model, "quick_note_attachments", "storage_account_id"), "Designer 缺 quick_note_attachments.storage_account_id");

        // 反向：F3 删掉的四个 Nextcloud 死字段在迁移之后的 Designer 里必须已经不在了
        Assert.False(HasColumn(model, "file_providers", "base_url"), "Designer 仍有 base_url");
        Assert.False(HasColumn(model, "file_providers", "internal_base_url"), "Designer 仍有 internal_base_url");
        Assert.False(HasColumn(model, "file_providers", "username"), "Designer 仍有 username");
        Assert.False(HasColumn(model, "file_providers", "app_password_secret"), "Designer 仍有 app_password_secret");

        // 读法自检：上面的 HasColumn 必须真的能读到东西，否则整组 False 断言会静默变绿
        Assert.True(HasTable(model, "file_providers"));
        Assert.True(HasColumn(model, "file_providers", "provider"));
    }

    /// <summary>
    /// 取**最后一条**迁移的 design-time 目标模型（就是它 Designer 里 <c>BuildTargetModel</c>
    /// 建出来的那份），与 <c>dotnet ef migrations remove</c> 用的是同一份模型。
    /// </summary>
    private static IModel GetLatestMigrationTargetModel(PimDbContext db)
    {
        var migrations = db.GetService<IMigrationsAssembly>().Migrations;
        Assert.NotEmpty(migrations);

        // 迁移 id 前缀是时间戳，字典顺序即生成顺序；显式排序，不依赖字典插入顺序
        var latestId = migrations.Keys.OrderBy(id => id, StringComparer.Ordinal).Last();
        var migration = (Migration)Activator.CreateInstance(migrations[latestId])!;

        return db.GetService<IModelRuntimeInitializer>()
            .Initialize(migration.TargetModel, designTime: true);
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
        // 与生产一致：加载随 API 发布的全部模块（ProductionModules = Pim.Api 的程序集引用
        // ∪ 输出目录里的 Pim.Module.*.dll，不手工维护）。测试项目与生产引用同一组模块；
        // 模型里出现的**非生产实体**只可能来自测试程序集自造的实体（例如模型缓存金丝雀
        // model_cache_canaries），由 InProductionScope 按表名过滤。这样新模块接入 Pim.Api 后
        // 若忘同步快照，这里 fail-loud 而非静默绿。
        foreach (var moduleName in ProductionModules)
        {
            PimDbContext.RegisterModuleAssembly(Assembly.Load($"Pim.Module.{moduleName}"));
        }

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

    /// <summary>
    /// diff 操作是否落在生产范围内。
    ///
    /// <para>
    /// 为什么需要过滤：<see cref="PimDbContext.RegisterModuleAssembly"/> 是进程级静态状态，
    /// 同一测试进程里的其他测试会注册生产之外的实体 —— 例如模型缓存测试的金丝雀实体
    /// （model_cache_canaries）。
    /// 这些实体出现在本测试构建的当前模型里，但 <b>dotnet ef 永远看不见它们</b>，
    /// 也就不可能被吸收进迁移 —— 按生产范围过滤才与 dotnet ef 的真实行为等价。
    /// 识别不了的操作类型按范围内处理（宁可误报也不漏报）。
    /// </para>
    /// </summary>
    private static bool InProductionScope(
        MigrationOperation operation,
        HashSet<string> snapshotTables,
        HashSet<string> targetTables)
    {
        return TablesOf(operation)?.Any(table =>
            snapshotTables.Contains(table) || targetTables.Contains(table)) ?? true;
    }

    /// <summary>生产模型里的表名：核心程序集 + Pim.Api 实际引用的 6 个模块（白名单制，
    /// 排除测试程序集里自造的实体 —— 例如模型缓存金丝雀 model_cache_canaries）。</summary>
    private static readonly string[] ProductionNamespaces =
    [
        "Pim.Infrastructure.",
        "Pim.Core.",
        "Pim.Api.",
        ..ProductionModules.Select(m => $"Pim.Module.{m}."),
    ];

    private static HashSet<string> ProductionTableNames(IModel model)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entityType in model.GetEntityTypes())
        {
            var ns = entityType.ClrType.Namespace ?? string.Empty;
            var isProduction = ProductionNamespaces.Any(p => ns == p.TrimEnd('.') || ns.StartsWith(p, StringComparison.Ordinal));
            if (isProduction && entityType.GetTableName() is { } table)
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>
    /// 操作引用的表名（CreateTable 的外键父表也算 —— 对缺表建外键同样会炸）。
    /// 未知操作类型返回 null，调用方按「范围内」处理，宁可误报也不漏报。
    /// </summary>
    private static IEnumerable<string>? TablesOf(MigrationOperation operation) => operation switch
    {
        AddColumnOperation addColumn => [addColumn.Table],
        AlterColumnOperation alterColumn => [alterColumn.Table],
        DropColumnOperation dropColumn => [dropColumn.Table],
        CreateTableOperation createTable => createTable.ForeignKeys
            .Select(fk => fk.PrincipalTable)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .Append(createTable.Name),
        DropTableOperation dropTable => [dropTable.Name],
        CreateIndexOperation createIndex => [createIndex.Table],
        DropIndexOperation dropIndex => [dropIndex.Table],
        AddForeignKeyOperation addForeignKey => [addForeignKey.Table, addForeignKey.PrincipalTable!],
        DropForeignKeyOperation dropForeignKey => [dropForeignKey.Table],
        AddPrimaryKeyOperation addPrimaryKey => [addPrimaryKey.Table],
        _ => null,
    };

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
        DropForeignKeyOperation dropForeignKey => $"DropForeignKey {dropForeignKey.Table} {dropForeignKey.Name}",
        AddPrimaryKeyOperation addPrimaryKey => $"AddPrimaryKey {addPrimaryKey.Table}",
        _ => operation.GetType().Name,
    };
}
