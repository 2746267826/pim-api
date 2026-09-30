using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-4（day 桶以业务日为界）与 REQ-6（hour + 跨日必须明确报错）。
/// </summary>
public sealed class PcHeatmapGridBoundaryTests
{
    [Fact]
    public async Task GetHeatmapGridAsync_DayBucketsStartAtBusinessDayStart()
    {
        await using var db = CreateDb();
        var service = Service(db);

        var response = await service.GetHeatmapGridAsync(
            new DateTime(2026, 9, 21), new DateTime(2026, 9, 24), "day", CancellationToken.None);

        var buckets = response.Grid.SelectMany(row => row).ToList();
        Assert.Equal(4, buckets.Count);
        for (var i = 0; i < buckets.Count; i++)
        {
            var expectedStart = PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 9, 21).AddDays(i));
            // AC-4.1：桶 start = 对应业务日起点（自 2026-09-20T20:00:00+00:00 起逐日 +24h），end = start + 24h。
            Assert.Equal(expectedStart, DateTimeOffset.Parse(buckets[i].Start));
            Assert.Equal(expectedStart.AddDays(1), DateTimeOffset.Parse(buckets[i].End));
        }

        Assert.Equal(DateTimeOffset.Parse("2026-09-20T20:00:00+00:00"), DateTimeOffset.Parse(buckets[0].Start));
    }

    [Fact]
    public async Task GetHeatmapGridAsync_DayBucketStartMatchesSummaryHeatmap()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "device-1",
            Timestamp = PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 9, 27)).AddHours(2),
            Duration = 600,
            EventType = "window",
            AppName = "Code.exe",
            AppNameNormalized = "code",
            DataJson = "{}"
        });
        await db.SaveChangesAsync();
        var service = Service(db);

        var grid = await service.GetHeatmapGridAsync(
            new DateTime(2026, 9, 27), new DateTime(2026, 9, 27), "day", CancellationToken.None);
        var summary = await service.GetSummaryAsync(new DateTime(2026, 9, 27), CancellationToken.None);

        // AC-4.2：同一业务日的桶起点在两个接口中完全相同。
        Assert.Equal(summary.Heatmap[0].Start, Assert.Single(grid.Grid).Single().Start);
    }

    [Theory]
    [InlineData("2026-09-26", "2026-09-28")]
    [InlineData("2026-09-26", "2026-09-27")]
    public async Task GetHeatmapGridAsync_HourDimensionAcrossDaysThrows(string start, string end)
    {
        await using var db = CreateDb();
        var service = Service(db);

        // AC-6.1（P-4 方案 a）：hour + 跨日不再静默返回起始日单行，而是明确报错。
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetHeatmapGridAsync(DateTime.Parse(start), DateTime.Parse(end), "hour", CancellationToken.None));
        Assert.Contains("hour", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetHeatmapGridAsync_UsesLatestSnapshotPerDayForCellsAndMaxKeyCount()
    {
        await using var db = CreateDb();
        var day = new DateTime(2026, 9, 27);
        db.Set<KeystatsDailyEntity>().AddRange(
            new KeystatsDailyEntity
            {
                DeviceId = "device-1",
                SnapshotDate = day,
                KeyPresses = 999_999,
                CreatedAt = DateTimeOffset.Parse("2026-09-27T09:00:00Z")
            },
            new KeystatsDailyEntity
            {
                DeviceId = "device-1",
                SnapshotDate = day,
                KeyPresses = 12_345,
                CreatedAt = DateTimeOffset.Parse("2026-09-27T12:00:00Z")
            });
        await db.SaveChangesAsync();

        var response = await Service(db).GetHeatmapGridAsync(day, day, "day", CancellationToken.None);
        var cell = Assert.Single(Assert.Single(response.Grid));

        // 与 summary / REQ-7 同口径：同一天取最近写入的一条快照，色阶上界也基于这一批。
        Assert.Equal(12_345, cell.KeyPressCount);
        Assert.Equal(12_345, response.MaxKeyCount);

        // hour 维度共用同一批「每日最新快照」：按键按事件数比例分摊，总数仍应来自 12345 而不是 999999。
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "device-1",
            Timestamp = PcTrackerService.GetBusinessDayStartForQuery(day).AddHours(2),
            Duration = 600,
            EventType = "window",
            AppName = "Code.exe",
            AppNameNormalized = "code",
            DataJson = "{}"
        });
        await db.SaveChangesAsync();

        var hour = await Service(db).GetHeatmapGridAsync(day, day, "hour", CancellationToken.None);
        var nonZero = Assert.Single(Assert.Single(hour.Grid).Where(b => b.KeyPressCount > 0));
        Assert.Equal(12_345, nonZero.KeyPressCount);
    }

    [Fact]
    public async Task GetHeatmapGridAsync_DimensionIsCaseInsensitive()
    {
        await using var db = CreateDb();
        var service = Service(db);

        // `Hour` 与 `hour` 走同一条分支（此前只有小写走 hour，其它大小写会掉进 day 网格）。
        var response = await service.GetHeatmapGridAsync(
            new DateTime(2026, 9, 27), new DateTime(2026, 9, 27), "Hour", CancellationToken.None);

        Assert.Equal(24, Assert.Single(response.Grid).Count);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetHeatmapGridAsync(
            new DateTime(2026, 9, 26), new DateTime(2026, 9, 27), "HOUR", CancellationToken.None));
    }

    [Fact]
    public async Task GetHeatmapGridAsync_HourDimensionSingleDayKeeps24Buckets()
    {
        await using var db = CreateDb();
        var service = Service(db);

        // AC-6.2：单日范围 + hour 行为不变 —— 24 桶、自业务日起点起算。
        var response = await service.GetHeatmapGridAsync(
            new DateTime(2026, 9, 27), new DateTime(2026, 9, 27), "hour", CancellationToken.None);

        var row = Assert.Single(response.Grid);
        Assert.Equal(24, row.Count);
        Assert.Equal(
            PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 9, 27)),
            DateTimeOffset.Parse(row[0].Start));
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
