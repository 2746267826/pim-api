using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-3（强度字段同名字段必须同量纲）。
/// </summary>
public sealed class PcIntensityFieldConsistencyTests
{
    private static readonly DateTime Day = new(2026, 7, 5);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.0 / 24, 1)]      // 2.5 分钟 / 小时
    [InlineData(1.0 / 12, 1)]      // 5 分钟边界
    [InlineData(0.2, 2)]
    [InlineData(1.0 / 4, 2)]       // 15 分钟边界
    [InlineData(1.0 / 3, 3)]
    [InlineData(1.0 / 2, 3)]       // 30 分钟边界
    [InlineData(0.7, 4)]
    [InlineData(3.0 / 4, 4)]       // 45 分钟边界
    [InlineData(0.9, 5)]
    [InlineData(1.0, 5)]
    public void IntensityBands_AreActiveTimeRatioBands(double ratio, int expected)
    {
        Assert.Equal(expected, PcActivityIntensity.ForRatio(ratio));
        Assert.Equal(expected, PcActivityIntensity.ForSeconds(ratio * 3600, 3600));
    }

    [Fact]
    public void IntensityBands_DefaultToZeroForNonPositiveOrNonFiniteValues()
    {
        Assert.Equal(0, PcActivityIntensity.ForRatio(0));
        Assert.Equal(0, PcActivityIntensity.ForRatio(-1));
        Assert.Equal(0, PcActivityIntensity.ForRatio(double.NaN));
        Assert.Equal(0, PcActivityIntensity.ForSeconds(100, 0));
    }

    [Fact]
    public async Task ActivityAnalysis_IntensityLevelEqualsSummaryHeatmapForEveryHour()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 逐小时铺出 5 档都出现的一天：满档 / 20 分钟 / 10 分钟 / 3 分钟 / 无数据。
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart, 3600, "Code.exe"));
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(1), 1200, "Code.exe"));
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(2), 600, "Code.exe"));
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(3), 180, "Code.exe"));
        await db.SaveChangesAsync();

        var tracker = Tracker(db);
        var analysis = await new PcActivityAnalysisService(tracker)
            .GetDailyAnalysisAsync(Day, 60, CancellationToken.None);
        var summary = await tracker.GetSummaryAsync(Day, CancellationToken.None);

        // AC-3.2：同一业务日逐小时，两个接口的 intensityLevel 相同（全量对照，不是抽样）。
        Assert.Equal(summary.Heatmap.Count, analysis.Blocks.Count);
        for (var hour = 0; hour < summary.Heatmap.Count; hour++)
        {
            Assert.Equal(summary.Heatmap[hour].IntensityLevel, analysis.Blocks[hour].IntensityLevel);
            Assert.Equal(PcActivityIntensity.MaxLevel, analysis.Blocks[hour].IntensityMax);
            Assert.Equal(PcActivityIntensity.MaxLevel, summary.Heatmap[hour].IntensityMax);
            Assert.InRange(analysis.Blocks[hour].IntensityLevel, 0, PcActivityIntensity.MaxLevel);
        }

        // 分档确实覆盖多档，不是恒 0。
        Assert.Contains(analysis.Blocks, b => b.IntensityLevel == 5);
        Assert.Contains(analysis.Blocks, b => b.IntensityLevel is >= 1 and <= 4);
    }

    [Fact]
    public async Task ActivityAnalysis_LongBlocksUseRatioSoTheyAreNotAlwaysMaxed()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 240 分钟块：满档 240 分钟、30 分钟（占比 1/8）、10 分钟（占比 1/24）、空闲。
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart, 240 * 60, "Code.exe"));
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddMinutes(240), 30 * 60, "Code.exe"));
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddMinutes(480), 10 * 60, "Code.exe"));
        await db.SaveChangesAsync();

        var analysis = await new PcActivityAnalysisService(Tracker(db))
            .GetDailyAnalysisAsync(Day, 240, CancellationToken.None);

        // AC-3.3：240 分钟块不得恒满档（按占比分档），且分布不是恒 0。
        Assert.Equal(6, analysis.Blocks.Count);
        Assert.Equal(5, analysis.Blocks[0].IntensityLevel);
        Assert.Equal(2, analysis.Blocks[1].IntensityLevel);
        Assert.Equal(1, analysis.Blocks[2].IntensityLevel);
        Assert.Equal(0, analysis.Blocks[3].IntensityLevel);
        Assert.True(analysis.Blocks.Select(b => b.IntensityLevel).Distinct().Count() >= 3);
    }

    [Fact]
    public async Task ActivityAnalysis_ShortBlocksAlsoFollowRatio()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddMinutes(30), 15 * 60, "Code.exe"));
        await db.SaveChangesAsync();

        var analysis = await new PcActivityAnalysisService(Tracker(db))
            .GetDailyAnalysisAsync(Day, 15, CancellationToken.None);

        // 15 分钟全活跃 → 满档；其余块 0 档。
        Assert.Equal(96, analysis.Blocks.Count);
        Assert.Equal(5, analysis.Blocks[2].IntensityLevel);
        Assert.Equal(0, analysis.Blocks[3].IntensityLevel);
        Assert.DoesNotContain(analysis.Blocks, b => b.IntensityLevel is > 5 or < 0);
    }

    private static PcTrackerService Tracker(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static AwEventEntity WindowEvent(DateTimeOffset timestamp, double duration, string appName) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            DeviceId = "device-1",
            Timestamp = timestamp,
            Duration = duration,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = AppNameNormalizer.Normalize(appName),
            WindowTitle = "A",
            DataJson = "{}"
        };
}
