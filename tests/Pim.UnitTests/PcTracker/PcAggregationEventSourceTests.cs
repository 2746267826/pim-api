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
/// #303 回归护栏（同类缺陷扫描）：专注块 / 应用时长 / 深夜使用三个聚合端点原先与
/// 「概览四项指标」一样只读 <c>pc_aw_events</c>。该表在原生 tracker 切换后已停止写入，
/// 因此在 tracker-only 日期这三个端点同样恒为空 —— 页面上的记录时长来自新来源，
/// 专注块 / 应用排行 / 深夜使用却是空的，彼此不自洽。
///
/// 本用例锁定：三个端点都必须读取当前在用的数据源（tracker），并在两路来源重叠时去重。
/// 业务日口径：D = [D 04:00, D+1 04:00) Asia/Shanghai。
/// </summary>
public sealed class PcAggregationEventSourceTests
{
    /// <summary>业务日窗口内的一个北京时间时刻 → UTC DateTimeOffset。</summary>
    private static DateTimeOffset Beijing(int day, int hour, int minute = 0, int second = 0)
        => new(new DateTime(2026, 9, day, hour, minute, second, DateTimeKind.Utc).AddHours(-8), TimeSpan.Zero);

    private static TrackerEventEntity TrackerWindow(DateTimeOffset timestamp, double durationSeconds, string appName)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = appName,
            DisplayName = appName,
            WindowTitle = appName,
            CreatedAt = timestamp,
            Date = new DateTime(2026, 9, 17),
        };

    private static AwEventEntity AwWindow(DateTimeOffset timestamp, double durationSeconds, string appName)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = appName,
            WindowTitle = appName,
            DataJson = "{}",
            BucketType = "currentwindow",
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

    private static PcAggregationQuery Day(string date = "2026-09-17")
        => new(date, null, null, null);

    // ================= 专注块 =================

    /// <summary>只有 tracker 事件时，专注块不得为空（原先只读 AW → 恒空）。</summary>
    [Fact]
    public async Task FocusBlocks_TrackerEventsOnly_AreReturned()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 10:00-10:40 连续 code.exe（≥10 分钟专注块下限）
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 1200, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10, 20), 1200, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(Day(), CancellationToken.None);

        var block = Assert.Single(res.Items);
        Assert.InRange(block.DurationMinutes, 39, 41);
        Assert.Equal("code", block.MainApp); // AppNameNormalizer 去掉 .exe 后缀
    }

    /// <summary>两路来源覆盖同一事件时去重，专注块时长不翻倍。</summary>
    [Fact]
    public async Task FocusBlocks_DuplicateAcrossSources_CountedOnce()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 1200, "code.exe"));
        db.Set<AwEventEntity>().Add(AwWindow(Beijing(17, 10), 1200, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(Day(), CancellationToken.None);

        var block = Assert.Single(res.Items);
        Assert.InRange(block.DurationMinutes, 19, 21);
    }

    // ================= 应用时长 Top =================

    /// <summary>只有 tracker 事件时，应用时长排行不得为空。</summary>
    [Fact]
    public async Task AppUsage_TrackerEventsOnly_AreReturned()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 1800, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 11), 900, "firefox.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(Day(), null, CancellationToken.None);

        Assert.Equal(2, res.Items.Count);
        Assert.Equal("code", res.Items[0].AppName); // AppNameNormalizer 去掉 .exe 后缀
        Assert.InRange(res.Items[0].TotalMinutes, 29, 31);
        Assert.InRange(res.TotalMinutes, 44, 46);
    }

    /// <summary>应用时长份额不再以第一名归一：合计 ≈ 100%。</summary>
    [Fact]
    public async Task AppUsage_PercentagesSumToHundred()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 1800, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 11), 1800, "firefox.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(Day(), null, CancellationToken.None);

        Assert.InRange(res.Items.Sum(i => i.Percentage), 99.0, 101.0);
        // 两个应用各占约一半，不应出现 100%
        Assert.All(res.Items, i => Assert.True(i.Percentage < 60, $"份额 {i.Percentage}% 疑似第一名归一"));
    }

    // ================= 深夜使用 =================

    /// <summary>只有 tracker 事件时，深夜使用不得为空。</summary>
    [Fact]
    public async Task LateNight_TrackerEventsOnly_AreReturned()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 23:40-23:59（属深夜段 23:30 之后）
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 23, 40), 1140, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetLateNightAsync(Day(), CancellationToken.None);

        var day = Assert.Single(res.Items);
        Assert.InRange(day.Minutes, 18, 20);
        Assert.True(day.HadActivity);
    }

    /// <summary>深夜段之前的事件计入「有活动」但不计入深夜分钟数。</summary>
    [Fact]
    public async Task LateNight_DaytimeEvents_HadActivityButNoLateMinutes()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 1800, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetLateNightAsync(Day(), CancellationToken.None);

        var day = Assert.Single(res.Items);
        Assert.Equal(0, day.Minutes);
        Assert.True(day.HadActivity);
    }

    // ================= review 修复：长事件不截断、跨业务日边界不漏算 =================

    /// <summary>
    /// 单条事件超过 1 小时不得被截断（review 发现 Important）：聚合端点原用 3600s 上限，
    /// 而镜像中 tracker 存在 4170s / 9560s 的合法前台窗口事件，会与概览指标口径不一致。
    /// </summary>
    [Fact]
    public async Task FocusBlocks_EventLongerThanOneHour_IsNotTruncatedToSixtyMinutes()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 10:00 起 90 分钟
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 5400, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(Day(), CancellationToken.None);

        var block = Assert.Single(res.Items);
        Assert.InRange(block.DurationMinutes, 89, 91);
    }

    /// <summary>应用时长同样不得把超过 1 小时的事件截断到 60 分钟。</summary>
    [Fact]
    public async Task AppUsage_EventLongerThanOneHour_IsNotTruncated()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 5400, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(Day(), null, CancellationToken.None);

        var app = Assert.Single(res.Items);
        Assert.InRange(app.TotalMinutes, 89, 91);
        Assert.InRange(res.TotalMinutes, 89, 91);
    }

    /// <summary>
    /// 起点在业务日窗口之前、但延伸进窗口的事件必须计入（review 发现 Important）：
    /// 只按「起点落在窗口内」筛选会整条丢弃（镜像中有 26 条 AW window 跨 04:00 边界）。
    /// </summary>
    [Fact]
    public async Task FocusBlocks_EventStartingBeforeBusinessDay_IsClippedNotDropped()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 9/17 03:30 起 60 分钟 → 业务日 9/17 只应计入 04:00-04:30 共 30 分钟。
        // 用 ≥10 分钟的专注块下限保证这条记录会形成块。
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 3, 30), 3600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        var block = Assert.Single(res.Items);
        Assert.InRange(block.DurationMinutes, 29, 31);
    }

    /// <summary>起点越界的事件不得把整条时长都算进本业务日（裁剪后合计不超过窗口）。</summary>
    [Fact]
    public async Task AppUsage_EventStartingBeforeBusinessDay_CountsOnlyClippedPart()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 3, 30), 3600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery("2026-09-17", null, null, null), null, CancellationToken.None);

        Assert.InRange(res.TotalMinutes, 29, 31);
    }

    /// <summary>完全落在窗口之外（更早）的事件仍不应计入。</summary>
    [Fact]
    public async Task FocusBlocks_EventEntirelyBeforeBusinessDay_IsIgnored()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 9/17 02:00 起 30 分钟，完全在业务日 04:00 之前
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 2), 1800, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery("2026-09-17", null, null, null), CancellationToken.None);

        Assert.Empty(res.Items);
    }

    // ================= review 修复：热力图网格同样跟随数据源 =================

    /// <summary>
    /// 通用热力图网格（PC 记录页 / MCP get_pc_heatmap）原只读 pc_aw_events，
    /// tracker-only 日期 24 个桶全为 0（实测 283 条 tracker window 事件仍显示 0），
    /// 与同页其它区块自相矛盾（review 发现 Important）。
    /// </summary>
    [Fact]
    public async Task HeatmapGrid_TrackerEventsOnly_CountsEvents()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 600, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10, 30), 600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(new DateTime(2026, 9, 17), new DateTime(2026, 9, 17), "hour", CancellationToken.None);

        var row = Assert.Single(res.Grid);
        Assert.Equal(24, row.Count);
        Assert.Equal(2, row.Sum(b => b.TotalEvents));
    }

    /// <summary>
    /// 两路来源记录同一条底层事件时（迁移期双写 / 重复上传），热力图只应计一次 ——
    /// 否则事件数与 keyCount 的分母会一起翻倍（#303 review）。
    /// </summary>
    [Fact]
    public async Task HeatmapGrid_DuplicateAcrossSources_CountedOnce()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 600, "code.exe"));
        db.Set<AwEventEntity>().Add(AwWindow(Beijing(17, 10), 600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(new DateTime(2026, 9, 17), new DateTime(2026, 9, 17), "hour", CancellationToken.None);

        var row = Assert.Single(res.Grid);
        Assert.Equal(1, row.Sum(b => b.TotalEvents));
    }

    /// <summary>热力图只统计窗口事件：两路来源的不同事件正常累加。</summary>
    [Fact]
    public async Task HeatmapGrid_DistinctEventsAcrossSources_AreBothCounted()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 600, "code.exe"));
        db.Set<AwEventEntity>().Add(AwWindow(Beijing(17, 11), 600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(new DateTime(2026, 9, 17), new DateTime(2026, 9, 17), "hour", CancellationToken.None);

        var row = Assert.Single(res.Grid);
        Assert.Equal(2, row.Sum(b => b.TotalEvents));
    }

    /// <summary>gap / idle 不属于前台窗口，不计入热力图事件数。</summary>
    [Fact]
    public async Task HeatmapGrid_GapAndIdle_AreExcluded()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 10), 600, "code.exe"));
        db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "pc-1", Timestamp = Beijing(17, 11), Duration = 600,
            EventType = "gap", CreatedAt = Beijing(17, 11), Date = new DateTime(2026, 9, 17),
        });
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(new DateTime(2026, 9, 17), new DateTime(2026, 9, 17), "hour", CancellationToken.None);

        var row = Assert.Single(res.Grid);
        Assert.Equal(1, row.Sum(b => b.TotalEvents));
    }

    /// <summary>
    /// 跨业务日边界的长事件：两条不同起点的长事件各自裁剪后，本业务日内的覆盖应是它们的**并集**
    /// （04:00–07:30 = 210 分钟），不能被裁剪逻辑漏算或重复计入。
    /// </summary>
    [Fact]
    public async Task AppUsage_LongEventsClippedToBusinessDay_UnionIsCorrect()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 业务日 9/17 窗口 = 北京 [9/17 04:00, 9/18 04:00)
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 2), 4 * 3600, "code.exe"));      // 覆盖 04:00-06:00
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 3, 30), 4 * 3600, "code.exe")); // 覆盖 04:00-07:30
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery("2026-09-17", null, null, null), null, CancellationToken.None);

        Assert.InRange(res.TotalMinutes, 209, 211);
    }

    /// <summary>
    /// 预裁剪时长的长事件也要被正确裁剪到业务日：起点在 04:00 前、时长跨越整个上午时，
    /// 本日只应计入窗口内的部分，不得把窗口外时间算进来。
    /// </summary>
    [Fact]
    public async Task AppUsage_EventSpanningBeyondWindow_CountsOnlyInWindowPart()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 9/17 02:00 起 8h（至 10:00）；业务日 04:00 起 → 本日应计 04:00-10:00 = 360 分钟
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(17, 2), 8 * 3600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery("2026-09-17", null, null, null), null, CancellationToken.None);

        Assert.InRange(res.TotalMinutes, 359, 361);
    }

    // ================= 无数据时保持为空 =================

    [Fact]
    public async Task AllEndpoints_NoEvents_ReturnEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);

        Assert.Empty((await svc.GetFocusBlocksAsync(Day(), CancellationToken.None)).Items);
        Assert.Empty((await svc.GetAppUsageAsync(Day(), null, CancellationToken.None)).Items);
        Assert.Equal(0, (await svc.GetAppUsageAsync(Day(), null, CancellationToken.None)).TotalMinutes);
        Assert.All((await svc.GetLateNightAsync(Day(), CancellationToken.None)).Items, i => Assert.False(i.HadActivity));
    }

    /// <summary>gap / idle 不属于前台窗口，不参与这三个端点。</summary>
    [Fact]
    public async Task Endpoints_GapAndIdleTrackerEvents_AreIgnored()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "pc-1", Timestamp = Beijing(17, 9), Duration = 7200,
            EventType = "gap", CreatedAt = Beijing(17, 9), Date = new DateTime(2026, 9, 17),
        });
        db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "pc-1", Timestamp = Beijing(17, 23, 40), Duration = 1140,
            EventType = "idle", CreatedAt = Beijing(17, 23, 40), Date = new DateTime(2026, 9, 17),
        });
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcAggregationService(db);

        Assert.Empty((await svc.GetFocusBlocksAsync(Day(), CancellationToken.None)).Items);
        Assert.Empty((await svc.GetAppUsageAsync(Day(), null, CancellationToken.None)).Items);
        Assert.Equal(0, Assert.Single((await svc.GetLateNightAsync(Day(), CancellationToken.None)).Items).Minutes);
    }
}
