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
/// PC 时间线 v2（#235 / #236 / #237，EPIC #254 组 3）。
/// 业务日口径：D = [D 04:00, D+1 04:00) Asia/Shanghai（EPIC #254 §10 D-1）。
/// 测试日 2026-07-07 的业务日窗口 = UTC [2026-07-06 20:00, 2026-07-07 20:00)。
/// 北京时间 10:00 == UTC 当日 02:00。
/// </summary>
public sealed class PcTimelineV2Tests
{
    private static readonly DateTime TestDate = new(2026, 7, 7);

    /// <summary>业务日窗口内的一个北京时间时刻 → UTC DateTimeOffset。</summary>
    private static DateTimeOffset Beijing(int day, int hour, int minute = 0, int second = 0)
        => new(new DateTime(2026, 7, day, hour, minute, second, DateTimeKind.Utc).AddHours(-8), TimeSpan.Zero);

    private static ActivityClassificationEntity Snapshot(
        string recordKey,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string categoryName = "工作",
        string categoryColor = "#10b981",
        double confidence = 0.9,
        string? appName = null,
        string? appDisplayName = null,
        string? windowTitle = null)
        => new()
        {
            Id = Guid.NewGuid(),
            RecordKey = recordKey,
            RecordType = "window",
            DeviceId = "pc-1",
            StartedAt = startedAt,
            EndedAt = endedAt,
            CategoryName = categoryName,
            CategoryColor = categoryColor,
            Confidence = confidence,
            Source = "rule",
            ClassifierVersion = "v1",
            ClassifiedAt = DateTimeOffset.UtcNow,
            AppName = appName,
            AppDisplayName = appDisplayName,
            WindowTitle = windowTitle
        };

    private static TrackerEventEntity WindowEvent(
        DateTimeOffset timestamp,
        double durationSeconds,
        string? appName = null,
        string? displayName = null,
        string? windowTitle = null)
        => new()
        {
            DeviceId = "pc-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = appName,
            DisplayName = displayName,
            WindowTitle = windowTitle,
            CreatedAt = timestamp
        };

    private static void SeedSignature(PimDbContext db, string processName, string displayName)
        => db.Set<AppSignatureEntity>().Add(new AppSignatureEntity
        {
            Id = Guid.NewGuid(),
            ProcessName = processName,
            DisplayName = displayName
        });

    // ================= #237 时间块重叠 =================

    [Fact]
    public async Task TimelineV2_OverlappingSnapshots_ReturnsNoOverlappingBlocks()
    {
        await using var db = ServiceTestBase.CreateDb();
        // A: 北京 10:00-11:00（编程，高置信）；B: 北京 10:30-11:30（视频，低置信）—— 交叠 30 分钟
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-b", Beijing(7, 10, 30), Beijing(7, 11, 30), "视频", "#ef4444", 0.5));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        for (var i = 1; i < res.Count; i++)
            Assert.True(res[i].Start >= res[i - 1].End,
                $"块 {i} 与块 {i - 1} 重叠：{res[i - 1].End:O} > {res[i].Start:O}");
    }

    [Fact]
    public async Task TimelineV2_OverlappingSnapshots_TotalDurationDoesNotExceedSpan()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-b", Beijing(7, 10, 30), Beijing(7, 11, 30), "视频", "#ef4444", 0.5));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var totalMinutes = res.Sum(x => x.DurationMinutes);
        var spanMinutes = (res.Max(x => x.End) - res.Min(x => x.Start)).TotalMinutes;
        Assert.True(totalMinutes <= spanMinutes + 0.05,
            $"内容合计 {totalMinutes} 分钟 > 时间跨度 {spanMinutes} 分钟");
        // 并集恰好 90 分钟（10:00-11:30）
        Assert.InRange(totalMinutes, 89.9, 90.1);
    }

    [Fact]
    public async Task TimelineV2_OverlappingSnapshots_HigherConfidenceWinsContestedRegion()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-b", Beijing(7, 10, 30), Beijing(7, 11, 30), "视频", "#ef4444", 0.5));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        // 10:45 落在交叠区（10:30-11:00）→ 应由高置信的「编程」独占
        var contested = Beijing(7, 10, 45);
        var owner = res.Single(x => x.Start <= contested && contested < x.End);
        Assert.Equal("productive", owner.Productivity);
        Assert.Equal("编程", owner.CategoryName);

        // 11:15 只属于 B → 视频
        var bOnly = Beijing(7, 11, 15);
        var bOwner = res.Single(x => x.Start <= bOnly && bOnly < x.End);
        Assert.Equal("distracting", bOwner.Productivity);
    }

    [Fact]
    public async Task TimelineV2_AdjacentSegmentsOfSameApp_AreMerged()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 同一应用、被低置信交叠块打断 → 高置信块的两段应合并为一个块
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-b", Beijing(7, 10, 20), Beijing(7, 10, 40), "视频", "#ef4444", 0.5));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var productive = res.Where(x => x.Productivity == "productive").ToList();
        Assert.Single(productive);
        Assert.InRange(productive[0].DurationMinutes, 59.9, 60.1);
    }

    [Fact]
    public async Task TimelineV2_SubMinuteSliver_IsDropped()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-main", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        // 2 秒的碎片，独占 11:00-11:00:02
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-sliver", Beijing(7, 11), Beijing(7, 11, 0, 2), "视频", "#ef4444", 0.5));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        Assert.DoesNotContain(res, x => x.DurationMinutes <= 0);
        Assert.Single(res);
    }

    [Fact]
    public async Task TimelineV2_NonOverlappingSnapshots_ArePreserved()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-b", Beijing(7, 11), Beijing(7, 12), "视频", "#ef4444", 0.9));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        Assert.Equal(2, res.Count);
        Assert.Equal(60, res[0].DurationMinutes, 1);
        Assert.Equal(60, res[1].DurationMinutes, 1);
    }

    // ================= #235 应用名 / 窗口标题 =================

    [Fact]
    public async Task TimelineV2_SnapshotAppIdentity_IsReturned()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-code", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9,
            appName: "Code.exe", appDisplayName: "Visual Studio Code", windowTitle: "PcProductivityService.cs"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("Code.exe", block.AppName);
        Assert.Equal("Visual Studio Code", block.AppDisplayName);
        Assert.Equal("PcProductivityService.cs", block.WindowTitle);
        Assert.DoesNotContain("pc-fallback-v1:", block.AppName);
    }

    [Fact]
    public async Task TimelineV2_MissingDisplayName_FallsBackToSignatureTable()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedSignature(db, "chrome.exe", "Google Chrome");
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-chrome", Beijing(7, 10), Beijing(7, 11), "文档", "#3b82f6", 0.9,
            appName: "chrome.exe", windowTitle: "文档 - Google Chrome"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("chrome.exe", block.AppName);
        Assert.Equal("Google Chrome", block.AppDisplayName);
    }

    [Fact]
    public async Task TimelineV2_LegacySnapshot_ResolvesAppIdentityFromNativeEvents()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 历史行：没有 app_name / window_title（迁移前写入的快照）
        db.Set<ActivityClassificationEntity>().Add(Snapshot("pc-fallback-v1:980d10d84b1dad665a9bb79a7bf125f0", Beijing(7, 10), Beijing(7, 11), "文档", "#3b82f6", 0.9));
        db.Set<TrackerEventEntity>().Add(WindowEvent(Beijing(7, 10), 3600, appName: "firefox", displayName: "Firefox", windowTitle: "PIM 文档"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("firefox", block.AppName);          // 进程名
        Assert.Equal("Firefox", block.AppDisplayName);   // 人类可读名
        Assert.Equal("PIM 文档", block.WindowTitle);
        Assert.DoesNotContain("pc-fallback-v1:", block.AppDisplayName!);
    }

    [Fact]
    public async Task TimelineV2_LegacySnapshot_PicksLongestOverlappingEvent()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("pc-fallback-v1:abc", Beijing(7, 10), Beijing(7, 11), "文档", "#3b82f6", 0.9));
        // 覆盖 10 秒的次要事件 vs 覆盖 50 分钟的窗口事件 → 取重叠更长的
        db.Set<TrackerEventEntity>().Add(WindowEvent(Beijing(7, 10, 59), 10, appName: "notepad.exe", windowTitle: "临时"));
        db.Set<TrackerEventEntity>().Add(WindowEvent(Beijing(7, 10, 5), 3300, appName: "firefox", displayName: "Firefox", windowTitle: "主窗口"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("firefox", block.AppName);
        Assert.Equal("Firefox", block.AppDisplayName);
        Assert.Equal("主窗口", block.WindowTitle);
    }

    [Fact]
    public async Task TimelineV2_CuratedSignatureName_WinsOverStoredDisplayName()
    {
        // 口径一致性：显示名以 pc_app_signatures 为准（与应用时长 Top 面板同源），
        // 避免同一应用在时间线上出现 "chrome" / "Chrome" 两种写法。
        await using var db = ServiceTestBase.CreateDb();
        SeedSignature(db, "chrome.exe", "Google Chrome");
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-chrome", Beijing(7, 10), Beijing(7, 11), "文档", "#3b82f6", 0.9,
            appName: "chrome.exe", appDisplayName: "chrome", windowTitle: "PIM 文档"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("chrome.exe", block.AppName);
        Assert.Equal("Google Chrome", block.AppDisplayName);
    }

    [Fact]
    public async Task TimelineV2_StoredDisplayName_UsedWhenSignatureMissing()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-custom", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9,
            appName: "custom-tool.exe", appDisplayName: "自研工具", windowTitle: "构建"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("custom-tool.exe", block.AppName);
        Assert.Equal("自研工具", block.AppDisplayName);
    }

    [Fact]
    public async Task TimelineV2_LegacySnapshotWithoutEvents_KeepsRecordKey()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("pc-fallback-v1:no-events", Beijing(7, 10), Beijing(7, 11), "文档", "#3b82f6", 0.9));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("pc-fallback-v1:no-events", block.AppName);
        Assert.Equal("pc-fallback-v1:no-events", block.AppDisplayName);
        Assert.Null(block.WindowTitle);
    }

    [Fact]
    public async Task TimelineV2_SnapshotWindowTitle_IsFilledFromEventsWhenMissing()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-code", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.9,
            appName: "Code.exe", appDisplayName: "Visual Studio Code"));
        db.Set<TrackerEventEntity>().Add(WindowEvent(Beijing(7, 10), 3600, appName: "Code.exe", windowTitle: "Program.cs"));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal("Visual Studio Code", block.AppDisplayName);
        Assert.Equal("Program.cs", block.WindowTitle);
    }

    // ================= #236 时间戳时区 =================

    [Fact]
    public async Task TimelineV2_Timestamps_CarryBeijingOffset()
    {
        await using var db = ServiceTestBase.CreateDb();
        // 北京 2026-07-07 10:39:24 == UTC 02:39:24（issue #236 的复现值）
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "k-a", Beijing(7, 10, 39, 24), Beijing(7, 10, 40, 24), "编程", "#10b981", 0.9));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal(TimeSpan.FromHours(8), block.Start.Offset);
        Assert.Equal(TimeSpan.FromHours(8), block.End.Offset);
        Assert.Equal(new DateTime(2026, 7, 7, 10, 39, 24), block.Start.DateTime);
        Assert.Equal(TimeSpan.FromHours(-8), block.Start.UtcDateTime - new DateTime(2026, 7, 7, 10, 39, 24));
    }

    [Fact]
    public async Task TimelineV2_Timestamps_RepresentSameInstantAsSourceSnapshot()
    {
        await using var db = ServiceTestBase.CreateDb();
        var utcStart = Beijing(7, 10);
        db.Set<ActivityClassificationEntity>().Add(Snapshot("k-a", utcStart, utcStart.AddHours(1), "编程", "#10b981", 0.9));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(res);
        Assert.Equal(utcStart.UtcDateTime, block.Start.UtcDateTime);
        Assert.Equal(TimeSpan.FromHours(1), block.End - block.Start);
    }

    // ================= #236 业务日「今天」口径（D-1） =================

    [Fact]
    public void ResolveBusinessDay_ExplicitDate_IsReturnedAsIs()
    {
        using var db = ServiceTestBase.CreateDb();
        var svc = new PcProductivityService(db);
        Assert.Equal(new DateTime(2026, 9, 13), svc.ResolveBusinessDay(new DateTime(2026, 9, 13, 23, 30, 0)));
    }

    [Theory]
    // 北京时间凌晨 0-4 点仍属前一业务日（EPIC #254 D-1）
    [InlineData("2026-09-13T16:30:00Z", "2026-09-13")] // 北京 09-14 00:30 → 业务日 09-13
    [InlineData("2026-09-13T19:59:00Z", "2026-09-13")] // 北京 09-14 03:59 → 业务日 09-13
    [InlineData("2026-09-13T20:00:00Z", "2026-09-14")] // 北京 09-14 04:00 → 业务日 09-14
    [InlineData("2026-09-14T10:00:00Z", "2026-09-14")] // 北京 09-14 18:00 → 业务日 09-14
    public void ResolveBusinessDay_OmittedDate_UsesBusinessDayOfNow(string utcNow, string expected)
    {
        var db = ServiceTestBase.CreateDb();
        var svc = new PcProductivityService(
            db,
            ServiceTestBase.Time(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), svc.ResolveBusinessDay(null));
    }

    /// <summary>
    /// 同起始时间、同置信度、同时长、不同 record_key 的两条快照：胜者由稳定键序决定，
    /// 与数据库对相同 StartedAt 行的返回顺序无关（查询已追加 ThenBy(Id)）。
    /// </summary>
    [Fact]
    public async Task TimelineV2_IdenticalIntervals_ResolveDeterministically()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(Snapshot("key-b", Beijing(7, 10), Beijing(7, 11), "视频", "#ef4444", 0.8));
        db.Set<ActivityClassificationEntity>().Add(Snapshot("key-a", Beijing(7, 10), Beijing(7, 11), "编程", "#10b981", 0.8));
        await db.SaveChangesAsync();

        var svc = new PcProductivityService(db);
        var first = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);
        var second = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);

        var block = Assert.Single(first);
        // 稳定键序小者胜出（"key-a" < "key-b"）
        Assert.Equal("key-a", block.AppName);
        Assert.Equal("编程", block.CategoryName);
        Assert.Equal(block.AppName, Assert.Single(second).AppName);
    }

    // ================= 不变量：随机负载下依然成立 =================

    /// <summary>
    /// 生成器式不变量（固定种子，可复现）：任意重叠形状的输入，输出必须
    /// ①按 start 升序 ②两两不重叠 ③时长合计 ≤ 输入并集跨度 ④不产生 0 分钟块。
    /// 覆盖 #237 描述的「内容合计 &gt; 时间跨度」这一失效模式。
    /// </summary>
    [Fact]
    public void Resolver_RandomOverlappingIntervals_UpholdsTimelineInvariants()
    {
        var random = new Random(20260914);
        var origin = new DateTimeOffset(2026, 7, 7, 2, 0, 0, TimeSpan.Zero);

        for (var round = 0; round < 200; round++)
        {
            var count = random.Next(1, 60);
            var candidates = new List<PcTimelineOverlapResolver.Candidate>(count);
            for (var i = 0; i < count; i++)
            {
                var startOffset = random.Next(0, 600);
                var length = random.Next(1, 120);
                candidates.Add(new PcTimelineOverlapResolver.Candidate(
                    origin.AddMinutes(startOffset),
                    origin.AddMinutes(startOffset + length),
                    Math.Round(random.NextDouble(), 3),
                    $"key-{i:D3}"));
            }

            var segments = PcTimelineOverlapResolver.Resolve(candidates, TimeSpan.FromSeconds(60));

            for (var i = 1; i < segments.Count; i++)
                Assert.True(segments[i].Start >= segments[i - 1].End,
                    $"round {round}: 块 {i} 与前一块重叠");

            var totalMinutes = segments.Sum(s => (s.End - s.Start).TotalMinutes);
            var unionSpanMinutes = UnionSpanMinutes(candidates);
            Assert.True(totalMinutes <= unionSpanMinutes + 0.001,
                $"round {round}: 内容合计 {totalMinutes} > 并集跨度 {unionSpanMinutes}");

            Assert.DoesNotContain(segments, s => s.End - s.Start < TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// 确定性回归（review 发现）：候选输入顺序不得影响输出。
    /// 时间线场景的 StableKey 是 record_key，该列有唯一索引，故胜出规则是全序；
    /// 这里穷举全部排列，断言「时间段 + 归属键」完全一致。
    /// </summary>
    [Fact]
    public void Resolver_IsIndependentOfCandidateInputOrder()
    {
        var origin = new DateTimeOffset(2026, 7, 7, 2, 0, 0, TimeSpan.Zero);
        var baseline = new List<PcTimelineOverlapResolver.Candidate>
        {
            new(origin, origin.AddMinutes(60), 0.9, "key-a"),
            new(origin.AddMinutes(30), origin.AddMinutes(90), 0.5, "key-b"),
            new(origin.AddMinutes(45), origin.AddMinutes(75), 0.5, "key-c"),
            new(origin.AddMinutes(80), origin.AddMinutes(120), 0.9, "key-d"),
            // 同起止、同置信度的并列对：此时输入次序会变，只有稳定键序决胜能让结果保持一致
            new(origin.AddMinutes(100), origin.AddMinutes(140), 0.5, "key-f"),
            new(origin.AddMinutes(100), origin.AddMinutes(140), 0.5, "key-e"),
        };

        var expected = Fingerprint(baseline, PcTimelineOverlapResolver.Resolve(baseline, TimeSpan.FromSeconds(60)));

        foreach (var permutation in Permutations(baseline))
        {
            var actual = Fingerprint(permutation, PcTimelineOverlapResolver.Resolve(permutation, TimeSpan.FromSeconds(60)));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>StableKey 相同时不得出现「随机」胜者：同一输入重复求解结果必须一致。</summary>
    [Fact]
    public void Resolver_DuplicateStableKeys_AreStillRepeatable()
    {
        var origin = new DateTimeOffset(2026, 7, 7, 2, 0, 0, TimeSpan.Zero);
        var candidates = new List<PcTimelineOverlapResolver.Candidate>
        {
            new(origin, origin.AddMinutes(60), 0.8, "same-key"),
            new(origin, origin.AddMinutes(60), 0.8, "same-key"),
        };

        var first = PcTimelineOverlapResolver.Resolve(candidates, TimeSpan.FromSeconds(60));
        for (var i = 0; i < 20; i++)
            Assert.Equal(Fingerprint(candidates, first), Fingerprint(candidates, PcTimelineOverlapResolver.Resolve(candidates, TimeSpan.FromSeconds(60))));
    }

    /// <summary>把消解结果归一化为「归属键 + 起止」序列，避免比较下标（下标随输入排列变化）。</summary>
    private static string Fingerprint(
        IReadOnlyList<PcTimelineOverlapResolver.Candidate> candidates,
        IReadOnlyList<PcTimelineOverlapResolver.Segment> segments)
        => string.Join(";", segments.Select(s =>
            $"{s.Start:O}~{s.End:O}~{candidates[s.WinnerIndex].StableKey}"));

    private static IEnumerable<List<PcTimelineOverlapResolver.Candidate>> Permutations(
        List<PcTimelineOverlapResolver.Candidate> source)
    {
        if (source.Count <= 1)
        {
            yield return source;
            yield break;
        }

        for (var i = 0; i < source.Count; i++)
        {
            var head = source[i];
            var rest = source.Where((_, index) => index != i).ToList();
            foreach (var tail in Permutations(rest))
            {
                var result = new List<PcTimelineOverlapResolver.Candidate> { head };
                result.AddRange(tail);
                yield return result;
            }
        }
    }

    private static double UnionSpanMinutes(IReadOnlyList<PcTimelineOverlapResolver.Candidate> candidates)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var candidate in candidates.OrderBy(c => c.Start).ThenBy(c => c.End))
        {
            if (merged.Count > 0 && candidate.Start <= merged[^1].End)
            {
                if (candidate.End > merged[^1].End)
                    merged[^1] = (merged[^1].Start, candidate.End);
                continue;
            }
            merged.Add((candidate.Start, candidate.End));
        }
        return merged.Sum(m => (m.End - m.Start).TotalMinutes);
    }
}
