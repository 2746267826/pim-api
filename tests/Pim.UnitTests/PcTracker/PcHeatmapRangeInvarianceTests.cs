using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20261001 · REQ-1（#375）：范围聚合的日/月/年桶活跃分钟必须与单日口径一致。
/// <para>
/// 缺陷：活跃区间并集按**整个请求范围**取数，而 input-minute 区间由「相邻两条采样」拼出 ——
/// 采样对一旦跨业务日（或跨采样断档），该区间就把整段断档外推成活跃时长，
/// 于是同一业务日的同一个桶在 1 天 / 7 天 / 30 天请求下得到 722 / 1145 / 1440 三个不同的值。
/// </para>
/// </summary>
public sealed class PcHeatmapRangeInvarianceTests
{
    private static readonly DateTime Day = new(2026, 9, 27);

    /// <summary>
    /// AC-1.1 / AC-1.4 / AC-1.5：同一业务日，单日 / 7 天 / 30 天三种请求范围下的
    /// <c>activeMinutes</c> 与 <c>intensityLevel</c> 必须完全相同。
    /// <para>前一日 20:00 的采样会在范围请求里与当日 11:00 的采样配成跨日采样对，
    /// 把 15 小时断档整段算进当日桶 —— 修复前该断言失败（单日 10 / 范围 430）。</para>
    /// </summary>
    [Fact]
    public async Task GetHeatmapGridAsync_DayBucketActiveMinutes_IsRangeInvariant()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        AddSample(db, dayStart.AddHours(-8)); // 前一业务日（本地 D-1 20:00）
        for (var i = 0; i < 11; i++)
            AddSample(db, dayStart.AddHours(7).AddMinutes(i)); // 本地 D 11:00 – 11:10，逐分钟
        await db.SaveChangesAsync();
        var service = Service(db);

        var single = await BucketForAsync(service, Day, Day);
        var week = await BucketForAsync(service, Day.AddDays(-6), Day);
        var month = await BucketForAsync(service, Day.AddDays(-29), Day);

        // 已观测的缺陷值（修复前）：单日 10、7 天 430、30 天 430。
        Assert.Equal(single.ActiveMinutes, week.ActiveMinutes);
        Assert.Equal(single.ActiveMinutes, month.ActiveMinutes);
        Assert.Equal(single.IntensityLevel, week.IntensityLevel);
        Assert.Equal(single.IntensityLevel, month.IntensityLevel);
    }

    /// <summary>
    /// AC-1.3：采样断档不得被计为活跃。
    /// <para>本地 D 11:01 → 20:00 之间约 9 小时没有采样（与库内 2026-09-06 的 1463 分钟断档同型），
    /// 断档时段不得进入活跃并集。修复前该段被整段外推，当日桶报 540 分钟。</para>
    /// </summary>
    [Fact]
    public async Task GetHeatmapGridAsync_LongSamplingGapIsNotCountedAsActive()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        AddSample(db, dayStart.AddHours(7));                 // 本地 11:00
        AddSample(db, dayStart.AddHours(7).AddMinutes(1));   // 本地 11:01（正常 1 分钟采样）
        AddSample(db, dayStart.AddHours(16));                // 本地 20:00（前一段 9 小时无采样）
        await db.SaveChangesAsync();

        var bucket = await BucketForAsync(Service(db), Day, Day);

        // 真实活跃只有 11:00–11:01 这一分钟；9 小时断档一分钟都不许计入。
        Assert.True(
            bucket.ActiveMinutes == 1,
            $"断档被算成活跃：activeMinutes={bucket.ActiveMinutes}（期望 1，修复前为 540）");
        Assert.True(bucket.ActiveMinutes <= 1440, $"AC-1.2：桶不得超过 1440 分钟，实际 {bucket.ActiveMinutes}");
    }

    /// <summary>
    /// AC-1.2 / AC-1.3：跨整个业务日的超长断档（40 小时无采样）不得让该业务日的桶变成 1440。
    /// <para>修复前：两个端点分属前一业务日与后一业务日，采样对区间覆盖整日 → 该桶报 1440；
    /// 修复后：当日没有任何采样与事件，桶必须是 0。</para>
    /// </summary>
    [Fact]
    public async Task GetHeatmapGridAsync_DayWithoutSamplesInsideLongGap_IsZero()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        AddSample(db, dayStart.AddHours(-8));      // 前一业务日 20:00
        AddSample(db, dayStart.AddHours(40));      // 后一业务日 20:00
        await db.SaveChangesAsync();

        var bucket = await BucketForAsync(Service(db), Day.AddDays(-1), Day.AddDays(2));

        Assert.True(
            bucket.ActiveMinutes == 0,
            $"整日断档被算成活跃：activeMinutes={bucket.ActiveMinutes}（期望 0，修复前为 1440）");
    }

    /// <summary>
    /// AC-1.1：生产形态的活跃来源（窗口事件 + 采样）同样必须范围不变 ——
    /// 这一条在修复前是通过的（样本都落在目标日内，跨日配对不成立），
    /// 保留它是为了防止「只在跨日采样对上修好」的回归。
    /// AC-1.6 的三组生产数字（722/1145/1440 等）由克隆库实测对照表证明，不在单测里硬编码。
    /// </summary>
    [Fact]
    public async Task GetHeatmapGridAsync_ProductionShapedInputKeepsRangeInvariance()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 业务日内的窗口事件（与采样无关的独立活跃来源）
        AddTrackerWindow(db, dayStart.AddHours(7), 600);
        AddSample(db, dayStart.AddHours(6).AddMinutes(30));
        AddSample(db, dayStart.AddHours(6).AddMinutes(31));
        AddSample(db, dayStart.AddHours(14));
        await db.SaveChangesAsync();
        var service = Service(db);

        var single = await BucketForAsync(service, Day, Day);
        var thirty = await BucketForAsync(service, Day.AddDays(-29), Day);
        var month = await BucketForAsync(service, Day.AddDays(-29), Day.AddDays(3));

        Assert.Equal(single.ActiveMinutes, thirty.ActiveMinutes);
        Assert.Equal(single.ActiveMinutes, month.ActiveMinutes);
        Assert.InRange(single.ActiveMinutes, 1, 1440);
    }

    /// <summary>
    /// AC-1.1 / AC-1.4（review round 2 补）：起点落在**前一业务日**、但跨过本地 04:00 伸进当日桶的
    /// 窗口事件，也必须让两个请求范围给出同一个值。
    /// <para>修复前该记录只按 <c>Timestamp &gt;= rangeStart</c> 取数：单日请求（rangeStart = 桶起点）
    /// 整条漏掉，更宽的范围请求却算得进去 —— 实测单日 0 分钟 / 范围 60 分钟。</para>
    /// </summary>
    [Fact]
    public async Task GetHeatmapGridAsync_WindowEventCrossingDayBoundary_IsRangeInvariant()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 本地 D 02:00 起、持续 3 小时：其中 60 分钟落在业务日 D（04:00 起）。
        AddTrackerWindow(db, dayStart.AddHours(-2), 3 * 3600);
        await db.SaveChangesAsync();
        var service = Service(db);

        var single = await BucketForAsync(service, Day, Day);
        var ranged = await BucketForAsync(service, Day.AddDays(-1), Day);
        var month = await BucketForAsync(service, Day.AddDays(-29), Day);

        Assert.Equal(60, single.ActiveMinutes);
        Assert.Equal(single.ActiveMinutes, ranged.ActiveMinutes);
        Assert.Equal(single.ActiveMinutes, month.ActiveMinutes);
        Assert.Equal(single.IntensityLevel, ranged.IntensityLevel);
    }

    /// <summary>
    /// REQ-1（review round 3）：跨业务日边界的窗口事件必须被 grid / summary.heatmap / activity-analysis
    /// **一致地**计入 —— 三个接口用同一批记录与同一消解口径（#370/#374 建立的契约）。
    /// <para>修复前 grid 计 60 分钟而 summary 与 activity-analysis 计 0（它们只取起点落在业务日内的事件）。</para>
    /// </summary>
    [Fact]
    public async Task CrossingWindowEvent_IsCountedConsistentlyByTheThreeEndpoints()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        // 本地 D 02:00 起、持续 3 小时：04:00–05:00 这一小时属于业务日 D。
        AddTrackerWindow(db, dayStart.AddHours(-2), 3 * 3600);
        await db.SaveChangesAsync();
        var service = Service(db);

        var grid = await BucketForAsync(service, Day, Day);
        var summary = await service.GetSummaryAsync(Day, CancellationToken.None);
        var analysis = await new PcActivityAnalysisService(service)
            .GetDailyAnalysisAsync(Day, 60, CancellationToken.None);

        Assert.Equal(60, grid.ActiveMinutes);
        Assert.Equal(grid.ActiveMinutes, summary.Heatmap.Sum(bucket => bucket.ActiveMinutes));
        Assert.Equal(grid.ActiveMinutes, (int)(analysis.Blocks.Sum(block => block.ActiveDurationSeconds) / 60));
    }

    private static async Task<HeatmapGridCell> BucketForAsync(
        PcTrackerService service,
        DateTime start,
        DateTime end)
    {
        var response = await service.GetHeatmapGridAsync(start, end, "day", CancellationToken.None);
        var expectedStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        return Assert.Single(
            response.Grid.SelectMany(row => row),
            cell => string.Equals(cell.Start, expectedStart.ToString("O"), StringComparison.Ordinal));
    }

    private static void AddSample(PimDbContext db, DateTimeOffset sampledAtUtc)
        => db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "device-1",
            SampledAtUtc = sampledAtUtc,
            StatsDate = sampledAtUtc.UtcDateTime.Date,
            KeyPresses = 10,
            KeyCountsJson = "{}",
            RawJson = "{}"
        });

    private static void AddTrackerWindow(PimDbContext db, DateTimeOffset timestamp, double durationSeconds)
        => db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "device-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = "Code.exe",
            RawJson = "{}",
            Date = timestamp.UtcDateTime.Date
        });

    private static PcTrackerService Service(PimDbContext db)
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
}
