using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcActivityAggregationServiceTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new PimDbContext(options);
    }

    private static AwEventEntity Win(string ts, double sec, string app, string afk = "not-afk") => new()
    {
        Id = Random.Shared.NextInt64(1, long.MaxValue),
        DeviceId = "d1",
        Timestamp = DateTimeOffset.Parse(ts),
        Duration = sec,
        EventType = "window",
        AppName = app,
        AppNameNormalized = AppNameNormalizer.Normalize(app),
        WindowTitle = "t",
        AfkStatus = afk,
        DataJson = "{}",
        CreatedAt = DateTimeOffset.Parse(ts),
        UpdatedAt = DateTimeOffset.Parse(ts)
    };

    private static PcAggregationQuery DayQuery(string date) =>
        new(date, null, null, "Asia/Shanghai");

    // === 任务 1：专注块 ===

    [Fact]
    public async Task GetFocusBlocksAsync_MergesSmallGapsAndFiltersShortBlocks()
    {
        await using var db = CreateDb();
        // Asia/Shanghai 2026-07-10 业务日窗口 = [2026-07-09T20:00Z, 2026-07-10T20:00Z)
        // 块 A（保留）：09:00(5min) + 09:10(5min) + 09:13(3min) → 01:00Z→01:16Z = 16min
        // 块 B（过滤）：10:00(200s) + 10:05(200s) → 02:00Z→02:08:20Z = 8.3min < 10min
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 300, "Code"),
            Win("2026-07-10T01:10:00Z", 300, "Code"),
            Win("2026-07-10T01:13:00Z", 180, "Code"),
            Win("2026-07-10T02:00:00Z", 200, "Edge"),
            Win("2026-07-10T02:05:00Z", 200, "Edge"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var block = Assert.Single(result.Items);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:00:00Z"), block.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:16:00Z"), block.EndUtc);
        Assert.Equal(16, block.DurationMinutes);
        Assert.Equal("code", block.MainApp);
        var top = Assert.Single(block.TopApps);
        Assert.Equal("code", top.Name);
        Assert.Equal(13, top.Minutes); // 块内该 app 事件时长之和（300+300+180s）
        // 本地时间串断言（不用 ToLocalTime）
        Assert.Equal("2026-07-10 09:00:00", block.StartLocal);
        Assert.Equal("2026-07-10 09:16:00", block.EndLocal);
    }

    [Fact]
    public async Task GetFocusBlocksAsync_SplitsOnLargeGap()
    {
        await using var db = CreateDb();
        // 块 1：09:00(5min)+09:10(5min) → 01:00Z→01:15Z = 15min
        // 块 2：09:21(5min)+09:31(5min) → 01:21Z→01:36Z = 15min（与块 1 间隔 6min > 5min）
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 300, "Code"),
            Win("2026-07-10T01:10:00Z", 300, "Code"),
            Win("2026-07-10T01:21:00Z", 300, "Code"),
            Win("2026-07-10T01:31:00Z", 300, "Code"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:00:00Z"), result.Items[0].StartUtc);
        Assert.Equal(15, result.Items[0].DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:21:00Z"), result.Items[1].StartUtc);
        Assert.Equal(15, result.Items[1].DurationMinutes);
    }

    [Fact]
    public async Task GetFocusBlocksAsync_ExcludesAfkEvents()
    {
        await using var db = CreateDb();
        // afk 事件若被计入，块会延到 01:20Z（20min）；排除后仅 01:00Z→01:10Z = 10min
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 600, "Code"),
            Win("2026-07-10T01:10:00Z", 600, "Code", afk: "afk"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var block = Assert.Single(result.Items);
        Assert.Equal(10, block.DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:10:00Z"), block.EndUtc);
    }

    [Fact]
    public async Task GetFocusBlocksAsync_MainAppAndTopAppsSortedByDuration()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 300, "Code"),
            Win("2026-07-10T01:05:00Z", 300, "Code"),
            Win("2026-07-10T01:10:00Z", 200, "Edge"),
            Win("2026-07-10T01:15:00Z", 200, "Edge"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var block = Assert.Single(result.Items);
        Assert.Equal("code", block.MainApp);
        Assert.Equal(2, block.TopApps.Count);
        Assert.Equal("code", block.TopApps[0].Name);
        Assert.Equal(10, block.TopApps[0].Minutes);
        Assert.Equal("edge", block.TopApps[1].Name);
        Assert.Equal(7, block.TopApps[1].Minutes);
    }

    [Fact]
    public async Task GetFocusBlocksAsync_CapsSingleEventDuration()
    {
        await using var db = CreateDb();
        // 单事件 7200s → 封顶 3600s → 块 60min
        db.Set<AwEventEntity>().Add(Win("2026-07-10T01:00:00Z", 7200, "Chrome"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var block = Assert.Single(result.Items);
        Assert.Equal(60, block.DurationMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T02:00:00Z"), block.EndUtc);
    }

    [Fact]
    public async Task GetFocusBlocksAsync_EmptyReturnsEmptyItems()
    {
        await using var db = CreateDb();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetFocusBlocksAsync(DayQuery("2026-07-10"), CancellationToken.None);

        Assert.Empty(result.Items);
    }
}
