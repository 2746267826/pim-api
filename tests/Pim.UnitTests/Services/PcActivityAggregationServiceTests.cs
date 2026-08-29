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
        Assert.Equal(11, block.DurationMinutes); // 去重后并集：300+300+180 去重 120s 后 660s=11min（不计 5m 间隙）
        Assert.Equal("code", block.MainApp);
        var top = Assert.Single(block.TopApps);
        Assert.Equal("code", top.Name);
        Assert.Equal(11, top.Minutes); // 去重后并集：300+300+180 去重重叠 2min 后 660s=11min
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
        Assert.Equal(10, result.Items[0].DurationMinutes); // 去重后并集 300+300=600s=10min
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T01:21:00Z"), result.Items[1].StartUtc);
        Assert.Equal(10, result.Items[1].DurationMinutes); // 去重后并集 300+300=600s=10min
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

    // === 任务 2：应用时长 Top + 深夜使用 ===

    [Fact]
    public async Task GetAppUsageAsync_SumsCappedDurationAndRanks()
    {
        await using var db = CreateDb();
        // Code.exe：7200 封顶 3600 + 300 + 300 = 4200s；Edge.exe：1800s；总 6000s = 100min
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 7200, "Code.exe"),
            Win("2026-07-10T02:00:00Z", 300, "Code.exe"),
            Win("2026-07-10T03:00:00Z", 300, "Code.exe"),
            Win("2026-07-10T01:30:00Z", 1800, "Edge.exe"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetAppUsageAsync(DayQuery("2026-07-10"), null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("code", result.Items[0].AppName); // .exe 原值归一合并
        Assert.Null(result.Items[0].DisplayName);      // 无签名 → null
        Assert.Equal(70, result.Items[0].TotalMinutes);
        Assert.Equal(70.0, result.Items[0].Percentage, 1);
        Assert.Equal("edge", result.Items[1].AppName);
        Assert.Equal(30, result.Items[1].TotalMinutes);
        Assert.Equal(30.0, result.Items[1].Percentage, 1);
        Assert.Equal(100, result.TotalMinutes);
        Assert.True(result.Items.Sum(i => i.Percentage) >= 99); // 未取整和 ≈ 100
    }

    [Fact]
    public async Task GetAppUsageAsync_ComputesPercentageFromRawSeconds()
    {
        await using var db = CreateDb();
        // 90s / 60s / 90s：秒数占比 37.5/25/37.5（按四舍五入分钟 2/1/2 会错算成 50/25/50）
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 90, "A"),
            Win("2026-07-10T01:30:00Z", 60, "B"),
            Win("2026-07-10T02:00:00Z", 90, "C"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetAppUsageAsync(DayQuery("2026-07-10"), null, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(37.5, result.Items.Single(i => i.AppName == "a").Percentage, 1);
        Assert.Equal(25.0, result.Items.Single(i => i.AppName == "b").Percentage, 1);
        Assert.Equal(37.5, result.Items.Single(i => i.AppName == "c").Percentage, 1);
    }

    [Fact]
    public async Task GetAppUsageAsync_ExcludesAfkAndWebEvents()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 600, "Code"),
            Win("2026-07-10T02:00:00Z", 600, "Code", afk: "afk"),
            new AwEventEntity
            {
                Id = Random.Shared.NextInt64(1, long.MaxValue),
                DeviceId = "d1",
                Timestamp = DateTimeOffset.Parse("2026-07-10T03:00:00Z"),
                Duration = 1200,
                EventType = "web",
                AppName = "Code",
                AppNameNormalized = AppNameNormalizer.Normalize("Code"),
                WindowTitle = "t",
                AfkStatus = "not-afk",
                DataJson = "{}",
                CreatedAt = DateTimeOffset.Parse("2026-07-10T03:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-07-10T03:00:00Z")
            });
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetAppUsageAsync(DayQuery("2026-07-10"), null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(10, item.TotalMinutes); // 仅非 afk window 的 600s
        Assert.Equal(10, result.TotalMinutes);
    }

    [Fact]
    public async Task GetAppUsageAsync_FiltersSubMinuteApps()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 30, "Code"),
            Win("2026-07-10T01:30:00Z", 500, "Edge"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetAppUsageAsync(DayQuery("2026-07-10"), null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("edge", item.AppName);
        Assert.Equal(8, item.TotalMinutes);
        Assert.Equal(9, result.TotalMinutes); // 30s 噪声不计入排行，但计入总时长
    }

    [Fact]
    public async Task GetAppUsageAsync_RespectsLimit()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T01:00:00Z", 3600, "Code"),
            Win("2026-07-10T02:00:00Z", 600, "Code"),
            Win("2026-07-10T01:30:00Z", 1800, "Edge"),
            Win("2026-07-10T03:00:00Z", 600, "Chrome"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetAppUsageAsync(DayQuery("2026-07-10"), 2, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("code", result.Items[0].AppName);
        Assert.Equal("edge", result.Items[1].AppName);
        Assert.Equal(110, result.TotalMinutes); // totalMinutes 仍是全量
    }

    [Fact]
    public async Task GetLateNightAsync_SumsMinutesInLateWindow()
    {
        await using var db = CreateDb();
        // 业务日 2026-07-10（Asia/Shanghai）：
        //   23:00 本地(=15:00Z) 不算深夜；23:45 本地(=15:45Z) 算；次日 02:00 本地(=18:00Z) 算归 D；
        //   次日 05:00 本地(=21:00Z) 已出 D 业务日窗口，不算
        db.Set<AwEventEntity>().AddRange(
            Win("2026-07-10T15:00:00Z", 300, "Code"),
            Win("2026-07-10T15:45:00Z", 1200, "Code"),
            Win("2026-07-10T18:00:00Z", 1800, "Edge"),
            Win("2026-07-10T21:00:00Z", 900, "Chrome"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetLateNightAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var day = Assert.Single(result.Items);
        Assert.Equal("2026-07-10", day.Date);
        Assert.Equal(50, day.Minutes); // (1200+1800)/60
        Assert.True(day.HadActivity);
        // 边界换算用 TimeZoneInfo，不用 ToLocalTime
        Assert.Equal(new DateTime(2026, 7, 10, 23, 0, 0),
            TimeZoneInfo.ConvertTime(DateTimeOffset.Parse("2026-07-10T15:00:00Z"), Tz).DateTime);
    }

    [Fact]
    public async Task GetLateNightAsync_AllDaysWithNoEvents()
    {
        await using var db = CreateDb();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetLateNightAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var day = Assert.Single(result.Items);
        Assert.Equal(0, day.Minutes);
        Assert.False(day.HadActivity);
    }

    // === 任务 3：分类分布 ===

    private static ActivityClassificationEntity Snap(string start, string end, string category, string? color = null) => new()
    {
        Id = Guid.NewGuid(),
        RecordKey = Guid.NewGuid().ToString("N"),
        DeviceId = "d1",
        StartedAt = DateTimeOffset.Parse(start),
        EndedAt = DateTimeOffset.Parse(end),
        CategoryName = category,
        CategoryColor = color ?? "#64748b",
        RecordType = "window",
        RecordKeyVersion = "pc-fallback-v1",
        RecordKeyStability = "low",
        SourceType = "fallback",
        InterpretationVersion = "interpreted-aw-v1",
        ProjectTag = null,
        Confidence = 0.2,
        Source = "fallback",
        Explanation = string.Empty,
        ClassifierVersion = "local-v1",
        ClassifiedAt = DateTimeOffset.Parse(start)
    };

    [Fact]
    public async Task GetCategoryDistributionAsync_SumsSnapshotDurations()
    {
        await using var db = CreateDb();
        // 编程/折腾 30min + 学习 40min + 编程/折腾 30min → 60min 60% / 40min 40%
        db.Set<ActivityClassificationEntity>().AddRange(
            Snap("2026-07-10T01:00:00Z", "2026-07-10T01:30:00Z", "编程/折腾", "#6B5EE4"),
            Snap("2026-07-10T01:30:00Z", "2026-07-10T02:10:00Z", "学习", "#14b8a6"),
            Snap("2026-07-10T02:30:00Z", "2026-07-10T03:00:00Z", "编程/折腾", "#6B5EE4"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("编程/折腾", result.Items[0].CategoryName);
        Assert.Equal(60, result.Items[0].Minutes);
        Assert.Equal(60.0, result.Items[0].Percentage, 1);
        Assert.Equal("学习", result.Items[1].CategoryName);
        Assert.Equal(40, result.Items[1].Minutes);
        Assert.Equal(40.0, result.Items[1].Percentage, 1);
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_FiltersByStartedAtWindow()
    {
        await using var db = CreateDb();
        // 07-10T20:00Z 恰为业务日窗口终点（04:00 本地 07-11）→ 不计
        db.Set<ActivityClassificationEntity>().AddRange(
            Snap("2026-07-10T01:00:00Z", "2026-07-10T01:30:00Z", "编程/折腾", "#6B5EE4"),
            Snap("2026-07-10T20:00:00Z", "2026-07-10T21:00:00Z", "学习", "#14b8a6"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("编程/折腾", item.CategoryName);
        Assert.Equal(30, item.Minutes);
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_CapsSingleSnapshotHour()
    {
        await using var db = CreateDb();
        // Ended-Started = 2h → 只计 60min
        db.Set<ActivityClassificationEntity>().Add(
            Snap("2026-07-10T01:00:00Z", "2026-07-10T03:00:00Z", "编程/折腾", "#6B5EE4"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(60, item.Minutes);
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_EmptyReturnsEmptyItems()
    {
        await using var db = CreateDb();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_FallsBackColor()
    {
        await using var db = CreateDb();
        // 编程/折腾 空色 → UnifiedColors #6B5EE4；未知分类空色 → #64748b；有效色直接使用
        db.Set<ActivityClassificationEntity>().AddRange(
            Snap("2026-07-10T01:00:00Z", "2026-07-10T01:30:00Z", "编程/折腾", ""),
            Snap("2026-07-10T02:00:00Z", "2026-07-10T02:20:00Z", "神秘分类", ""),
            Snap("2026-07-10T03:00:00Z", "2026-07-10T03:10:00Z", "学习", "#123456"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal("#6B5EE4", result.Items[0].Color); // 编程/折腾 → UnifiedColors 兜底
        Assert.Equal("#64748b", result.Items[1].Color); // 未知分类 → 默认灰
        Assert.Equal("#123456", result.Items[2].Color); // 有效快照色直接使用
    }

    [Fact]
    public async Task GetCategoryDistributionAsync_RejectsNonHexColor()
    {
        await using var db = CreateDb();
        // #zzzzzz 长度与前缀都对但非十六进制 → 兜底 UnifiedColors
        db.Set<ActivityClassificationEntity>().AddRange(
            Snap("2026-07-10T01:00:00Z", "2026-07-10T01:30:00Z", "编程/折腾", "#zzzzzz"));
        await db.SaveChangesAsync();
        var service = new PcActivityAggregationService(db);

        var result = await service.GetCategoryDistributionAsync(DayQuery("2026-07-10"), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("#6B5EE4", item.Color);
    }
}
