using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-1（聚合输入覆盖整个业务日）与 REQ-2（块内时长先消解重叠再汇总）。
/// 这些用例在修复前必须失败：聚合输入被 <c>QueryCompleteDetailAsync</c> 的分页上限夹到 200 条，
/// 且块内时长是「逐条相加」而不是「区间并集」。
/// </summary>
public sealed class PcActivityAnalysisCoverageTests
{
    private static readonly DateTime Day = new(2026, 7, 5);

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
