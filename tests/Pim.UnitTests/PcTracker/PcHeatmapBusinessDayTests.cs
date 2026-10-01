using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20261001 · REQ-7（#380）：<c>heatmap/grid</c> 的桶必须携带业务日字段。
/// <para>
/// 缺陷：业务日自本地 04:00 起算，因此一个业务日的最后 4 个小时桶（本地 00:00–03:59）的
/// <c>start</c> 落在**次日** UTC 时刻（业务日 2026-09-27 → <c>2026-09-27T16:00Z</c> ~ <c>19:00Z</c>）。
/// 响应里没有业务日字段时，消费方只能按 +08:00 日历日推断，会把这 4 个桶归到 9/28。
/// </para>
/// </summary>
public sealed class PcHeatmapBusinessDayTests
{
    private static readonly DateTime Day = new(2026, 9, 27);

    /// <summary>AC-7.2：业务日 D 的 24 个小时桶全部返回 businessDay = D。</summary>
    [Fact]
    public async Task HourBuckets_CarryTheOwningBusinessDay()
    {
        await using var db = CreateDb();
        var response = await Service(db).GetHeatmapGridAsync(Day, Day, "hour", CancellationToken.None);

        var row = Assert.Single(response.Grid);
        Assert.Equal(24, row.Count);
        Assert.All(row, cell => Assert.Equal("2026-09-27", cell.BusinessDay));
    }

    /// <summary>
    /// AC-7.2：本地 00:00–03:59 的四个桶（Start 落在次日 UTC 时刻）必须标 9/27 而不是 9/28。
    /// </summary>
    [Fact]
    public async Task HourBuckets_AfterLocalMidnight_StillBelongToThePreviousBusinessDay()
    {
        await using var db = CreateDb();
        var response = await Service(db).GetHeatmapGridAsync(Day, Day, "hour", CancellationToken.None);

        var row = Assert.Single(response.Grid);
        // 本地 00:00–03:59 = UTC 2026-09-27T16:00Z ~ 19:00Z（次日 UTC 的凌晨时段），共 4 个桶。
        var lateNight = row
            .Select(cell => (Cell: cell, StartUtc: DateTimeOffset.Parse(cell.Start).UtcDateTime))
            .Where(x => x.StartUtc.Date == new DateTime(2026, 9, 27)
                        && x.StartUtc.TimeOfDay >= TimeSpan.FromHours(16))
            .Select(x => x.Cell)
            .ToList();

        Assert.Equal(4, lateNight.Count);
        Assert.All(lateNight, cell =>
        {
            Assert.InRange(cell.Hour, 0, 3);
            Assert.Equal("2026-09-27", cell.BusinessDay);
        });
    }

    /// <summary>AC-7.3：同一业务日下 hour / day 两个维度对同一时段的业务日归属一致。</summary>
    [Fact]
    public async Task HourAndDayDimensions_AgreeOnBusinessDay()
    {
        await using var db = CreateDb();
        var service = Service(db);

        var hour = await service.GetHeatmapGridAsync(Day, Day, "hour", CancellationToken.None);
        var day = await service.GetHeatmapGridAsync(Day, Day, "day", CancellationToken.None);

        var hourDays = Assert.Single(hour.Grid).Select(cell => cell.BusinessDay).Distinct().ToList();
        var dayDays = Assert.Single(day.Grid).Select(cell => cell.BusinessDay).Distinct().ToList();

        Assert.Equal(["2026-09-27"], hourDays);
        Assert.Equal(["2026-09-27"], dayDays);
    }

    /// <summary>AC-7.1 / AC-7.3：day 桶的业务日与桶起点一致，且跨多日范围时逐日递增。</summary>
    [Theory]
    [InlineData("day")]
    [InlineData("month")]
    [InlineData("year")]
    public async Task DayMonthYearBuckets_CarryOneBusinessDayPerCell(string dimension)
    {
        await using var db = CreateDb();
        var response = await Service(db).GetHeatmapGridAsync(
            Day.AddDays(-2), Day, dimension, CancellationToken.None);

        var cells = response.Grid.SelectMany(row => row).ToList();
        Assert.Equal(3, cells.Count);
        Assert.Equal(
            ["2026-09-25", "2026-09-26", "2026-09-27"],
            cells.Select(cell => cell.BusinessDay).ToList());

        // 每个桶的 Start 必须落在它自己的业务日窗口 [D 04:00, D+1 04:00) 内。
        Assert.All(cells, cell =>
        {
            var businessDay = DateTime.Parse(cell.BusinessDay);
            Assert.Equal(PcTrackerService.GetBusinessDayStartForQuery(businessDay), DateTimeOffset.Parse(cell.Start));
        });
    }

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
