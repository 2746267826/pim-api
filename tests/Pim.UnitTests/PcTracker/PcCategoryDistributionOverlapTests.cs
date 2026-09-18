using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// #301 回归护栏：「今日」页 PC 概览的两处口径修正。
///
/// A. 分类分布分钟数膨胀：快照由存在重叠的采集事件派生（gap / input-minute / window /
///    web-page 互相嵌套），原实现「逐条 cap 后按重叠比例分摊再直接求和」，同一时刻被重复计入
///    多个分类。实测 2026-09-17 全部类别合计 1,622 分钟 ≈ 27 小时（超过物理上限 24h），
///    且 707 分钟的 gap 空档被整块判为「游戏」。
///    修正口径（已与用户确认）：
///      - 同一时刻只归属一个分类（按优先级去重，前台窗口记录优先）；
///      - gap / idle 完全不进入分类分布（既不出现在列表中，也不计入分母）；
///      - 任一天所有分类合计 ≤ 24 小时（物理上限）。
///
/// B.「主要应用」百分比以第一名归一：原实现 share = 该应用按键数 ÷ 第一名按键数，
///    使第一名恒为 100%。修正为占总量口径（已与用户确认选 C）：
///      share = (按键+点击) ÷ 全部(按键+点击)，保留按键+点击降序排序。
///
/// 业务日口径：D = [D 04:00, D+1 04:00) Asia/Shanghai。
/// </summary>
public sealed class PcCategoryDistributionOverlapTests
{
    private static readonly DateTime TestDate = new(2026, 9, 17);

    /// <summary>业务日窗口内的一个北京时间时刻 → UTC DateTimeOffset。</summary>
    private static DateTimeOffset Beijing(int day, int hour, int minute = 0, int second = 0)
        => new(new DateTime(2026, 9, day, hour, minute, second, DateTimeKind.Utc).AddHours(-8), TimeSpan.Zero);

    private static ActivityClassificationEntity Snapshot(
        string recordKey,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string categoryName,
        string recordType = "window",
        double confidence = 0.8,
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
            CategoryColor = "#10b981",
            Confidence = confidence,
            Source = "rule",
            ClassifierVersion = "v1",
            ClassifiedAt = DateTimeOffset.UtcNow,
            AppName = appName,
        };

    private static PcCategoryDistributionItem Get(PcCategoryDistributionResponse res, string category)
        => res.Items.SingleOrDefault(i => i.CategoryName == category)
           ?? throw new Xunit.Sdk.XunitException($"分类「{category}」不在结果中：{string.Join(", ", res.Items.Select(i => i.CategoryName))}");

    // ================= A. 总计不得超过 24 小时 =================

    /// <summary>
    /// 复现 #301 的膨胀形态：一条 13:36–16:42 的长 window 记录内部，嵌套着
    /// input-minute 微记录与 web-page 记录。原实现逐条求和 → 合计远超 24 小时。
    /// </summary>
    [Fact]
    public async Task CategoryDistribution_OverlappingRecords_TotalNeverExceeds24Hours()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 长记录：游戏 13:36-16:42（186 分钟）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("long", Beijing(17, 13, 36), Beijing(17, 16, 42), "游戏"));
        // 内部嵌套 6 段 30 分钟同分类小块
        for (var i = 0; i < 6; i++)
        {
            var start = Beijing(17, 13, 37).AddMinutes(i * 30);
            db.Set<ActivityClassificationEntity>().Add(Snapshot($"nested-{i}", start, start.AddMinutes(30), "游戏"));
        }
        // 同一时段内还叠着「浏览」网页记录（12:48-12:52 的窗口内 4 条 1 分钟游戏记录）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("web", Beijing(17, 12, 48), Beijing(17, 12, 52), "浏览", "web-page"));
        for (var i = 0; i < 4; i++)
        {
            var start = Beijing(17, 12, 48).AddMinutes(i);
            db.Set<ActivityClassificationEntity>().Add(Snapshot($"minute-{i}", start, start.AddMinutes(1), "游戏", "input-minute"));
        }
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        var totalMinutes = res.Items.Sum(i => i.Minutes);
        Assert.True(totalMinutes <= 24 * 60,
            $"分类分布合计 {totalMinutes} 分钟超过 24 小时物理上限");
    }

    /// <summary>构造一组极端重叠输入，合计仍必须 ≤ 1440 分钟。</summary>
    [Fact]
    public async Task CategoryDistribution_MassivelyOverlappingRecords_RespectsPhysicalCap()
    {
        await using var db = ServiceTestBase.CreateDb();
        var categories = new[] { "游戏", "浏览", "编程/折腾", "文档", "学习" };
        var random = new Random(20260917);
        for (var i = 0; i < 200; i++)
        {
            // 全部落在 10:00-18:00 内，每条 5-60 分钟，大量互相重叠
            var offset = random.Next(0, 480);
            var length = random.Next(5, 60);
            db.Set<ActivityClassificationEntity>().Add(Snapshot(
                $"r-{i}",
                Beijing(17, 10).AddMinutes(offset),
                Beijing(17, 10).AddMinutes(offset + length),
                categories[i % categories.Length]));
        }
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        var totalMinutes = res.Items.Sum(i => i.Minutes);
        Assert.True(totalMinutes <= 24 * 60,
            $"分类分布合计 {totalMinutes} 分钟超过 24 小时物理上限");
    }

    // ================= A. 同一时刻只归属一个分类 =================

    /// <summary>
    /// 嵌套的同类记录不得重复计数：一条 60 分钟记录内部叠着 4 段 10 分钟同分类小块，
    /// 去重后仍应是 60 分钟（逐条相加会得到 100 分钟）。
    /// 注：单条记录另有 3600s 上限（既有口径），故本条主记录取在 cap 以内以隔离本用例关注点。
    /// </summary>
    [Fact]
    public async Task CategoryDistribution_NestedSameCategory_CountedOnce()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("long", Beijing(17, 13), Beijing(17, 14), "游戏"));
        for (var i = 0; i < 4; i++)
        {
            var start = Beijing(17, 13, 5).AddMinutes(i * 10);
            db.Set<ActivityClassificationEntity>().Add(Snapshot($"nested-{i}", start, start.AddMinutes(10), "游戏"));
        }
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        var game = Get(res, "游戏");
        Assert.InRange(game.Minutes, 59, 61);
    }

    /// <summary>
    /// 同一时刻归属不同分类时按优先级唯一归属：前台 window 记录优先于 input-minute 微记录。
    /// 「浏览」web-page 窗口 12:48-12:52 内叠着 4 条 1 分钟「游戏」→ 该 4 分钟应算浏览，不算游戏。
    /// </summary>
    [Fact]
    public async Task CategoryDistribution_HigherPriorityRecordWinsContestedRegion()
    {
        await using var db = ServiceTestBase.CreateDb();
        // window 记录 12:48-12:52「浏览」（高置信）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("web", Beijing(17, 12, 48), Beijing(17, 12, 52), "浏览", "web-page", confidence: 0.8));
        // 同一时段 4 条 input-minute「游戏」（低置信）
        for (var i = 0; i < 4; i++)
        {
            var start = Beijing(17, 12, 48).AddMinutes(i);
            db.Set<ActivityClassificationEntity>().Add(Snapshot($"minute-{i}", start, start.AddMinutes(1), "游戏", "input-minute", confidence: 0.2));
        }
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        // 4 分钟全部归「浏览」；「游戏」没有独占时段 → 不出现（或为 0）
        var browse = Get(res, "浏览");
        Assert.InRange(browse.Minutes, 3, 5);
        var game = res.Items.FirstOrDefault(i => i.CategoryName == "游戏");
        Assert.True(game is null || game.Minutes == 0,
            $"「游戏」不应获得被 window 记录覆盖的分钟数，实际 {game?.Minutes} 分钟");
    }

    /// <summary>互不重叠的记录保持原样，逐条相加即可。</summary>
    [Fact]
    public async Task CategoryDistribution_NonOverlappingRecords_ArePreserved()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("a", Beijing(17, 9), Beijing(17, 10), "编程/折腾"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("b", Beijing(17, 10), Beijing(17, 11), "文档"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("c", Beijing(17, 11), Beijing(17, 11, 30), "编程/折腾"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.InRange(Get(res, "编程/折腾").Minutes, 89, 91); // 60 + 30
        Assert.InRange(Get(res, "文档").Minutes, 59, 61);
    }

    // ================= A. gap / idle 完全排除 =================

    /// <summary>
    /// gap / idle 记录完全不进入分类分布：既不出现在 items 中，
    /// 也不计入百分比分母（合计仍应等于真实活动分钟数）。
    /// </summary>
    [Fact]
    public async Task CategoryDistribution_GapAndIdleRecords_AreExcluded()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("active", Beijing(17, 10), Beijing(17, 11), "编程/折腾"));
        // 707 分钟 gap 全判为「游戏」（#301 的典型失效形态）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("gap-1", Beijing(17, 4), Beijing(17, 16), "游戏", "gap"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("idle-1", Beijing(17, 20), Beijing(17, 21), "游戏", "idle"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("afk-1", Beijing(17, 22), Beijing(17, 23), "其他", "afk"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.DoesNotContain(res.Items, i => i.CategoryName == "游戏");
        Assert.InRange(res.Items.Sum(i => i.Minutes), 59, 61);
        Assert.InRange(res.Items.Sum(i => i.Percentage), 99.0, 101.0);
    }

    /// <summary>只有 gap / idle 时结果为空白，不得伪造分类。</summary>
    [Fact]
    public async Task CategoryDistribution_OnlyGapAndIdle_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("gap-1", Beijing(17, 4), Beijing(17, 16), "游戏", "gap"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("idle-1", Beijing(17, 20), Beijing(17, 21), "游戏", "idle"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.Empty(res.Items);
    }

    // ================= A. 百分比口径 =================

    /// <summary>百分比按去重后的分钟数占总量计算，四舍五入后合计 ≈100%。</summary>
    [Fact]
    public async Task CategoryDistribution_Percentages_SumToHundred()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("a", Beijing(17, 9), Beijing(17, 10), "编程/折腾"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("b", Beijing(17, 10), Beijing(17, 10, 30), "文档"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("c", Beijing(17, 10, 30), Beijing(17, 11), "浏览"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.InRange(res.Items.Sum(i => i.Percentage), 99.0, 101.0);
    }

    // ================= A. 跨业务日切分保持 =================

    /// <summary>跨 04:00 业务日边界的记录仍按重叠比例切分到两天。</summary>
    [Fact]
    public async Task CategoryDistribution_CrossBusinessDay_StillProrated()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 9/17 03:50 - 04:20 → 前一日 10 分钟 + 当日 20 分钟
        db.Set<ActivityClassificationEntity>().Add(Snapshot("cross", Beijing(17, 3, 50), Beijing(17, 4, 20), "编程/折腾"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var prev = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-16", null, null, null), CancellationToken.None);
        var curr = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.InRange(Get(prev, "编程/折腾").Minutes, 9, 11);
        Assert.InRange(Get(curr, "编程/折腾").Minutes, 19, 21);
    }

    // ================= B. 主要应用百分比改为占总量 =================

    private static void SeedKeystats(PimDbContext db, DateTime date)
    {
        db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity
        {
            DeviceId = "pc-1",
            SnapshotDate = date.Date,
            KeyPresses = 16621,
            LeftClicks = 5329,
            RightClicks = 1581,
            MiddleClicks = 42,
            SideBackClicks = 23,
            SideForwardClicks = 801,
            MouseDistance = 0,
            ScrollDistance = 0,
            PeakKps = 14,
            PeakCps = 11,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCounts = new List<KeystatsKeyCountEntity>(),
            AppBreakdowns = new List<KeystatsAppBreakdownEntity>
            {
                // #301 真实数据：VALORANT 15,115 按键 / 6,591 点击；Obsidian 741 / 68
                new() { AppName = "VALORANT-Win64-Shipping", DisplayName = "VALORANT", KeyPresses = 15115, LeftClicks = 6591, RightClicks = 0, MiddleClicks = 0, ScrollDistance = 0 },
                new() { AppName = "Obsidian", DisplayName = "Obsidian", KeyPresses = 741, LeftClicks = 68, RightClicks = 0, MiddleClicks = 0, ScrollDistance = 0 },
                new() { AppName = "msedge", DisplayName = "Microsoft Edge", KeyPresses = 273, LeftClicks = 654, RightClicks = 0, MiddleClicks = 0, ScrollDistance = 0 },
            }
        });
    }

    /// <summary>第一名不再恒为 100%：份额按 (按键+点击) ÷ 全部(按键+点击) 计算。</summary>
    [Fact]
    public async Task AppRanking_TopAppShareIsNotForcedToOne()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        var top = res.AppRanking[0];
        Assert.Equal("VALORANT-Win64-Shipping", top.AppName);
        Assert.True(top.Share < 1.0, $"第一名份额不应恒为 100%，实际 {top.Share:P1}");
    }

    /// <summary>份额 = (按键+点击) ÷ 全部(按键+点击)，逐项可核对。</summary>
    [Fact]
    public async Task AppRanking_ShareEqualsKeysPlusClicksOverTotal()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        // 全部按键 = 15,115 + 741 + 273；全部点击 = 6,591 + 68 + 654
        const double totalKeys = 15115 + 741 + 273;
        const double totalClicks = 6591 + 68 + 654;
        var total = totalKeys + totalClicks;

        foreach (var app in res.AppRanking)
        {
            var expected = (app.KeyPresses + app.TotalClicks) / total;
            Assert.Equal(expected, app.Share, 4);
        }
    }

    /// <summary>全部应用份额合计 ≈100%（不再是一堆「相对第一名」的比例）。</summary>
    [Fact]
    public async Task AppRanking_SharesSumToHundredPercent()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.InRange(res.AppRanking.Sum(a => a.Share) * 100, 99.0, 101.0);
    }

    /// <summary>排序口径保持不变：按键+点击 降序（含中键 / 侧键，与份额口径一致）。</summary>
    [Fact]
    public async Task AppRanking_KeepsKeysPlusClicksDescendingOrder()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedKeystats(db, TestDate);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        var weights = res.AppRanking.Select(a => a.KeyPresses + a.TotalClicks).ToList();
        Assert.Equal(weights.OrderByDescending(w => w).ToList(), weights);
    }

    /// <summary>
    /// 排序必须计入**全部**点击类型（含中键 / 侧键），否则排名与显示的百分比自相矛盾
    /// （review 发现的 Important）。
    /// </summary>
    [Fact]
    public async Task AppRanking_SortIncludesMiddleAndSideClicks()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity
        {
            DeviceId = "pc-1",
            SnapshotDate = TestDate.Date,
            KeyPresses = 1000,
            LeftClicks = 0,
            RightClicks = 0,
            MiddleClicks = 0,
            SideBackClicks = 0,
            SideForwardClicks = 0,
            MouseDistance = 0,
            ScrollDistance = 0,
            PeakKps = 1,
            PeakCps = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCounts = new List<KeystatsKeyCountEntity>(),
            AppBreakdowns = new List<KeystatsAppBreakdownEntity>
            {
                // 只看左右键：left(10) 领先；计入侧键后 right(10+200) 才是第一
                new() { AppName = "left.exe", DisplayName = "Left", KeyPresses = 100, LeftClicks = 10, RightClicks = 0, MiddleClicks = 0, SideBackClicks = 0, SideForwardClicks = 0, ScrollDistance = 0 },
                new() { AppName = "side.exe", DisplayName = "Side", KeyPresses = 100, LeftClicks = 0, RightClicks = 0, MiddleClicks = 0, SideBackClicks = 150, SideForwardClicks = 50, ScrollDistance = 0 },
            }
        });
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal("side.exe", res.AppRanking[0].AppName);
    }

    /// <summary>
    /// 跨业务日的长记录仍按「cap 后按重叠比例分摊」计入，不得因裁剪顺序而低估
    /// （review 发现的 Important）。
    /// <para>
    /// 记录北京 03:00–05:00（原始 7200s，cap 后 3600s）；业务日 9/17 窗口从 04:00 起，
    /// 与窗口重叠 3600s → 按比例计入 3600 × (3600/7200) = 1800s = 30 分钟。
    /// 修复前「先截断到 cap 再裁剪」会得到 0 分钟（截断终点恰落在窗口起点）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task CategoryDistribution_LongRecordSpanningBusinessDay_IsProratedNotDropped()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("long-cross", Beijing(17, 3), Beijing(17, 5), "编程/折腾"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.InRange(Get(res, "编程/折腾").Minutes, 29, 31);
    }

    /// <summary>超过 3600s 上限的记录按上限计入（既有 cap 口径保持）。</summary>
    [Fact]
    public async Task CategoryDistribution_RecordExceedingCap_IsCapped()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 10:00-14:00（240 分钟）→ cap 到 60 分钟
        db.Set<ActivityClassificationEntity>().Add(Snapshot("too-long", Beijing(17, 10), Beijing(17, 14), "编程/折腾"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.InRange(Get(res, "编程/折腾").Minutes, 59, 61);
    }

    /// <summary>百分比校正不得产生负数（review 发现的 Minor）。</summary>
    [Fact]
    public async Task CategoryDistribution_PercentageCorrection_NeverProducesNegative()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 构造四舍五入后合计略超 100 的分布，触发校正逻辑
        var cases = new (string Category, int Minutes)[]
        {
            ("A", 731), ("B", 580), ("C", 129), ("D", 0),
        };
        var start = Beijing(17, 8);
        foreach (var (category, minutes) in cases)
        {
            if (minutes <= 0) continue;
            for (var chunk = 0; chunk < minutes; chunk += 60)
            {
                var length = Math.Min(60, minutes - chunk);
                db.Set<ActivityClassificationEntity>().Add(Snapshot(
                    $"k-{category}-{chunk}",
                    start.AddMinutes(chunk),
                    start.AddMinutes(chunk + length),
                    category));
            }
        }
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.All(res.Items, i => Assert.True(i.Percentage >= 0, $"分类 {i.CategoryName} 百分比为负：{i.Percentage}"));
        Assert.InRange(res.Items.Sum(i => i.Percentage), 99.0, 101.0);
    }

    // ================= review 修复：生产力统计复用同一套去重 =================

    /// <summary>
    /// 生产力 dashboard 不再逐条累加重叠记录（review 发现的 Important）：
    /// 总和不得超过 24 小时，且 productive + distracting + neutral = total。
    /// </summary>
    [Fact]
    public async Task ProductivityDashboard_OverlappingRecords_TotalNeverExceeds24Hours()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 大量互相重叠的「编程」（productive）与「游戏」（distracting）记录
        var random = new Random(20260917);
        for (var i = 0; i < 120; i++)
        {
            var offset = random.Next(0, 600);
            var length = random.Next(10, 120);
            var category = i % 2 == 0 ? "编程/折腾" : "游戏";
            db.Set<ActivityClassificationEntity>().Add(Snapshot(
                $"p-{i}",
                Beijing(17, 8).AddMinutes(offset),
                Beijing(17, 8).AddMinutes(offset + length),
                category));
        }
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db, ServiceTestBase.Time(Beijing(17, 20)));
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);

        var totalHours = res.ProductiveHours + res.DistractingHours + res.NeutralHours;
        Assert.True(totalHours <= 24.0, $"生产力合计 {totalHours} 小时超过 24 小时物理上限");
        Assert.InRange(res.TodayScore, 0, 100);
    }

    /// <summary>同一时刻只归属一个生产力档位：重叠的 productive / distracting 不得都计入。</summary>
    [Fact]
    public async Task ProductivityDashboard_OverlappingDifferentBuckets_OneInstantCountsOnce()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 编程 10:00-11:00（productive, window 高置信）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("prod", Beijing(17, 10), Beijing(17, 11), "编程/折腾", "window", 0.9));
        // 游戏 10:00-11:00（重叠，input-minute 低置信）→ 应被 window 覆盖
        db.Set<ActivityClassificationEntity>().Add(Snapshot("dist", Beijing(17, 10), Beijing(17, 11), "游戏", "input-minute", 0.2));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db, ServiceTestBase.Time(Beijing(17, 20)));
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);

        Assert.InRange(res.ProductiveHours, 0.9, 1.1);
        Assert.InRange(res.DistractingHours, 0.0, 0.05);
    }

    // ================= review 修复：两个 resolver 口径必须一致 =================

    /// <summary>
    /// 分类分布/生产力用的 <see cref="PcActivityOverlapResolver"/> 与时间线 v2 用的
    /// <see cref="PcTimelineOverlapResolver"/> 必须对同一输入给出同一胜者
    /// （review 发现两套规则曾分别以「记录类型优先级」和「置信度」为首要键）。
    /// </summary>
    [Fact]
    public void Resolvers_AgreeOnWinnerForSameInput()
    {
        var start = Beijing(17, 10);
        var end = Beijing(17, 11);

        // A：window（高优先级）但低置信；B：web-page（低优先级）但高置信
        var shared = new List<PcActivityOverlapResolver.Candidate>
        {
            new(start, end, "window", 0.2, "key-a"),
            new(start, end, "web-page", 0.9, "key-b"),
        };
        var timeline = new List<PcTimelineOverlapResolver.Candidate>
        {
            new(start, end, 0.2, "key-a", "window"),
            new(start, end, 0.9, "key-b", "web-page"),
        };

        var sharedWinner = Assert.Single(PcActivityOverlapResolver.Resolve(shared)).WinnerIndex;
        var timelineWinner = Assert.Single(PcTimelineOverlapResolver.Resolve(timeline, TimeSpan.Zero)).WinnerIndex;

        Assert.Equal(sharedWinner, timelineWinner);
        // 记录类型优先级是首要键 → window 胜出
        Assert.Equal(0, sharedWinner);
    }

    /// <summary>时间线候选不带记录类型时退化为「置信度 → 时长 → 稳定键」，保持其历史行为。</summary>
    [Fact]
    public void TimelineResolver_WithoutRecordType_FallsBackToConfidence()
    {
        var start = Beijing(17, 10);
        var end = Beijing(17, 11);
        var candidates = new List<PcTimelineOverlapResolver.Candidate>
        {
            new(start, end, 0.2, "key-a"),
            new(start, end, 0.9, "key-b"),
        };

        var winner = Assert.Single(PcTimelineOverlapResolver.Resolve(candidates, TimeSpan.Zero)).WinnerIndex;
        Assert.Equal(1, winner); // 置信度 0.9 者胜
    }

    /// <summary>
    /// 长区间查询不得退化为逐日全量扫描（review 发现的 Important）。
    /// <para>
    /// 用**确定性**的规模比例而非墙钟阈值来判定：查询 365 天但记录只落在 3 天内。
    /// 建索引后，每天只处理与之重叠的记录（3 天 × 每天 2000 条），总候选构造量约 6000；
    /// 旧的「逐日全量扫描」会对 365 天各扫描全部 6000 条，约 219 万次候选构造 —— 相差约 365 倍。
    /// 这比绝对耗时阈值更可靠（不受 CI 负载影响），且实测旧实现会超时/明显变慢。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProductivityRange_LongRange_OnlyResolvesDaysWithData()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 记录只落在 2026-01-01..03 三天内，每天 2000 条；查询跨度却是整年。
        const int perDay = 2000;
        for (var d = 0; d < 3; d++)
        {
            var baseDay = new DateTime(2026, 1, 1).AddDays(d);
            for (var i = 0; i < perDay; i++)
            {
                var start = new DateTimeOffset(baseDay.AddHours(5).AddSeconds(i * 20), TimeSpan.FromHours(8));
                db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
                {
                    Id = Guid.NewGuid(),
                    RecordKey = $"perf-{d}-{i}",
                    RecordType = "window",
                    DeviceId = "pc-1",
                    StartedAt = start.ToUniversalTime(),
                    EndedAt = start.AddSeconds(15).ToUniversalTime(),
                    CategoryName = i % 2 == 0 ? "编程/折腾" : "游戏",
                    CategoryColor = "#10b981",
                    Confidence = 0.8,
                    Source = "rule",
                    ClassifierVersion = "v1",
                    ClassifiedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db, ServiceTestBase.Time(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await svc.GetRangeAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), CancellationToken.None);
        sw.Stop();

        // 只有 3 天有数据；其余日期因总时长 <= 0 被过滤。
        Assert.Equal(3, res.Count);
        foreach (var day in res)
        {
            Assert.True(day.TotalMinutes > 0, $"{day.Date} 不应出现空结果");
            Assert.True(day.TotalMinutes <= 24 * 60, $"{day.Date} 合计超过 24 小时");
        }

        // 确定性判据（不依赖墙钟）：逐日消解阶段累计扫描的记录条数。
        // 建索引后只统计「有数据且与当日重叠」的记录：3 天 × 2000 条 = 6000；
        // 旧的逐日全量扫描会是 365 天 × 6000 条 = 2,190,000 —— 相差约 365 倍。
        Assert.True(svc.LastRangeScannedRecordCount <= 3 * perDay + 10,
            $"逐日消解扫描了 {svc.LastRangeScannedRecordCount} 条记录，疑似仍为逐日全量扫描" +
            $"（预期约 {3 * perDay}）");
        // 同时保留一个宽松的耗时上限，防止出现「计数正常但仍异常缓慢」的情况。
        Assert.True(sw.ElapsedMilliseconds < 20_000,
            $"365 天区间查询耗时 {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>gap / idle 不参与生产力统计。</summary>
    [Fact]
    public async Task ProductivityDashboard_GapAndIdle_AreExcluded()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("prod", Beijing(17, 10), Beijing(17, 11), "编程/折腾"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("gap", Beijing(17, 4), Beijing(17, 9), "游戏", "gap"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db, ServiceTestBase.Time(Beijing(17, 20)));
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);

        Assert.InRange(res.ProductiveHours, 0.9, 1.1);
        Assert.InRange(res.DistractingHours, 0.0, 0.05);
    }

    /// <summary>无按键点击数据时份额为 0，不得除零。</summary>
    [Fact]
    public async Task AppRanking_WithoutKeystats_ReturnsEmptyWithoutDivideByZero()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Empty(res.AppRanking);
    }
}
