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
