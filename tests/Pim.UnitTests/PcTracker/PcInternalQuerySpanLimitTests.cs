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

    /// <summary>
    /// #390 / REQ-12 / AC-12.1 + AC-12.2：守卫文案必须与实现一致 ——
    /// 不得再声称对外明细端点「不能用来绕过本上限」（该端点根本不执行本上限），
    /// 但必须继续给出「按业务日拆分请求」这一可执行动作。
    /// <para>
    /// 该守卫**没有可触发的 HTTP 入口**（两个内部调用方都只传单一业务日），
    /// 因此这里按 AC-12.1 的要求直接调用内部取数路径。
    /// </para>
    /// </summary>
    [Fact]
    public async Task QueryAllDetailRecordsAsync_GuardMessageMatchesTheActualBehaviour()
    {
        await using var db = CreateDb();
        var service = Service(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAllDetailRecordsAsync(
            Query(Day.AddDays(-30), Day), CancellationToken.None));

        // AC-12.1：不再出现与实现相反的说法
        Assert.DoesNotContain("/pc/detail", ex.Message);
        Assert.DoesNotContain("不能用来绕过", ex.Message);
        Assert.DoesNotContain("绕过", ex.Message);

        // AC-12.2：可执行动作仍在
        Assert.Contains("按业务日拆分请求", ex.Message);
        Assert.Contains("每个业务日一次", ex.Message);
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
    /// <c>pc_activity_classifications</c>。顺序很关键：先发超限请求并断言一条快照都没有
    /// （守卫若被放到取数之后，这条记录就会被分类并落库 → 用例失败），再用同一天内的合法请求
    /// 证明该副作用确实会发生（正对照）。
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

        // 1) 超限请求：记录本身落在范围内，一旦先取数就会被分类落库。
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAllDetailRecordsAsync(
            Query(Day.AddDays(-30), Day), CancellationToken.None));
        Assert.Equal(0, await db.Set<ActivityClassificationEntity>().CountAsync());

        // 2) 正对照：合法跨度取数确实会写入分类快照，说明上面的「0」不是断言写错。
        Assert.NotEmpty(await service.QueryAllDetailRecordsAsync(Query(Day, Day), CancellationToken.None));
        Assert.True(
            await db.Set<ActivityClassificationEntity>().CountAsync() > 0,
            "正对照不成立：合法跨度取数应当写入分类快照");
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
