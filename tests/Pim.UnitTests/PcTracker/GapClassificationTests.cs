using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// #331：空档（gap）被判成「游戏」并渲染进 timeline/v2 应用块，每天约 10 小时持续产生。
///
/// 规则级根因：<c>AppNameNormalizer.Normalize(null/空白)</c> 返回字面量 <c>"unknown"</c>，
/// 而 <c>pc_app_categories</c> 迁移出的 <c>Migrated app rule: unknown</c> 规则把
/// <c>appNameNormalized equals "unknown"</c> 映射到「游戏」（confidence 0.95，priority +1000）。
/// 于是所有「无应用身份」的记录——gap / idle / afk——一律命中该规则变「游戏」。
///
/// 本文件锁定三层修复：
/// 1. 分类层：非应用记录（gap/idle/afk）不参与应用类别匹配，结论是「未活动」而不是某个应用类别；
/// 2. 规则层：unknown 兜底规则不得命中非应用记录，且不会再被迁移播种成 active 的「游戏」规则；
/// 3. 展示层：timeline/v2 不把空档渲染成 <c>pc-fallback-v1:*</c> 伪应用块。
/// </summary>
public sealed class GapClassificationTests
{
    private static ActivityCategoryRuleEntity UnknownRule(
        string categoryName = "游戏",
        string status = "active",
        string source = "user")
        => new()
        {
            Id = Guid.NewGuid(),
            RuleName = "Migrated app rule: unknown",
            Scope = "activity",
            CategoryName = categoryName,
            Color = "#F43F5E",
            Priority = 1100,
            Source = source,
            Status = status,
            ConditionsJson = """{"all":[{"field":"appNameNormalized","op":"equals","value":"unknown"}]}""",
            Confidence = 0.95,
            Explanation = "Migrated from pc_app_categories.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ActivityClassificationContext InactiveContext(string recordType)
        => new(
            RecordType: recordType,
            AppName: null,
            AppNameNormalized: AppNameNormalizer.Normalize(null),
            Domain: null,
            UrlPath: null,
            Title: null,
            WindowTitle: null,
            FilePath: null,
            BucketType: null);

    // ================= 1. 分类层：非应用记录不再被判「游戏」 =================

    [Theory]
    [InlineData("gap")]
    [InlineData("idle")]
    [InlineData("afk")]
    public void Classify_InactiveRecord_DoesNotMatchUnknownGameRule(string recordType)
    {
        var rules = new[] { UnknownRule() };
        var context = InactiveContext(recordType);

        var result = ActivityClassifier.Classify(context, rules, NullLogger.Instance);

        Assert.NotEqual("游戏", result.CategoryName);
        Assert.Equal(ActivityClassificationResult.InactiveCategoryName, result.CategoryName);
        Assert.NotEqual("rule", result.Source);
    }

    [Fact]
    public void Classify_InactiveRecord_IsNotOverriddenByAnyUserAppRule()
    {
        // 不止 unknown 规则：任何以应用名/应用类别为条件的规则都不应命中空档记录，
        // 否则「空档」会被任意应用规则改写成别的活动类别。
        var rules = new[]
        {
            new ActivityCategoryRuleEntity
            {
                Id = Guid.NewGuid(),
                RuleName = "User: 把 code 记为编程",
                Scope = "activity",
                CategoryName = "编程/折腾",
                Color = "#6B5EE4",
                Priority = 3000,
                Source = "user",
                Status = "active",
                ConditionsJson = """{"all":[{"field":"appNameNormalized","op":"equals","value":"unknown"}]}""",
                Confidence = 0.99,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        var result = ActivityClassifier.Classify(InactiveContext("gap"), rules, NullLogger.Instance);

        Assert.Equal(ActivityClassificationResult.InactiveCategoryName, result.CategoryName);
    }

    [Fact]
    public void Classify_ActiveRecord_StillMatchesUnknownRule()
    {
        // 回归保护：修复只针对「非应用记录」，不能顺手让真实应用的 unknown 规则失效。
        var rules = new[] { UnknownRule() };
        var context = new ActivityClassificationContext(
            RecordType: "window",
            AppName: "unknown",
            AppNameNormalized: "unknown",
            Domain: null,
            UrlPath: null,
            Title: null,
            WindowTitle: null,
            FilePath: null,
            BucketType: null);

        var result = ActivityClassifier.Classify(context, rules, NullLogger.Instance);

        Assert.Equal("游戏", result.CategoryName);
        Assert.Equal("rule", result.Source);
    }

    [Fact]
    public void Classify_InactiveRecord_IsDetectedBySharedResolver()
    {
        // 分类层必须复用全仓库唯一口径，避免「统计过滤 gap、分类却不管」的再次分叉（#301 遗留）。
        Assert.True(PcActivityOverlapResolver.IsInactive("gap"));
        Assert.True(PcActivityOverlapResolver.IsInactive("idle"));
        Assert.True(PcActivityOverlapResolver.IsInactive("afk"));
        Assert.False(PcActivityOverlapResolver.IsInactive("window"));
    }

    [Fact]
    public void InactiveClassification_IsExplicitAndNotASuggestionCandidate()
    {
        var result = ActivityClassificationResult.Inactive();

        Assert.Equal(ActivityClassificationResult.InactiveCategoryName, result.CategoryName);
        Assert.Equal("#94a3b8", result.CategoryColor);
        Assert.Equal("inactive", result.Source);
        Assert.Null(result.ProjectTag);

        // 「该时段没有应用活动」是确定结论而非猜测 → 高置信；同时因为 source 不是 fallback、
        // 置信度也不低于 0.5，不会进入「待人工标注」建议队列（空档不该被追问属于哪个应用类别）。
        Assert.True(result.Confidence >= 0.5);
        Assert.NotEqual("fallback", result.Source);
    }

    // ================= 2. 规则层：unknown 规则不再被播种成「游戏」 =================

    [Fact]
    public void SchemaSql_MigrationInsertExcludesUnknownPattern()
    {
        // 播种语句必须显式排除 unknown —— 否则新建/重建的库里又会多出一条
        // 「unknown → 某应用类别」的 active 规则（生产库里它被映射成了「游戏」）。
        var normalized = PcTrackerSchemaInitializer.SchemaSql.Replace("\r\n", "\n", StringComparison.Ordinal);

        var insertIndex = normalized.IndexOf("'Migrated app rule: ' || app_pattern", StringComparison.Ordinal);
        Assert.True(insertIndex >= 0, "应存在迁移规则播种语句");

        // 取该 INSERT 语句的结尾（到下一个语句分隔符 `;` 为止）
        var statementEnd = normalized.IndexOf(';', insertIndex);
        Assert.True(statementEnd > insertIndex);
        var statement = normalized[insertIndex..statementEnd];

        Assert.Contains("<> 'unknown'", statement);
    }

    [Fact]
    public void SchemaSql_DisablesLegacyUnknownRulesByConditionNotName()
    {
        // 生产库里已经存在该规则（active），必须有一条幂等 SQL 把它停用，
        // 否则仅改播种逻辑对存量数据库无效。
        //
        // 识别**按规则语义**（条件里 appNameNormalized equals unknown）而不是只按规则名：
        // 历史迁移的名称变体（大小写 / .exe / 空格）都要覆盖，同时不误伤恰好同名的其它规则
        //（review 发现只匹配精确名称会漏掉变体、并可能误停用户规则）。
        var sql = PcTrackerSchemaInitializer.SchemaSql.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("UPDATE pc_activity_category_rules", sql);
        Assert.Contains("status = 'disabled'", sql);
        Assert.Contains("conditions_json -> 'all' -> 0 ->> 'value'", sql);
        Assert.Contains("= 'appNameNormalized'", sql);
        // 名称变体归一化：去 .exe、去空格、转小写后再比较
        Assert.Contains("lower(trim(regexp_replace(", sql);
        // 不能只按规则名匹配（那会漏掉未知变体）
        Assert.DoesNotContain("WHERE rule_name = 'Migrated app rule: unknown'", sql);
    }

    [Fact]
    public void SchemaSql_UnknownCleanupRunsBeforeMigrationInsert()
    {
        // 顺序保证：先停用存量 unknown 规则，再执行迁移插入，
        // 避免「插入时 ON CONFLICT DO NOTHING 跳过、清理又漏掉」的空窗。
        var normalized = PcTrackerSchemaInitializer.SchemaSql.Replace("\r\n", "\n", StringComparison.Ordinal);

        var cleanupIndex = normalized.IndexOf(
            "UPDATE pc_activity_category_rules",
            StringComparison.Ordinal);
        var insertIndex = normalized.IndexOf(
            "'Migrated app rule: ' || app_pattern",
            StringComparison.Ordinal);

        Assert.True(cleanupIndex >= 0, "应存在针对存量 unknown 规则的处理");
        Assert.True(insertIndex >= 0, "应存在迁移规则播种");
        Assert.True(cleanupIndex < insertIndex, "停用存量 unknown 规则必须发生在播种之前");
    }

    [Fact]
    public void SchemaSql_FormatsJsonbLiteralsForExecuteSqlRawAfterUnknownCleanup()
    {
        // 初始化 SQL 走 string.Format，新增 SQL 若带入未转义花括号会直接抛 FormatException。
        var formattedSql = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            PcTrackerSchemaInitializer.SchemaSql,
            Array.Empty<object>());

        Assert.Contains("DEFAULT '{}'::jsonb", formattedSql);
        Assert.Contains("DEFAULT '[]'::jsonb", formattedSql);
    }

    // ================= 3. 展示层：timeline/v2 不渲染空档伪应用块 =================

    /// <summary>业务日 2026-07-07 = UTC [2026-07-06 20:00, 2026-07-07 20:00)；北京时间 10:00 == UTC 02:00。</summary>
    private static DateTimeOffset Beijing(int day, int hour, int minute = 0)
        => new(new DateTime(2026, 7, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-8), TimeSpan.Zero);

    private static ActivityClassificationEntity Snapshot(
        string recordKey,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string recordType,
        string categoryName,
        double confidence = 0.95,
        string? appName = null)
        => new()
        {
            Id = Guid.NewGuid(),
            RecordKey = recordKey,
            RecordType = recordType,
            DeviceId = "pc-1",
            StartedAt = startedAt,
            EndedAt = endedAt,
            CategoryName = categoryName,
            CategoryColor = "#F43F5E",
            Confidence = confidence,
            Source = "rule",
            Explanation = "Migrated from pc_app_categories.",
            ClassifierVersion = "local-v1",
            ClassifiedAt = DateTimeOffset.UtcNow,
            AppName = appName
        };

    [Fact]
    public async Task TimelineV2_GapRecords_AreNotRenderedAsApplicationBlocks()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 04:00→10:30 睡眠/无活动时段：3 个 30 分钟 gap 片，全部曾被判「游戏」
        db.Set<ActivityClassificationEntity>().AddRange(
            Snapshot("pc-fallback-v1:gap-1", Beijing(7, 4), Beijing(7, 4, 30), "gap", "游戏"),
            Snapshot("pc-fallback-v1:gap-2", Beijing(7, 4, 30), Beijing(7, 5), "gap", "游戏"),
            Snapshot("pc-fallback-v1:gap-3", Beijing(7, 5), Beijing(7, 5, 30), "gap", "游戏"));
        // 同一天的真实游戏时段必须保留
        db.Set<ActivityClassificationEntity>().Add(
            Snapshot("k-real-game", Beijing(7, 20), Beijing(7, 21), "window", "游戏", 0.99, appName: "steam.exe"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(new DateTime(2026, 7, 7), CancellationToken.None);

        // 空档不得出现在时间线里（既不作为应用块，也不作为「游戏」时间）
        Assert.DoesNotContain(res, item => item.AppName.StartsWith("pc-fallback-v1:", StringComparison.Ordinal));
        Assert.DoesNotContain(res, item => item.CategoryName == "游戏" && item.AppName != "steam.exe");

        // 真实活动仍然保留，且分类正确
        var real = Assert.Single(res);
        Assert.Equal("steam.exe", real.AppName);
        Assert.Equal("游戏", real.CategoryName);
        Assert.Equal("distracting", real.Productivity);
    }

    [Fact]
    public async Task TimelineV2_IdleAndAfkRecords_AreNotRendered()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().AddRange(
            Snapshot("pc-fallback-v1:idle-1", Beijing(7, 12), Beijing(7, 13), "idle", "游戏"),
            Snapshot("pc-fallback-v1:afk-1", Beijing(7, 13), Beijing(7, 14), "afk", "游戏"));
        db.Set<ActivityClassificationEntity>().Add(
            Snapshot("k-work", Beijing(7, 15), Beijing(7, 16), "window", "编程/折腾", 0.9, appName: "Code.exe"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(new DateTime(2026, 7, 7), CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("Code.exe", block.AppName);
    }

    [Fact]
    public async Task TimelineV2_AllInactiveDay_ReturnsEmptyTimeline()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(
            Snapshot("pc-fallback-v1:gap-all", Beijing(7, 4), Beijing(7, 12), "gap", "游戏"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(new DateTime(2026, 7, 7), CancellationToken.None);

        Assert.Empty(res);
    }

    [Fact]
    public async Task TimelineV2_InactiveGapDoesNotShadowRealActivity()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 空档与真实活动重叠：空档不得「赢」下重叠时段而把真实应用挤掉
        db.Set<ActivityClassificationEntity>().AddRange(
            Snapshot("pc-fallback-v1:gap-overlap", Beijing(7, 10), Beijing(7, 11), "gap", "游戏"),
            Snapshot("k-real", Beijing(7, 10, 30), Beijing(7, 11, 30), "window", "编程/折腾", 0.9, appName: "Code.exe"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(new DateTime(2026, 7, 7), CancellationToken.None);

        Assert.All(res, item => Assert.Equal("Code.exe", item.AppName));
        Assert.DoesNotContain(res, item => item.CategoryName == "游戏");
        // 10:00-10:30 只有空档覆盖 → 不应产出任何块
        Assert.DoesNotContain(res, item => item.Start == Beijing(7, 10));
    }

    // ================= 4. 历史数据：存量错误分类必须被重写 =================

    /// <summary>构造一条 gap 分类记录（模拟 #331 修复前被写成「游戏」的存量行）。</summary>
    private static PcDetailRecord GapRecord(string deviceId = "device-1")
        => new(
            "gap",
            "2026-07-07T02:00:00Z",
            "2026-07-07T02:30:00Z",
            1800,
            deviceId,
            null,
            null,
            "游戏",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    [Fact]
    public async Task EnsureClassificationsAsync_RewritesLegacyInactiveSnapshotOnNormalBackfill()
    {
        // #331 的历史数据风险：常规 detail/backfill 走的是 auditId == null 的分支，
        // 旧实现在该分支直接沿用已有快照，于是生产库里已经写成「游戏」的 gap 行
        // 永远不会被纠正（只修了新数据）。这里锁定：非应用记录必须按新口径重写。
        using var db = MobileTestHelpersBridge.CreateDb();
        var snapshotService = new ActivityClassificationSnapshotService(
            db,
            NullLogger<ActivityClassificationSnapshotService>.Instance);

        var record = GapRecord();
        var recordKey = ActivityClassificationRecordKey.FromRecord(record);

        // 存量快照：旧规则判成「游戏」，source=rule
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = recordKey,
            RecordType = "gap",
            DeviceId = "device-1",
            StartedAt = DateTimeOffset.Parse("2026-07-07T02:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-07-07T02:30:00Z"),
            CategoryName = "游戏",
            CategoryColor = "#F43F5E",
            Confidence = 0.95,
            Source = "rule",
            ClassifierVersion = "local-v1",
            ClassifiedAt = DateTimeOffset.Parse("2026-07-07T03:00:00Z")
        });
        await db.SaveChangesAsync();

        // auditId: null == 常规补齐路径
        var classified = await snapshotService.EnsureClassificationsAsync(
            [record],
            [],
            auditId: null,
            CancellationToken.None);

        Assert.Equal(ActivityClassificationResult.InactiveCategoryName, classified[0].CategoryName);

        var rewritten = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal(ActivityClassificationResult.InactiveCategoryName, rewritten.CategoryName);
        Assert.Equal("inactive", rewritten.Source);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_KeepsProtectedInactiveSnapshot()
    {
        // 人工纠正过的空档分类必须保留：重写历史不能覆盖人的判断。
        using var db = MobileTestHelpersBridge.CreateDb();
        var snapshotService = new ActivityClassificationSnapshotService(
            db,
            NullLogger<ActivityClassificationSnapshotService>.Instance);

        var record = GapRecord();
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = ActivityClassificationRecordKey.FromRecord(record),
            RecordType = "gap",
            DeviceId = "device-1",
            StartedAt = DateTimeOffset.Parse("2026-07-07T02:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-07-07T02:30:00Z"),
            CategoryName = "学习",
            CategoryColor = "#14b8a6",
            Confidence = 0.99,
            Source = "manual",
            ClassifierVersion = "local-v1",
            ClassifiedAt = DateTimeOffset.Parse("2026-07-07T03:00:00Z")
        });
        await db.SaveChangesAsync();

        await snapshotService.EnsureClassificationsAsync([record], [], null, CancellationToken.None);

        var kept = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("学习", kept.CategoryName);
        Assert.Equal("manual", kept.Source);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_LeavesActiveSnapshotsUntouchedOnBackfill()
    {
        // 回归保护：重写只针对非应用记录，普通应用记录的存量快照仍按原逻辑沿用
        // （避免把每次 backfill 都变成全量重分类）。
        using var db = MobileTestHelpersBridge.CreateDb();
        var snapshotService = new ActivityClassificationSnapshotService(
            db,
            NullLogger<ActivityClassificationSnapshotService>.Instance);

        var record = new PcDetailRecord(
            "window",
            "2026-07-07T02:00:00Z",
            "2026-07-07T02:30:00Z",
            1800,
            "device-1",
            "Code.exe",
            "Code.exe",
            "文档",
            "Program.cs",
            null,
            null,
            null,
            null,
            null,
            null);

        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = ActivityClassificationRecordKey.FromRecord(record),
            RecordType = "window",
            DeviceId = "device-1",
            StartedAt = DateTimeOffset.Parse("2026-07-07T02:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-07-07T02:30:00Z"),
            CategoryName = "文档",
            CategoryColor = "#F59E0B",
            Confidence = 0.9,
            Source = "rule",
            ClassifierVersion = "local-v1",
            ClassifiedAt = DateTimeOffset.Parse("2026-07-07T03:00:00Z")
        });
        await db.SaveChangesAsync();

        await snapshotService.EnsureClassificationsAsync([record], [], null, CancellationToken.None);

        var kept = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("文档", kept.CategoryName);
        Assert.Equal("rule", kept.Source);
    }
}

/// <summary>InMemory 库工厂：本文件同时用到 PcTracker 与 Mobile 实体配置。</summary>
internal static class MobileTestHelpersBridge
{
    public static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"gap-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }
}
