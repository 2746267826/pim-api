using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20261001 · REQ-2（#371）：内部全量取数路径必须给出跨度边界。
/// <para>
/// 缺陷：<see cref="PcTrackerService.QueryAllDetailRecordsAsync"/> 只校验 <c>end &gt;= start</c>，
/// 跨度不设上限 —— 传 30/90 天会把整段记录载入内存（克隆库实测 30 天 33,707 条 / 2,279 MiB / 18.1 s，
/// 60 天起直接以写库异常告终）。
/// </para>
/// </summary>
public sealed class PcInternalQuerySpanLimitTests
{
    private static readonly DateTime Day = new(2026, 9, 27);

    /// <summary>
    /// 上限 = 7 个业务日（<c>PcTrackerService.MaxInternalQuerySpanDays</c>）。
    /// 用例刻意用字面量而不是引用常量：这样「未实现上限」的代码也能编译，
    /// 失败原因是断言（超限没被拒绝），而不是编译不过。
    /// </summary>
    private const int SpanLimitDays = 7;

    /// <summary>AC-2.2：超过上限必须直接拒绝，并给出含上限值与建议做法的可读文案。</summary>
    [Fact]
    public async Task QueryAllDetailRecordsAsync_RejectsRequestBeyondSpanLimit()
    {
        await using var db = CreateDb();
        var service = Service(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAllDetailRecordsAsync(
            Query(Day.AddDays(-SpanLimitDays), Day), CancellationToken.None));

        // 文案必须自带「上限值」与「建议做法」，消费方无需读源码才能知道怎么改请求。
        Assert.Contains(SpanLimitDays.ToString(), ex.Message);
        Assert.Contains("个业务日", ex.Message);
        Assert.Contains("拆分", ex.Message);
        // 文案里的业务日必须是**被请求的那些业务日**（范围端点是 UTC 时刻，按 UTC 格式化会整体差一天）。
        Assert.Contains(Day.AddDays(-SpanLimitDays).ToString("yyyy-MM-dd"), ex.Message);
        Assert.Contains(Day.ToString("yyyy-MM-dd"), ex.Message);
    }

    /// <summary>AC-2.1：正好等于上限的跨度合法，不得误伤。</summary>
    [Fact]
    public async Task QueryAllDetailRecordsAsync_AllowsRequestAtSpanLimit()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        AddTrackerWindow(db, dayStart.AddHours(2), 600);
        await db.SaveChangesAsync();

        var records = await Service(db).QueryAllDetailRecordsAsync(
            Query(Day.AddDays(-(SpanLimitDays - 1)), Day), CancellationToken.None);

        Assert.NotEmpty(records);
    }

    /// <summary>
    /// AC-2.4：超限请求必须在**取数之前**被拒 —— 不得先全量拉一遍再报错。
    /// <para>
    /// 断言对象是取数路径的副作用：<c>BuildCompleteDetailRecordsAsync</c> 会把分类快照写进
    /// <c>pc_activity_classifications</c>。先在同等数据下跑一次合法跨度，证明该副作用确实会发生；
    /// 再用超限跨度请求，断言一条快照都没多出来，即「没有触发全量取数」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task QueryAllDetailRecordsAsync_RejectedRequestDoesNotFetchRecords()
    {
        await using var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        AddTrackerWindow(db, dayStart.AddHours(2), 600);
        await db.SaveChangesAsync();
        var service = Service(db);

        var legalQuery = Query(Day, Day);
        Assert.NotEmpty(await service.QueryAllDetailRecordsAsync(legalQuery, CancellationToken.None));
        var snapshotsAfterLegalFetch = await db.Set<ActivityClassificationEntity>().CountAsync();
        Assert.True(
            snapshotsAfterLegalFetch > 0,
            "前置条件不成立：合法跨度取数应当写入分类快照，否则本用例无法证明「超限未取数」");

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAllDetailRecordsAsync(
            Query(Day.AddDays(-30), Day), CancellationToken.None));

        Assert.Equal(snapshotsAfterLegalFetch, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    private static DetailQueryParams Query(DateTime from, DateTime to) => new(
        from.ToString("yyyy-MM-dd"),
        to.ToString("yyyy-MM-dd"),
        null, null, null, null, null, null,
        "date", "asc", 1, 2000, View: "interpreted");

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
