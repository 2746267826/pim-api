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
