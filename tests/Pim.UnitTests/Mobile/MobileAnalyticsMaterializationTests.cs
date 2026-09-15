using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// issue #247②：两张派生表（mobile_timeline_blocks / mobile_usage_aggregates）生产里始终 0 行，
/// 块与聚合全部在线计算，每个端点各算一遍。
///
/// 这里锁定三件事：
/// 1. 上传带新条目时会把窗口的派生数据落库（且窗口按本地整日对齐）；
/// 2. 端点在被物化的窗口上直接读派生表，结果与在线计算逐字段一致（不引入口径漂移）；
/// 3. 形状不可复现 / 未被物化 / 已失效时回退在线计算，结果同样正确。
/// </summary>
public sealed class MobileAnalyticsMaterializationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
    private const string DeviceId = "phone-materialized";

    // 本地日（Asia/Shanghai）2026-07-06 00:00 = 2026-07-05T16:00Z
    private static readonly DateTimeOffset DayStartUtc = DateTimeOffset.Parse("2026-07-05T16:00:00Z");
    private static readonly DateTimeOffset DayEndUtc = DateTimeOffset.Parse("2026-07-06T16:00:00Z");

    [Fact]
    public async Task IngestAsync_MaterializesDerivedTablesForTheBatchWindow()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var ingest = CreateIngest(db);
        var request = UploadRequest();

        await ingest.IngestAsync(request, CancellationToken.None);

        var aggregates = await db.Set<MobileUsageAggregateEntity>().ToListAsync();
        var blocks = await db.Set<MobileTimelineBlockEntity>().ToListAsync();
        Assert.NotEmpty(aggregates);
        Assert.All(aggregates, row => Assert.False(row.IsStale));
        Assert.All(aggregates, row => Assert.Equal(MobileAnalyticsDefaults.HourGranularity, row.Granularity));
        Assert.All(aggregates, row => Assert.Equal(600, row.ForegroundSeconds));
        Assert.NotEmpty(blocks);
        Assert.All(blocks, row => Assert.False(string.IsNullOrWhiteSpace(row.BlockId)));
        Assert.All(blocks, row => Assert.Equal(600, row.ForegroundSeconds));

        // 物化窗口按本地整日对齐（批次窗口只有 1 小时）。
        var coverage = Assert.Single(await db.Set<MobileAnalyticsMaterializationEntity>().ToListAsync());
        Assert.Equal(DayStartUtc, coverage.CoveredFromUtc);
        Assert.Equal(DayEndUtc, coverage.CoveredToUtc);
        Assert.Equal(MobileAnalyticsDefaults.DefaultTimezone, coverage.Timezone);
    }

    [Fact]
    public async Task GetHeatmapAsync_ReadsMaterializedBucketsWithIdenticalResult()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        var service = CreateAggregation(db);

        var request = new MobileAnalyticsQueryRequest(DayStartUtc, DayEndUtc, DeviceId: DeviceId);
        // 先物化，再对比"读派生表"与"在线计算"两条路径。
        await MaterializeAsync(db);
        var fromCache = await service.GetHeatmapAsync(request, CancellationToken.None);

        await ClearDerivedRowsAsync(db);
        var computed = await service.GetHeatmapAsync(request, CancellationToken.None);

        Assert.NotEmpty(computed);
        Assert.Equivalent(computed, fromCache, strict: true);
    }

    [Fact]
    public async Task GetBlocksAsync_ReadsMaterializedBlocksWithIdenticalResult()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        var service = CreateBlocks(db);

        var request = new MobileAnalyticsQueryRequest(DayStartUtc, DayEndUtc, DeviceId: DeviceId);
        await MaterializeAsync(db);
        var fromCache = await service.GetBlocksAsync(request, CancellationToken.None);

        await ClearDerivedRowsAsync(db);
        var computed = await service.GetBlocksAsync(request, CancellationToken.None);

        Assert.NotEmpty(computed.Items);
        Assert.Equivalent(computed.Items, fromCache.Items, strict: true);
        Assert.Equal(computed.TotalCount, fromCache.TotalCount);
    }

    [Fact]
    public async Task GetBlocksAsync_FallsBackWhenTheRequestRangeIsNotCoveredExactly()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        var service = CreateBlocks(db);
        await MaterializeAsync(db);

        // 只请求半天：物化窗口是整天，边界裁剪口径不同 => 必须回退在线计算且结果仍然正确。
        var request = new MobileAnalyticsQueryRequest(DayStartUtc, DayStartUtc.AddHours(12), DeviceId: DeviceId);
        var withDerivedRows = await service.GetBlocksAsync(request, CancellationToken.None);

        await ClearDerivedRowsAsync(db);
        var computed = await service.GetBlocksAsync(request, CancellationToken.None);

        Assert.NotEmpty(computed.Items);
        Assert.Equivalent(computed.Items, withDerivedRows.Items, strict: true);
    }

    [Fact]
    public async Task GetHeatmapAsync_FallsBackWhenMaterializedRowsAreStale()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        var service = CreateAggregation(db);
        await MaterializeAsync(db);

        foreach (var row in await db.Set<MobileUsageAggregateEntity>().ToListAsync())
        {
            row.IsStale = true;
        }

        await db.SaveChangesAsync();

        var request = new MobileAnalyticsQueryRequest(DayStartUtc, DayEndUtc, DeviceId: DeviceId);
        var stale = await service.GetHeatmapAsync(request, CancellationToken.None);

        await ClearDerivedRowsAsync(db);
        var computed = await service.GetHeatmapAsync(request, CancellationToken.None);

        // 失效标记不能改变结果：回退到在线计算，与物化前逐字段一致（含嵌套集合，用结构化比较）。
        Assert.NotEmpty(computed);
        Assert.Equivalent(computed, stale, strict: true);
    }

    [Fact]
    public async Task GetHeatmapAsync_FallsBackForNonHourGranularityAndNonDefaultDuration()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        var service = CreateAggregation(db);
        await MaterializeAsync(db);

        foreach (var granularity in new[] { "30m", "day" })
        {
            var request = new MobileAnalyticsQueryRequest(
                DayStartUtc,
                DayEndUtc,
                DeviceId: DeviceId,
                Granularity: granularity);
            await MaterializeAsync(db);
            var withDerivedRows = await service.GetHeatmapAsync(request, CancellationToken.None);
            await ClearDerivedRowsAsync(db);
            var computed = await service.GetHeatmapAsync(request, CancellationToken.None);
            Assert.Equivalent(computed, withDerivedRows, strict: true);
        }

        // minDurationSeconds 不是默认值时同样回退（分桶前就要排除短事件，无法事后修正）。
        var customDuration = new MobileAnalyticsQueryRequest(
            DayStartUtc,
            DayEndUtc,
            DeviceId: DeviceId,
            MinDurationSeconds: 0);
        await MaterializeAsync(db);
        var cachedCustom = await service.GetHeatmapAsync(customDuration, CancellationToken.None);
        await ClearDerivedRowsAsync(db);
        var computedCustom = await service.GetHeatmapAsync(customDuration, CancellationToken.None);
        Assert.Equivalent(computedCustom, cachedCustom, strict: true);
    }

    [Fact]
    public async Task MaterializeAsync_ReplacesRowsInsteadOfDuplicatingThem()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);

        await MaterializeAsync(db);
        var firstCount = await db.Set<MobileUsageAggregateEntity>().CountAsync();
        await MaterializeAsync(db);

        Assert.Equal(firstCount, await db.Set<MobileUsageAggregateEntity>().CountAsync());
        Assert.Single(await db.Set<MobileAnalyticsMaterializationEntity>().ToListAsync());
    }

    [Fact]
    public async Task MaterializeAsync_RemovesStaleRowsAfterTheWindowIsRebuilt()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedSessionsAsync(db);
        db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            Granularity = MobileAnalyticsDefaults.HourGranularity,
            BucketStartUtc = DayStartUtc,
            BucketEndUtc = DayStartUtc.AddHours(1),
            PackageName = "com.example.old",
            LifeCategory = MobileLifeCategories.Uncategorized,
            ForegroundSeconds = 999,
            IsStale = true,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        await MaterializeAsync(db);

        var rows = await db.Set<MobileUsageAggregateEntity>().ToListAsync();
        Assert.DoesNotContain(rows, row => row.PackageName == "com.example.old");
        Assert.All(rows, row => Assert.False(row.IsStale));
    }

    private static async Task MaterializeAsync(PimDbContext db)
    {
        var service = CreateMaterialization(db);
        await service.MaterializeAsync(
            MobileTestHelpers.UserId,
            DeviceId,
            DayStartUtc.AddHours(1),
            DayStartUtc.AddHours(2),
            CancellationToken.None);
    }

    private static async Task ClearDerivedRowsAsync(PimDbContext db)
    {
        db.Set<MobileUsageAggregateEntity>().RemoveRange(await db.Set<MobileUsageAggregateEntity>().ToListAsync());
        db.Set<MobileTimelineBlockEntity>().RemoveRange(await db.Set<MobileTimelineBlockEntity>().ToListAsync());
        db.Set<MobileAnalyticsMaterializationEntity>().RemoveRange(
            await db.Set<MobileAnalyticsMaterializationEntity>().ToListAsync());
        await db.SaveChangesAsync();
    }

    private static async Task SeedSessionsAsync(PimDbContext db)
    {
        db.Set<MobileUsageSessionEntity>().AddRange(
            Session("com.example.video", DayStartUtc.AddHours(1), DayStartUtc.AddHours(1).AddMinutes(30)),
            Session("com.example.chat", DayStartUtc.AddHours(1).AddMinutes(40), DayStartUtc.AddHours(2).AddMinutes(10)),
            Session("com.example.reader", DayStartUtc.AddHours(5), DayStartUtc.AddHours(5).AddMinutes(20)));
        await db.SaveChangesAsync();
    }

    private static MobileUsageSessionEntity Session(string packageName, DateTimeOffset start, DateTimeOffset end) => new()
    {
        UserId = MobileTestHelpers.UserId,
        DeviceId = DeviceId,
        PackageName = packageName,
        StartUtc = start,
        EndUtc = end,
        DurationMs = (long)(end - start).TotalMilliseconds,
        QualityFlagsJson = "[]",
        CreatedAt = Now
    };

    private static MobileUsageIngestService CreateIngest(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        return new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db, timeProvider),
            timeProvider,
            catalogOverrideService: null,
            materializationService: CreateMaterialization(db));
    }

    private static MobileAnalyticsMaterializationService CreateMaterialization(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        return new MobileAnalyticsMaterializationService(
            db,
            CreateAggregation(db),
            CreateBlocks(db),
            timeProvider);
    }

    private static MobileUsageAggregationService CreateAggregation(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        var currentUser = MobileTestHelpers.CurrentUser();
        return new MobileUsageAggregationService(
            db,
            currentUser,
            new MobileAnalyticsQueryService(timeProvider),
            new MobileUsageGoalService(db, currentUser, timeProvider),
            timeProvider,
            new MobileAppClassificationService(db, currentUser));
    }

    private static MobileTimelineBlockService CreateBlocks(PimDbContext db)
    {
        var timeProvider = MobileTestHelpers.Time(Now);
        return new MobileTimelineBlockService(
            db,
            MobileTestHelpers.CurrentUser(),
            timeProvider,
            new MobileAppClassificationService(db, MobileTestHelpers.CurrentUser()));
    }

    private static MobileUsageEventsUploadRequest UploadRequest()
    {
        var start = DayStartUtc.AddHours(1);
        return new MobileUsageEventsUploadRequest(
            DeviceId,
            "batch-materialize",
            start,
            start.AddHours(1),
            [],
            [
                new MobileUsageEventDto(
                    "com.example.video",
                    "MOVE_TO_FOREGROUND",
                    start,
                    "VideoActivity",
                    start.AddMinutes(1),
                    "{}"),
                new MobileUsageEventDto(
                    "com.example.video",
                    "MOVE_TO_BACKGROUND",
                    start.AddMinutes(10),
                    "VideoActivity",
                    start.AddMinutes(11),
                    "{}")
            ],
            []);
    }
}
