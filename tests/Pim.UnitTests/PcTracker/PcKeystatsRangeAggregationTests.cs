using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-7（聚合组新增键鼠范围聚合）。
/// </summary>
public sealed class PcKeystatsRangeAggregationTests
{
    private readonly ITestOutputHelper _output;

    public PcKeystatsRangeAggregationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task GetKeystatsRangeAsync_MatchesPerDaySummaryAggregateForEveryKey()
    {
        await using var db = CreateDb();
        var start = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 30);      // 近 30 天
        SeedKeystats(db, start, days: 30);
        await db.SaveChangesAsync();

        var service = new PcActivityAggregationService(db);
        var range = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-08-01", "2026-08-30", null), CancellationToken.None);

        // AC-7.1：与逐日单日 summary.keystats 的聚合结果逐键一致（全量对照）。
        var tracker = Tracker(db);
        var expectedCounts = new Dictionary<string, int>();
        var expectedKeys = 0;
        var expectedLeft = 0;
        var expectedMiddle = 0;
        var expectedRight = 0;
        var expectedClicks = 0;
        double expectedScroll = 0;
        var expectedPeakKps = 0;
        var expectedPeakCps = 0;
        var expectedSideBack = 0;
        var expectedSideForward = 0;
        double expectedMouse = 0;

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var summary = await tracker.GetSummaryAsync(day, CancellationToken.None);
            var keystats = summary.Keystats!;
            expectedKeys += keystats.KeyPresses;
            expectedLeft += keystats.LeftClicks;
            expectedMiddle += keystats.MiddleClicks;
            expectedRight += keystats.RightClicks;
            expectedClicks += keystats.TotalClicks;
            expectedScroll += keystats.ScrollDistance;
            expectedPeakKps = Math.Max(expectedPeakKps, keystats.PeakKps);
            expectedPeakCps = Math.Max(expectedPeakCps, keystats.PeakCps);
            expectedSideBack += keystats.SideBackClicks;
            expectedSideForward += keystats.SideForwardClicks;
            expectedMouse += keystats.MouseDistance;
            foreach (var (key, count) in keystats.KeyPressCounts)
                expectedCounts[key] = expectedCounts.GetValueOrDefault(key) + count;
        }

        Assert.Equal(expectedKeys, range.TotalKeyPresses);
        Assert.Equal(expectedLeft, range.LeftClicks);
        Assert.Equal(expectedMiddle, range.MiddleClicks);
        Assert.Equal(expectedRight, range.RightClicks);
        Assert.Equal(expectedClicks, range.TotalClicks);
        Assert.Equal(expectedScroll, range.ScrollDistance, 6);
        Assert.Equal(expectedPeakKps, range.PeakKps);
        Assert.Equal(expectedPeakCps, range.PeakCps);
        // AC-7.3：与单日版同构的补充字段（前端复用同一组件）。
        Assert.Equal(expectedKeys, range.KeyPresses);
        Assert.Equal(expectedSideBack, range.SideBackClicks);
        Assert.Equal(expectedSideForward, range.SideForwardClicks);
        Assert.Equal(expectedMouse, range.MouseDistance, 6);
        Assert.Equal(expectedCounts.Count, range.KeyPressCounts.Count);
        foreach (var (key, count) in expectedCounts)
            Assert.Equal(count, range.KeyPressCounts[key]);
    }

    [Fact]
    public async Task GetKeystatsRangeAsync_KeepsTopKeysBoundedSoPayloadDoesNotGrowWithDays()
    {
        await using var db = CreateDb();
        SeedKeystats(db, new DateTime(2026, 8, 1), days: 30);
        await db.SaveChangesAsync();

        var service = new PcActivityAggregationService(db);
        var range = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-08-01", "2026-08-30", null), CancellationToken.None);

        // 键分布按 keyName 合并，Top 列表只留 TopN（10），规模不随天数线性膨胀。
        Assert.Equal(10, range.TopKeys.Count);
        Assert.Equal(range.TopKeys.OrderByDescending(k => k.Count).Select(k => k.Count),
            range.TopKeys.Select(k => k.Count));
        Assert.All(range.TopKeys, key =>
            Assert.Equal((double)key.Count / range.TotalKeyPresses, key.Share, 6));
    }

    [Fact]
    public async Task GetKeystatsRangeAsync_UsesLatestRowPerDayLikeSingleDaySummary()
    {
        await using var db = CreateDb();
        var day = new DateTime(2026, 9, 2);
        db.Set<KeystatsDailyEntity>().AddRange(
            Row(day, keyPresses: 100, createdAt: DateTimeOffset.Parse("2026-09-02T10:00:00Z"), key: "A"),
            Row(day, keyPresses: 700, createdAt: DateTimeOffset.Parse("2026-09-02T12:00:00Z"), key: "B"));
        await db.SaveChangesAsync();

        var service = new PcActivityAggregationService(db);
        var range = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-09-02", "2026-09-02", null), CancellationToken.None);

        Assert.Equal(700, range.TotalKeyPresses);
        Assert.Equal(700, range.KeyPressCounts["B"]);
    }

    [Fact]
    public async Task GetKeystatsRangeAsync_EmptyRangeAndCrossMonthBoundariesAreWellFormed()
    {
        await using var db = CreateDb();
        db.Set<KeystatsDailyEntity>().Add(Row(new DateTime(2026, 8, 31), 500, DateTimeOffset.Parse("2026-08-31T10:00:00Z"), "A"));
        db.Set<KeystatsDailyEntity>().Add(Row(new DateTime(2026, 9, 1), 300, DateTimeOffset.Parse("2026-09-01T10:00:00Z"), "B"));
        await db.SaveChangesAsync();

        var service = new PcActivityAggregationService(db);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 空范围：字段齐全、全 0，不抛异常。
        var empty = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-07-01", "2026-07-05", null), CancellationToken.None);
        stopwatch.Stop();
        var emptyMs = stopwatch.ElapsedMilliseconds;
        Assert.Equal(0, empty.TotalKeyPresses);
        Assert.Equal(0, empty.TotalClicks);
        Assert.Empty(empty.KeyPressCounts);
        Assert.Empty(empty.TopKeys);

        // 跨月边界：8/31 与 9/1 都计入。
        stopwatch.Restart();
        var crossMonth = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-08-31", "2026-09-01", null), CancellationToken.None);
        stopwatch.Stop();
        var crossMonthMs = stopwatch.ElapsedMilliseconds;
        Assert.Equal(800, crossMonth.TotalKeyPresses);
        Assert.Equal(500, crossMonth.KeyPressCounts["A"]);
        Assert.Equal(300, crossMonth.KeyPressCounts["B"]);

        // 单日：与单日版一致。
        stopwatch.Restart();
        var single = await service.GetKeystatsRangeAsync(
            new PcAggregationQuery("2026-09-01", null, null, null), CancellationToken.None);
        stopwatch.Stop();
        _output.WriteLine(
            $"keystats range latency: empty={emptyMs}ms crossMonth={crossMonthMs}ms single={stopwatch.ElapsedMilliseconds}ms");

        // AC-7.2：空范围 / 单日 / 跨月边界的耗时实测（数字见输出），并给宽松上界防回归。
        Assert.True(emptyMs < 2000 && crossMonthMs < 2000 && stopwatch.ElapsedMilliseconds < 2000);
        var summary = await Tracker(db).GetSummaryAsync(new DateTime(2026, 9, 1), CancellationToken.None);
        Assert.Equal(summary.Keystats!.KeyPresses, single.TotalKeyPresses);
        Assert.Equal(summary.Keystats.TotalClicks, single.TotalClicks);
    }

    [Fact]
    public async Task GetKeystatsRangeAsync_FallsBackToSamplesForDaysWithoutDailySnapshot()
    {
        await using var db = CreateDb();
        var withDaily = new DateTime(2026, 9, 1);
        var sampleOnly = new DateTime(2026, 9, 2);
        db.Set<KeystatsDailyEntity>().Add(Row(withDaily, 900, DateTimeOffset.Parse("2026-09-01T10:00:00Z"), "A"));
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "device-1",
            SampledAtUtc = DateTimeOffset.Parse("2026-09-02T10:00:00Z"),
            StatsDate = sampleOnly,
            KeyPresses = 400,
            LeftClicks = 7,
            RightClicks = 3,
            MiddleClicks = 1,
            SideBackClicks = 2,
            SideForwardClicks = 2,
            ScrollDistance = 12.5,
            PeakKps = 6,
            PeakCps = 4,
            KeyCountsJson = "{\"B\":400}"
        });
        await db.SaveChangesAsync();

        var range = await new PcActivityAggregationService(db).GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-09-01", "2026-09-02", null), CancellationToken.None);

        // AC-7.1：逐日与单日版同口径 —— 9/2 没有日快照，单日接口会回退到采样，范围聚合必须一致。
        var single = await Tracker(db).GetSummaryAsync(sampleOnly, CancellationToken.None);
        Assert.Equal(400, single.Keystats!.KeyPresses);
        Assert.Equal(900 + 400, range.TotalKeyPresses);
        Assert.Equal(400, range.KeyPressCounts["B"]);
        Assert.Equal(single.Keystats.LeftClicks, range.LeftClicks);
        Assert.Equal(single.Keystats.TotalClicks, range.TotalClicks);
        Assert.Equal(6, range.PeakKps);
        Assert.Equal(12.5, range.ScrollDistance, 6);
    }

    [Fact]
    public async Task GetKeystatsRangeAsync_StartAfterEndThrows()
    {
        await using var db = CreateDb();
        var service = new PcActivityAggregationService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetKeystatsRangeAsync(
            new PcAggregationQuery(null, "2026-09-05", "2026-09-01", null), CancellationToken.None));
    }

    private static PcTrackerService Tracker(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static void SeedKeystats(PimDbContext db, DateTime start, int days)
    {
        for (var i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            var row = Row(day, keyPresses: 1000 + i * 37, createdAt: new DateTimeOffset(day, TimeSpan.Zero).AddHours(12), key: $"K{i % 25}");
            row.LeftClicks = 10 + i;
            row.RightClicks = 5 + i;
            row.MiddleClicks = i % 3;
            row.ScrollDistance = 100.5 + i;
            row.PeakKps = 5 + (i % 7);
            row.PeakCps = 3 + (i % 4);
            row.SideBackClicks = 1;
            row.SideForwardClicks = 2;
            row.MouseDistance = 1000.5 + i;
            db.Set<KeystatsDailyEntity>().Add(row);
        }
    }

    private static KeystatsDailyEntity Row(DateTime day, int keyPresses, DateTimeOffset createdAt, string key)
    {
        var row = new KeystatsDailyEntity
        {
            DeviceId = "device-1",
            SnapshotDate = day.Date,
            KeyPresses = keyPresses,
            CreatedAt = createdAt
        };
        row.KeyCounts.Add(new KeystatsKeyCountEntity { KeyName = key, Count = keyPresses });
        return row;
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(KeystatsDailyEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
