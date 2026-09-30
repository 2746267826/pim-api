using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-1（聚合输入覆盖整个业务日）与 REQ-2（块内时长先消解重叠再汇总）。
/// 这些用例在修复前必须失败：聚合输入被 <c>QueryCompleteDetailAsync</c> 的分页上限夹到 200 条，
/// 且块内时长是「逐条相加」而不是「区间并集」。
/// </summary>
public sealed class PcActivityAnalysisCoverageTests
{
    private static readonly DateTime Day = new(2026, 7, 5);

    private readonly ITestOutputHelper _output;

    public PcActivityAnalysisCoverageTests(ITestOutputHelper output) => _output = output;

    private ITestOutputHelper Output => _output;

    [Fact]
    public async Task GetDailyAnalysisAsync_CoversWholeBusinessDayBeyondLegacyPageCap()
    {
        await using var db = CreateDb();
        SeedSparseLateHours(db);
        await db.SaveChangesAsync();

        var analysis = await AnalysisAsync(db);
        var summary = await Tracker(db).GetSummaryAsync(Day, CancellationToken.None);

        var nonZeroBlocks = analysis.Blocks.Where(b => b.ActiveDurationSeconds > 0).ToList();
        var nonZeroHours = summary.Heatmap.Where(b => b.ActiveMinutes > 0).ToList();

        // AC-1.1：非零块数与同业务日 summary.heatmap 的非零小时数一致（复核基线 13；修复前只有 2）。
        Assert.Equal(13, nonZeroBlocks.Count);
        Assert.Equal(nonZeroHours.Count, nonZeroBlocks.Count);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_HourlyActiveSecondsMatchesSummaryHeatmapWithinOneMinute()
    {
        await using var db = CreateDb();
        SeedSparseLateHours(db);
        await db.SaveChangesAsync();

        var analysis = await AnalysisAsync(db);
        var summary = await Tracker(db).GetSummaryAsync(Day, CancellationToken.None);

        // AC-1.1：逐小时 activeDurationSeconds 与 activeMinutes 的差 ≤ 1 分钟（全量对照，不抽样）。
        Assert.Equal(summary.Heatmap.Count, analysis.Blocks.Count);
        for (var i = 0; i < summary.Heatmap.Count; i++)
        {
            var diff = Math.Abs(analysis.Blocks[i].ActiveDurationSeconds - summary.Heatmap[i].ActiveMinutes * 60);
            Assert.True(
                diff <= 60,
                $"块 {analysis.Blocks[i].Start} 活跃秒数 {analysis.Blocks[i].ActiveDurationSeconds} " +
                $"与热力图 {summary.Heatmap[i].ActiveMinutes} 分钟相差 {diff} 秒，超过 1 分钟。");
        }
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_BlockDurationIsIntervalUnionNotSumOfRecords()
    {
        await using var db = CreateDb();
        var blockStart = At("2026-07-05T08:00:00Z");

        // 1 小时 window 记录覆盖整块。
        db.Set<AwEventEntity>().Add(WindowEvent(blockStart, 3600, "Code.exe", "A"));
        // 网页记录与逐分钟输入记录落在同一时段（镜像库同形：90 条记录逐条相加 5960s，并集 3600s）。
        db.Set<AwEventEntity>().Add(WebPageEvent(blockStart.AddMinutes(5), 1200, "docs"));
        db.Set<KeystatsSampleEntity>().AddRange(BuildMinuteSamples(blockStart, minutes: 60, "device-1"));
        await db.SaveChangesAsync();

        var block = SingleBlockAt(await AnalysisAsync(db), blockStart);

        Assert.Equal(3600, block.ActiveDurationSeconds, 3);
        // AC-2.2：apps / categories 合计均 ≤ 块时长，且等于消解后归属时长。
        Assert.All(block.Apps, app => Assert.True(app.DurationSeconds <= 3600));
        Assert.All(block.Categories, category => Assert.True(category.DurationSeconds <= 3600));
        Assert.True(block.Apps.Sum(a => a.DurationSeconds) <= 3600);
        Assert.True(block.Categories.Sum(c => c.DurationSeconds) <= 3600);
        // 最高优先级的 window 记录占满整块，应当独占全部时长（web-page / input-minute 全被消解掉）。
        Assert.Equal(3600, block.Apps.Single().DurationSeconds, 3);
        Assert.Equal("Code.exe", block.Apps.Single().AppName);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_RecordOverlappingBlockBoundaryContributesOnlyClippedPart()
    {
        await using var db = CreateDb();
        var blockStart = At("2026-07-05T08:00:00Z");

        // 07:30 起、持续 1 小时的记录跨过块边界：块内只应计入 08:00–08:30 的 1800 秒。
        db.Set<AwEventEntity>().Add(WindowEvent(blockStart.AddMinutes(-30), 3600, "Code.exe", "A"));
        await db.SaveChangesAsync();

        var block = SingleBlockAt(await AnalysisAsync(db), blockStart);

        Assert.Equal(1800, block.ActiveDurationSeconds, 3);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_InactiveRecordsDoNotContributeDuration()
    {
        await using var db = CreateDb();
        var blockStart = At("2026-07-05T08:00:00Z");

        db.Set<AwEventEntity>().Add(WindowEvent(blockStart, 1800, "Code.exe", "A"));
        db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "device-1",
            Timestamp = blockStart.AddMinutes(30),
            Duration = 1800,
            EventType = "idle",
            AppName = null,
            Date = Day
        });
        await db.SaveChangesAsync();

        var block = SingleBlockAt(await AnalysisAsync(db), blockStart);

        Assert.Equal(1800, block.ActiveDurationSeconds, 3);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_DailyTotalDoesNotExceedPhysicalDay()
    {
        await using var db = CreateDb();
        SeedSparseLateHours(db);
        db.Set<AwEventEntity>().Add(WindowEvent(At("2026-07-05T08:00:00Z"), 86400, "Code.exe", "A"));
        await db.SaveChangesAsync();

        var analysis = await AnalysisAsync(db);

        // AC-2.3：单日所有块 activeDurationSeconds 之和 ≤ 86400。
        Assert.True(analysis.Blocks.Sum(b => b.ActiveDurationSeconds) <= 86400);
    }

    [Fact]
    public async Task QueryCompleteDetailAsync_KeepsLegacyPageContractWhileInternalPathIsUncapped()
    {
        await using var db = CreateDb();
        for (var i = 0; i < 250; i++)
        {
            db.Set<AwEventEntity>().Add(WindowEvent(At("2026-07-05T08:00:00Z").AddSeconds(i * 10), 10, "Code.exe", "A"));
        }

        await db.SaveChangesAsync();
        var tracker = Tracker(db);

        // 对外契约保持不变：请求 2000 条仍被夹到 200，totalCount 仍是真实条数。
        var page = await tracker.QueryCompleteDetailAsync(
            DetailQuery("2026-07-05", pageSize: 2000, page: 1), CancellationToken.None);
        Assert.Equal(200, page.PageSize);
        Assert.Equal(200, page.Items.Count);
        Assert.Equal(250, page.TotalCount);

        // 内部聚合路径不受分页契约限制。
        var all = await tracker.QueryAllDetailRecordsAsync(
            DetailQuery("2026-07-05", pageSize: 2000, page: 1), CancellationToken.None);
        Assert.Equal(250, all.Count);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_DailyTotalMatchesSummaryTimelineCoverageWithinTwoPercent()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 与镜像库同形：13 个活跃小时，每小时一条覆盖整点的 window 记录，
        // 逐分钟输入记录与网页记录都落在这些小时内（不额外扩张活跃区间）。
        for (var hour = 0; hour <= 12; hour++)
        {
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(hour), 3600, "Code.exe", "A"));
            if (hour is >= 1 and <= 6)
                db.Set<KeystatsSampleEntity>().AddRange(BuildMinuteSamples(dayStart.AddHours(hour), minutes: 30, $"device-{hour}"));
            if (hour % 3 == 0)
                db.Set<AwEventEntity>().Add(WebPageEvent(dayStart.AddHours(hour).AddMinutes(10), 600, "docs"));
        }

        await db.SaveChangesAsync();
        var tracker = Tracker(db);
        var analysis = await new PcActivityAnalysisService(tracker).GetDailyAnalysisAsync(Day, 60, CancellationToken.None);
        var summary = await tracker.GetSummaryAsync(Day, CancellationToken.None);

        var analysisMinutes = analysis.Blocks.Sum(b => b.ActiveDurationSeconds) / 60.0;
        var timelineMinutes = summary.Timeline.Sum(item => item.DurationMinutes);
        var deviation = timelineMinutes > 0 ? Math.Abs(analysisMinutes - timelineMinutes) / timelineMinutes : 0;

        // AC-2.3：单日合计 ≤ 86400 秒，且与 summary.timeline 覆盖分钟数差 ≤ 2%。
        Assert.True(analysis.Blocks.Sum(b => b.ActiveDurationSeconds) <= 86400);
        Assert.True(
            deviation <= 0.02,
            $"activity-analysis 合计 {analysisMinutes:0.##} 分钟 vs summary.timeline {timelineMinutes:0.##} 分钟，偏差 {deviation:P2}");
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_InputOnlyHoursAreNotInSummaryTimeline()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 记录在案的口径差异（AC-2.3 的已知例外）：
        // summary.timeline 只收 window / web-page（它是「应用使用时间线」），
        // 而 REQ-2 之后的 analysis 活跃时长还包含 input-minute。
        // 只有逐分钟输入、没有窗口事件的小时：analysis > 0 而 timeline = 0 → 2% 容差在这种形状下不成立。
        db.Set<KeystatsSampleEntity>().AddRange(BuildMinuteSamples(dayStart.AddHours(5), minutes: 60, "device-1"));
        await db.SaveChangesAsync();

        var tracker = Tracker(db);
        var analysis = await new PcActivityAnalysisService(tracker).GetDailyAnalysisAsync(Day, 60, CancellationToken.None);
        var summary = await tracker.GetSummaryAsync(Day, CancellationToken.None);

        // 60 条采样 → 59 条 input-minute 记录（相邻采样的差），覆盖 59 分钟。
        Assert.Equal(3540, analysis.Blocks[5].ActiveDurationSeconds, 3);
        Assert.Equal(0, summary.Timeline.Sum(item => item.DurationMinutes));
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_InputMinuteOnlyMinutesAreExactlyTheTimelineGap()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 与镜像库 2026-09-27 同形（只读实测：input-minute 全天覆盖，window / web-page 只覆盖其中一部分）：
        // analysis 的活跃时长走 PcActivityOverlapResolver 的候选并集（含 input-minute，这是 REQ-2 与术语表
        // 规定的唯一口径），而 summary.timeline 按 IsSummaryTimelineRecord 只收 window / web-page。
        // 两者的差额恰好是「只有 input-minute 覆盖的分钟」—— 该差额在真实数据上远大于 2%，
        // 因此 AC-2.3 的第二条（与 summary.timeline 覆盖分钟数差 ≤2%）与本条 AC 的并集口径互相冲突。
        for (var hour = 0; hour < 13; hour++)
        {
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(hour), 2400, "Code.exe", "A"));
            db.Set<KeystatsSampleEntity>().AddRange(BuildMinuteSamples(dayStart.AddHours(hour), minutes: 60, $"device-{hour}"));
        }

        await db.SaveChangesAsync();

        var tracker = Tracker(db);
        var analysis = await new PcActivityAnalysisService(tracker).GetDailyAnalysisAsync(Day, 60, CancellationToken.None);
        var summary = await tracker.GetSummaryAsync(Day, CancellationToken.None);

        var analysisMinutes = analysis.Blocks.Sum(b => b.ActiveDurationSeconds) / 60.0;
        var timelineMinutes = summary.Timeline.Sum(item => item.DurationMinutes);

        // 每小时：60 条采样 → 59 条 input-minute 记录（覆盖 59 分钟），window 的 40 分钟整段被消解 → 并集 59 分钟。
        Assert.Equal(13 * 59, analysisMinutes, 1);
        // timeline 只收 window / web-page：每小时 40 分钟。
        Assert.Equal(13 * 40, timelineMinutes, 1);
        // 差额 = input-minute 独占分钟（19 分钟/小时）。AC-2.3 第一条（≤86400 秒）仍然成立。
        Assert.Equal(13 * 19, analysisMinutes - timelineMinutes, 1);
        Assert.True(analysis.Blocks.Sum(b => b.ActiveDurationSeconds) <= 86400);
    }

    [Fact]
    public async Task GetDailyAnalysisAsync_FullBusinessDay_MeasuresLatencyAndAllocations()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 与镜像库同量级：1210 条明细（window / web-page / input-minute 混合）。
        for (var i = 0; i < 400; i++)
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddMinutes(i * 2), 60, "Code.exe", "A"));
        for (var i = 0; i < 400; i++)
            db.Set<AwEventEntity>().Add(WebPageEvent(dayStart.AddMinutes(i * 2 + 1), 30, "docs"));
        db.Set<KeystatsSampleEntity>().AddRange(BuildMinuteSamples(dayStart, minutes: 410, "device-1"));
        await db.SaveChangesAsync();

        var tracker = Tracker(db);
        var service = new PcActivityAnalysisService(tracker);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var analysis = await service.GetDailyAnalysisAsync(Day, 60, CancellationToken.None);

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Output.WriteLine(
            $"full-day activity-analysis: seededRecords=1209, blocks={analysis.Blocks.Count}, " +
            $"elapsed={stopwatch.ElapsedMilliseconds}ms, allocated≈{allocated / 1024.0:0}KiB");

        // AC-1.4：覆盖全天后单次响应的耗时/内存实测（数字见测试输出），并给一个宽松上界防回归。
        Assert.True(stopwatch.ElapsedMilliseconds < 10_000, $"单次 activity-analysis 耗时 {stopwatch.ElapsedMilliseconds}ms 超过 10s");
        Assert.True(AllTotal(analysis) <= 86400);
    }

    private static double AllTotal(PcActivityAnalysisResponse analysis)
        => analysis.Blocks.Sum(b => b.ActiveDurationSeconds);

    /// <summary>
    /// 与镜像库同形：前 200 条覆盖 2 个整点（每小时 100 条，升序取第一页时只看得到这两个小时），
    /// 其余 11 个小时每小时只有 1 条 —— 分页上限会把它们整段丢掉，共 13 个非零小时。
    /// </summary>
    private static void SeedSparseLateHours(PimDbContext db)
    {
        for (var i = 0; i < 100; i++)
        {
            db.Set<AwEventEntity>().Add(WindowEvent(At("2026-07-05T08:00:00Z").AddSeconds(i * 30), 30, "Code.exe", "A"));
            db.Set<AwEventEntity>().Add(WindowEvent(At("2026-07-05T09:00:00Z").AddSeconds(i * 30), 30, "Code.exe", "A"));
        }

        db.Set<AwEventEntity>().Add(WindowEvent(At("2026-07-05T07:00:00Z"), 60, "Code.exe", "A"));
        for (var hour = 10; hour <= 19; hour++)
        {
            db.Set<AwEventEntity>().Add(WindowEvent(At($"2026-07-05T{hour:00}:00Z"), 60, "Code.exe", "A"));
        }
    }

    private static Task<PcActivityAnalysisResponse> AnalysisAsync(PimDbContext db)
        => new PcActivityAnalysisService(Tracker(db))
            .GetDailyAnalysisAsync(Day, 60, CancellationToken.None);

    private static PcActivityAnalysisBlockDto SingleBlockAt(PcActivityAnalysisResponse analysis, DateTimeOffset start)
        => Assert.Single(analysis.Blocks.Where(b =>
            DateTimeOffset.Parse(b.Start) == start && b.ActiveDurationSeconds > 0));

    private static PcTrackerService Tracker(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static DetailQueryParams DetailQuery(string date, int pageSize, int page)
        => new(
            date,
            date,
            null,
            null,
            null,
            null,
            null,
            null,
            "date",
            "asc",
            page,
            pageSize,
            View: "interpreted");

    private static IEnumerable<KeystatsSampleEntity> BuildMinuteSamples(DateTimeOffset start, int minutes, string deviceId)
    {
        for (var i = 0; i < minutes; i++)
        {
            yield return new KeystatsSampleEntity
            {
                PimDeviceId = deviceId,
                SampledAtUtc = start.AddMinutes(i),
                StatsDate = Day,
                KeyPresses = i * 10,
                LeftClicks = i,
                RawJson = "{}"
            };
        }
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static DateTimeOffset At(string timestamp) => DateTimeOffset.Parse(timestamp);

    private static AwEventEntity WindowEvent(DateTimeOffset timestamp, double duration, string appName, string title) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            DeviceId = "device-1",
            Timestamp = timestamp,
            Duration = duration,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = AppNameNormalizer.Normalize(appName),
            WindowTitle = title,
            DataJson = "{}"
        };

    private static AwEventEntity WebPageEvent(DateTimeOffset timestamp, double duration, string domain) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            DeviceId = "device-1",
            Timestamp = timestamp,
            Duration = duration,
            EventType = "web",
            AppName = "msedge.exe",
            AppNameNormalized = "msedge",
            WindowTitle = domain,
            DataJson = $"{{\"url\":\"https://{domain}.example.com/\",\"title\":\"{domain}\"}}"
        };
}
