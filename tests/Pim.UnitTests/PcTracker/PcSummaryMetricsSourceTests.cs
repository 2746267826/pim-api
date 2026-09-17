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
/// #303 回归护栏：「今日」页 PC 概览四项指标（记录时长 / 会话 / 应用数 / 最专注）
/// 必须基于**当前实际在用的数据源**计算。
///
/// 背景：这组指标原先只读 <c>pc_aw_events</c>（EventType == "window"/"afk"），
/// 而该表在原生 tracker 切换后已停止写入（最后一条 2026-08-31），活动事件改为写入
/// <c>pc_tracker_events</c>。于是有按键 / 点击的日期四项指标仍恒为空
/// （记录时长 0m、会话 0 次、应用 0 个、最专注「-」）。
///
/// 业务日口径：D = [D 04:00, D+1 04:00) Asia/Shanghai。
/// 测试日 2026-07-07 的业务日窗口 = UTC [2026-07-06 20:00, 2026-07-07 20:00)。
/// 北京时间 10:00 == UTC 当日 02:00。
/// </summary>
public sealed class PcSummaryMetricsSourceTests
{
    private static readonly DateTime TestDate = new(2026, 7, 7);

    /// <summary>业务日窗口内的一个北京时间时刻 → UTC DateTimeOffset。</summary>
    private static DateTimeOffset Beijing(int day, int hour, int minute = 0, int second = 0)
        => new(new DateTime(2026, 7, day, hour, minute, second, DateTimeKind.Utc).AddHours(-8), TimeSpan.Zero);

    private static TrackerEventEntity TrackerWindow(
        DateTimeOffset timestamp,
        double durationSeconds,
        string? appName,
        string? displayName = null,
        string? windowTitle = null)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = appName,
            DisplayName = displayName ?? appName,
            WindowTitle = windowTitle,
            CreatedAt = timestamp,
            Date = BusinessDateOf(timestamp),
        };

    private static TrackerEventEntity TrackerEvent(
        string eventType,
        DateTimeOffset timestamp,
        double durationSeconds,
        string? appName = null)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = eventType,
            AppName = appName,
            DisplayName = appName,
            CreatedAt = timestamp,
            Date = BusinessDateOf(timestamp),
        };

    /// <summary>与后端一致的业务日归属（本地时间 → 去掉 04:00 前的小时）。</summary>
    private static DateTime BusinessDateOf(DateTimeOffset timestamp)
    {
        var local = timestamp.ToOffset(TimeSpan.FromHours(8));
        return (local.Hour < 4 ? local.Date.AddDays(-1) : local.Date);
    }

    private static void SeedKeystats(PimDbContext db, DateTime date, int keyPresses, int leftClicks)
    {
        db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity
        {
            DeviceId = "pc-1",
            SnapshotDate = date.Date,
            KeyPresses = keyPresses,
            LeftClicks = leftClicks,
            RightClicks = 0,
            MiddleClicks = 0,
            MouseDistance = 0,
            ScrollDistance = 0,
            PeakKps = 5,
            PeakCps = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCounts = new List<KeystatsKeyCountEntity>(),
            AppBreakdowns = new List<KeystatsAppBreakdownEntity>
            {
                new() { AppName = "code.exe", DisplayName = "VS Code", KeyPresses = keyPresses, LeftClicks = leftClicks, RightClicks = 0, MiddleClicks = 0, ScrollDistance = 0 },
            }
        });
    }

    // ================= 核心失效模式：只有 tracker 事件时四项指标不得为空 =================

    /// <summary>
    /// 复现 #303：当日活动事件全部在 <c>pc_tracker_events</c>，<c>pc_aw_events</c> 为空。
    /// 修复前 metrics 恒为 0m / 0 次 / 0 个 / 「-」。
    /// </summary>
    [Fact]
    public async Task GetSummary_TrackerEventsOnly_DerivesMetricsFromTrackerStream()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 3600, "code.exe", "Visual Studio Code", "Program.cs"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 11), 3600, "code.exe", "Visual Studio Code", "Program.cs"));
        SeedKeystats(db, TestDate, keyPresses: 1000, leftClicks: 200);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.NotNull(res.Metrics);
        // 记录时长：10:00-12:00 → 2h
        Assert.Equal("2h", res.Metrics!.TotalRecordedDuration);
        // 应用数：只有 code.exe
        Assert.Equal(1, res.Metrics.ActiveAppCount);
        // 最专注应用：时长最长的 code.exe
        Assert.Equal("code.exe", res.Metrics.MostFocusedApp);
        // 会话数：>=5 分钟，连续无 >15 分钟间隙 → 1 段
        Assert.Equal(1, res.Metrics.SessionCount);
    }

    /// <summary>有 tracker 事件时四项指标必须与「活跃输入」同源自洽，不再恒空。</summary>
    [Fact]
    public async Task GetSummary_TrackerEventsOnly_MetricsAreNotAllEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 9), 1800, "msedge.exe", "Microsoft Edge"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 1800, "Obsidian.exe", "Obsidian"));
        SeedKeystats(db, TestDate, keyPresses: 3000, leftClicks: 100);
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.NotNull(res.Metrics);
        Assert.NotEqual("0m", res.Metrics!.TotalRecordedDuration);
        Assert.NotEqual("-", res.Metrics.MostFocusedApp);
        Assert.True(res.Metrics.SessionCount > 0);
        Assert.Equal(2, res.Metrics.ActiveAppCount);
    }

    // ================= 合并来源：AW 与 tracker 同时存在时不得重复计数 =================

    /// <summary>
    /// 两路数据源同时有覆盖同一时段的 window 事件时，记录时长按**并集**计算，
    /// 不能把重叠部分算两遍（否则「记录时长」会超过 24h 物理上限）。
    /// </summary>
    [Fact]
    public async Task GetSummary_BothSourcesOverlapping_RecordedDurationIsUnionNotSum()
    {
        await using var db = ServiceTestBase.CreateDb();
        // AW：北京 10:00-11:00
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "pc-1",
            Timestamp = Beijing(7, 10),
            Duration = 3600,
            EventType = "window",
            AppName = "code.exe",
            AppNameNormalized = "code.exe",
            WindowTitle = "aw",
            DataJson = "{}",
            BucketType = "currentwindow",
            CreatedAt = Beijing(7, 10),
            UpdatedAt = Beijing(7, 10),
        });
        // tracker：北京 10:30-11:30（与 AW 重叠 30 分钟）
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10, 30), 3600, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        // 并集 10:00-11:30 = 1.5h；两条相加会是 2h
        Assert.Equal("1h 30m", res.Metrics!.TotalRecordedDuration);
    }

    /// <summary>两路数据源的活跃应用取并集，重复应用只算一个。</summary>
    [Fact]
    public async Task GetSummary_BothSources_ActiveAppCountIsDistinctUnion()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "pc-1",
            Timestamp = Beijing(7, 10),
            Duration = 600,
            EventType = "window",
            AppName = "code.exe",
            AppNameNormalized = "code.exe",
            WindowTitle = "aw",
            DataJson = "{}",
            BucketType = "currentwindow",
            CreatedAt = Beijing(7, 10),
            UpdatedAt = Beijing(7, 10),
        });
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 12), 600, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 13), 600, "firefox.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal(2, res.Metrics!.ActiveAppCount);
    }

    // ================= 既有过滤规则保持 =================

    /// <summary>会话下限保持 ≥5 分钟：短于 5 分钟的孤立片段不计入会话数。</summary>
    [Fact]
    public async Task GetSummary_TrackerEvents_ShortSessionUnderFiveMinutes_IsExcluded()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 120, "code.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal(0, res.Metrics!.SessionCount);
    }

    /// <summary>会话切分阈值保持 &gt;15 分钟间隙。</summary>
    [Fact]
    public async Task GetSummary_TrackerEvents_GapOverFifteenMinutes_SplitsSessions()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 300, "code.exe"));
        // 间隔 20 分钟 → 新会话
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10, 25), 300, "firefox.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal(2, res.Metrics!.SessionCount);
    }

    /// <summary>gap / idle / web-page 事件不参与「记录时长 / 应用数 / 最专注」的窗口口径。</summary>
    [Fact]
    public async Task GetSummary_TrackerEvents_GapAndIdleAreExcludedFromWindowMetrics()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 600, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerEvent("gap", Beijing(7, 4), 7200));
        db.Set<TrackerEventEntity>().Add(TrackerEvent("idle", Beijing(7, 6), 3600));
        db.Set<TrackerEventEntity>().Add(TrackerEvent("web-page", Beijing(7, 11), 600, "msedge.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal(1, res.Metrics!.ActiveAppCount);
        Assert.Equal("code.exe", res.Metrics.MostFocusedApp);
        // 记录时长只算 window：10:00-10:10 → 10m
        Assert.Equal("10m", res.Metrics.TotalRecordedDuration);
    }

    // ================= 空闲时长：数据源同样要跟随 tracker =================

    /// <summary>
    /// 空闲时长原先只读 AW 的 <c>afk</c> 事件，同样随 AW 停写而恒为 0m。
    /// tracker 的 idle 事件应被计入。
    /// </summary>
    [Fact]
    public async Task GetSummary_TrackerIdleEvents_ContributeToIdleDuration()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 10), 600, "code.exe"));
        db.Set<TrackerEventEntity>().Add(TrackerEvent("idle", Beijing(7, 11), 1800));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal("30m", res.Metrics!.IdleDuration);
    }

    /// <summary>AW 的 afk 事件仍计入空闲时长（旧数据不丢口径）。</summary>
    [Fact]
    public async Task GetSummary_AwAfkEvents_StillContributeToIdleDuration()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "pc-1",
            Timestamp = Beijing(7, 10),
            Duration = 1200,
            EventType = "afk",
            AfkStatus = "afk",
            DataJson = "{}",
            BucketType = "afk",
            CreatedAt = Beijing(7, 10),
            UpdatedAt = Beijing(7, 10),
        });
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal("20m", res.Metrics!.IdleDuration);
    }

    // ================= 无数据时保持默认 =================

    [Fact]
    public async Task GetSummary_NoEvents_KeepsEmptyDefaults()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal("0m", res.Metrics!.TotalRecordedDuration);
        Assert.Equal("-", res.Metrics.MostFocusedApp);
        Assert.Equal(0, res.Metrics.SessionCount);
        Assert.Equal(0, res.Metrics.ActiveAppCount);
    }

    // ================= 业务日边界 =================

    /// <summary>业务日窗口 [D 04:00, D+1 04:00)：北京 03:30 属前一业务日，不计入本日。</summary>
    [Fact]
    public async Task GetSummary_TrackerEvents_RespectsBusinessDayBoundary()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 7/7 03:30 → 属业务日 7/6
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 3, 30), 600, "code.exe"));
        // 北京 7/7 04:30 → 属业务日 7/7
        db.Set<TrackerEventEntity>().Add(TrackerWindow(Beijing(7, 4, 30), 600, "firefox.exe"));
        await db.SaveChangesAsync();

        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetSummaryAsync(TestDate, CancellationToken.None);

        Assert.Equal(1, res.Metrics!.ActiveAppCount);
        Assert.Equal("firefox.exe", res.Metrics.MostFocusedApp);
    }
}
