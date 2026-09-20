using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// #330：手机端 timeline 曾把 sessions / fallbackSummaries 各硬编码截断在前 500 条，
/// 且返回体没有任何分页或截断标记 —— 调用方只看到当日约前 2~3 小时的数据，
/// 下午/晚间数据被静默丢弃（实测 2026-09-18 库内 2049 条 / 接口仅 500 条）。
///
/// 真库实测（pim_test 镜像，80 个业务日）：单设备单日会话峰值 2922 条，
/// 跨设备单日峰值 7863 条；fallback 汇总单设备单日峰值 30444 条。
/// 因此这里锁定修复后的契约：
/// 1. 默认上限提高到 <see cref="MobileTimelinePagination.DefaultPageSize"/>，
///    覆盖绝大多数真实业务日，不再在 500 条处静默截断；
/// 2. 超出上限时返回体必须带可感知标记（totalCount / hasMore / truncated / page / pageSize），
///    而不是让调用方无从察觉；
/// 3. 传 page/pageSize 可翻页取回全天数据，越界参数被夹紧而不是抛出。
/// </summary>
public sealed class MobileTimelinePaginationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");

    /// <summary>业务日窗口：Asia/Shanghai 2026-09-18 04:00 == UTC 2026-09-17 20:00。</summary>
    private static readonly DateTimeOffset WindowStart = DateTimeOffset.Parse("2026-09-17T20:00:00Z");
    private static readonly DateTimeOffset WindowEnd = DateTimeOffset.Parse("2026-09-18T20:00:00Z");

    /// <summary>
    /// 在业务日窗口内均布 n 条互不重叠的会话（首条贴窗口起点、末条贴窗口末尾），
    /// 使「最后一条」代表晚间数据 —— 修复前它永远落在 500 条截断之外。
    /// </summary>
    private static void SeedSessions(PimDbContext db, int count, string deviceId = "android-main")
    {
        var windowTicks = (WindowEnd - WindowStart).Ticks;
        var step = TimeSpan.FromTicks(windowTicks / count);
        var sessions = new List<MobileUsageSessionEntity>(count);

        for (var i = 0; i < count; i++)
        {
            var start = WindowStart.AddTicks(step.Ticks * i);
            sessions.Add(new MobileUsageSessionEntity
            {
                Id = Guid.NewGuid(),
                UserId = MobileTestHelpers.UserId,
                DeviceId = deviceId,
                PackageName = $"com.example.app{i % 7}",
                StartUtc = start,
                EndUtc = start.Add(step),
                DurationMs = (long)step.TotalMilliseconds,
                QualityFlagsJson = "[]",
                CreatedAt = start
            });
        }

        db.Set<MobileUsageSessionEntity>().AddRange(sessions);
        db.SaveChanges();
    }

    private static void SeedFallbackSummaries(PimDbContext db, int count, string deviceId = "android-main")
    {
        var windowTicks = (WindowEnd - WindowStart).Ticks;
        var step = TimeSpan.FromTicks(windowTicks / count);
        var summaries = new List<MobileUsageSummaryEntity>(count);

        for (var i = 0; i < count; i++)
        {
            var start = WindowStart.AddTicks(step.Ticks * i);
            summaries.Add(new MobileUsageSummaryEntity
            {
                Id = Guid.NewGuid(),
                UserId = MobileTestHelpers.UserId,
                DeviceId = deviceId,
                PackageName = $"com.example.fallback{i % 5}",
                WindowStartUtc = start,
                WindowEndUtc = start.Add(step),
                TotalTimeVisibleMs = (long)step.TotalMilliseconds,
                SourceKind = "usage-stats-fallback",
                CreatedAt = start
            });
        }

        db.Set<MobileUsageSummaryEntity>().AddRange(summaries);
        db.SaveChanges();
    }

    private static MobileUsageQueryService Service(PimDbContext db)
        => new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    private static MobileTimelineQuery Query(int? page = null, int? pageSize = null)
        => new("android-main", WindowStart, WindowEnd, page, pageSize);

    [Fact]
    public async Task GetTimelineAsync_ReturnsEverySessionWhenDayExceedsLegacy500Cap()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 1200);

        var response = await Service(db).GetTimelineAsync(Query(), CancellationToken.None);

        // 修复前：恰好 500（静默截断）。修复后：1200 条全量返回且不标记截断。
        Assert.Equal(1200, response.Sessions.Count);
        Assert.Equal(1200, response.Items.Count);
        Assert.Equal(1200, response.TotalCount);
        Assert.False(response.Truncated);
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task GetTimelineAsync_MarksTruncationInsteadOfSilentlyDroppingRows()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 比默认页大小多出的部分必须显式声明「还有数据没给你」，而不是静默丢弃。
        var total = MobileTimelinePagination.DefaultPageSize + 500;
        SeedSessions(db, total);

        var page1 = await Service(db).GetTimelineAsync(Query(), CancellationToken.None);

        Assert.Equal(MobileTimelinePagination.DefaultPageSize, page1.Sessions.Count);
        Assert.Equal(total, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.True(page1.Truncated);
        Assert.Equal(1, page1.Page);
        Assert.Equal(MobileTimelinePagination.DefaultPageSize, page1.PageSize);

        // 第二页取回剩余 500 条，并明确告知已到末尾
        var page2 = await Service(db).GetTimelineAsync(
            Query(page: 2, pageSize: MobileTimelinePagination.DefaultPageSize),
            CancellationToken.None);
        Assert.Equal(500, page2.Sessions.Count);
        Assert.Equal(total, page2.TotalCount);
        Assert.False(page2.HasMore);
        Assert.False(page2.Truncated);
    }

    [Fact]
    public async Task GetTimelineAsync_ExplicitPagingWalksWholeDayWithoutDuplicates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 1200);

        var service = Service(db);
        var first = await service.GetTimelineAsync(Query(page: 1, pageSize: 500), CancellationToken.None);
        var second = await service.GetTimelineAsync(Query(page: 2, pageSize: 500), CancellationToken.None);
        var third = await service.GetTimelineAsync(Query(page: 3, pageSize: 500), CancellationToken.None);

        Assert.Equal(500, first.Sessions.Count);
        Assert.Equal(500, second.Sessions.Count);
        Assert.Equal(200, third.Sessions.Count);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);

        var all = first.Sessions.Concat(second.Sessions).Concat(third.Sessions).ToList();
        Assert.Equal(1200, all.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(new[] { first, second, third }, page => Assert.Equal(1200, page.TotalCount));

        // 结果保持按时间正序（分页不得打乱顺序）
        var starts = all.Select(item => item.Start).ToList();
        Assert.Equal(starts.OrderBy(value => value).ToList(), starts);
    }

    [Fact]
    public async Task GetTimelineAsync_LateDaySessionsAreReachable()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 1200);

        var service = Service(db);
        var firstPage = await service.GetTimelineAsync(Query(page: 1, pageSize: 500), CancellationToken.None);
        var lastPage = await service.GetTimelineAsync(Query(page: 3, pageSize: 500), CancellationToken.None);

        // 上午数据在第 1 页
        Assert.Equal(WindowStart, firstPage.Sessions[0].Start);

        // 晚间数据在第 3 页 —— 修复前这类数据被静默丢弃
        var lastStart = lastPage.Sessions[^1].Start;
        Assert.True(lastStart > WindowStart.AddHours(20), $"末条会话应在当日较晚时段，实际 {lastStart:O}");
        Assert.True(lastPage.Sessions.Count > 0);
    }

    [Fact]
    public async Task GetTimelineAsync_ClampsOutOfRangePaginationArguments()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 10);

        // pageSize 超过上限 → 夹到上限而不是抛异常；page < 1 → 视为第 1 页。
        // 注意上限内不会有 100_000 条数据，因此这里同时验证「夹紧后仍能正常返回」。
        var oversized = await Service(db).GetTimelineAsync(
            Query(page: 0, pageSize: 100_000), CancellationToken.None);

        Assert.Equal(1, oversized.Page);
        Assert.Equal(MobileTimelinePagination.MaxPageSize, oversized.PageSize);
        Assert.Equal(10, oversized.Sessions.Count);
    }

    [Fact]
    public async Task GetTimelineAsync_PagesMergeBothSourcesIntoSingleOrderedStream()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 两个来源各自 640 条、时间交错：合并流共 1280 条。
        SeedSessions(db, 640);
        SeedFallbackSummaries(db, 640);

        var service = Service(db);
        var page1 = await service.GetTimelineAsync(Query(page: 1, pageSize: 500), CancellationToken.None);
        var page2 = await service.GetTimelineAsync(Query(page: 2, pageSize: 500), CancellationToken.None);
        var page3 = await service.GetTimelineAsync(Query(page: 3, pageSize: 500), CancellationToken.None);

        // 总数是**合并流**的 1280，不是任一来源的 640
        Assert.Equal(1280, page1.TotalCount);
        Assert.Equal(640, page1.SessionTotalCount);
        Assert.Equal(640, page1.FallbackTotalCount);

        Assert.Equal(500, page1.Items.Count);
        Assert.Equal(500, page2.Items.Count);
        Assert.Equal(280, page3.Items.Count);
        Assert.True(page1.HasMore);
        Assert.True(page2.HasMore);
        Assert.False(page3.HasMore);

        // 每页至多 pageSize 条（旧实现两个列表各自 Take，单页最多 2×pageSize）
        Assert.All(new[] { page1, page2, page3 }, page => Assert.True(page.Items.Count <= 500));

        // 逐页拼接后全天有序 —— 旧实现两个列表独立分页会让第 2 页的会话早于第 1 页的汇总
        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).ToList();
        Assert.Equal(1280, all.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        var starts = all.Select(item => item.Start).ToList();
        Assert.Equal(starts.OrderBy(value => value).ToList(), starts);

        // Sessions / FallbackSummaries 是当前页按来源的切分，与 Items 完全一致
        Assert.Equal(page1.Items.Count, page1.Sessions.Count + page1.FallbackSummaries.Count);
        Assert.Equal(
            page1.Items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id),
            page1.Sessions.Concat(page1.FallbackSummaries).OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id));
    }

    [Fact]
    public async Task GetTimelineAsync_PagesFallbackSummariesWithTheirOwnTotal()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedFallbackSummaries(db, 640);

        var service = Service(db);
        var page1 = await service.GetTimelineAsync(Query(page: 1, pageSize: 500), CancellationToken.None);
        var page2 = await service.GetTimelineAsync(Query(page: 2, pageSize: 500), CancellationToken.None);

        Assert.Equal(500, page1.FallbackSummaries.Count);
        Assert.Equal(640, page1.FallbackTotalCount);
        Assert.Equal(640, page1.TotalCount);
        Assert.True(page1.HasMore);

        Assert.Equal(140, page2.FallbackSummaries.Count);
        Assert.Equal(640, page2.FallbackTotalCount);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task GetTimelineAsync_HugePageNumberIsRejectedInsteadOfOverflowing()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 10);

        // page=int.MaxValue 时 (page-1)*pageSize 会溢出 int 变成负数，
        // 在 PostgreSQL 上会因负 OFFSET 直接报错。现在用 long 计算并拒绝超大偏移：
        // 明确抛错（端点映射为 400），既不溢出，也不会静默物化整段历史。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(db).GetTimelineAsync(
            Query(page: int.MaxValue, pageSize: MobileTimelinePagination.MaxPageSize),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetTimelineAsync_PageBeyondEndButWithinReadableOffset_ReturnsEmptyPage()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 10);

        // 偏移在可读上限内但超出总条数：返回空页，仍报告真实总数且不谎报还有更多。
        var response = await Service(db).GetTimelineAsync(Query(page: 99, pageSize: 5), CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal(10, response.TotalCount);
        Assert.False(response.HasMore);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task GetTimelineAsync_DefaultQueryKeepsBackwardCompatibleShape()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedSessions(db, 3);

        // 老客户端仍只传 date/deviceId（无 page/pageSize）→ 行为不变，仍能拿到完整数据
        var response = await Service(db).GetTimelineAsync(
            new MobileTimelineQuery("android-main", WindowStart, WindowEnd),
            CancellationToken.None);

        Assert.Equal(3, response.Sessions.Count);
        Assert.Equal(3, response.Items.Count);
        Assert.Equal(1, response.Page);
        Assert.Equal(MobileTimelinePagination.DefaultPageSize, response.PageSize);
        Assert.Equal(3, response.TotalCount);
        Assert.Equal("2026-09-18", response.Date);
    }

    [Fact]
    public async Task GetTimelineAsync_ExactPageBoundary_DoesNotClaimMoreData()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 恰好整页：最后一页必须报告 hasMore=false，不能永远说「还有下一页」
        SeedSessions(db, 1000);

        var service = Service(db);
        var page1 = await service.GetTimelineAsync(Query(page: 1, pageSize: 500), CancellationToken.None);
        var page2 = await service.GetTimelineAsync(Query(page: 2, pageSize: 500), CancellationToken.None);

        Assert.Equal(500, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.Equal(500, page2.Items.Count);
        Assert.False(page2.HasMore);
        Assert.False(page2.Truncated);
        Assert.Equal(1000, page2.TotalCount);
    }

    [Fact]
    public async Task GetTimelineAsync_IdenticalTimestampsAcrossSources_AreOrderedDeterministically()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 同一时刻既有会话又有汇总：排序必须确定（Start 相同则按 Id），
        // 且分页不得因此丢行或重复。
        for (var i = 0; i < 30; i++)
        {
            var start = WindowStart.AddMinutes(i);
            db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
            {
                Id = Guid.NewGuid(),
                UserId = MobileTestHelpers.UserId,
                DeviceId = "android-main",
                PackageName = "com.example.session",
                StartUtc = start,
                EndUtc = start.AddMinutes(1),
                DurationMs = 60_000,
                CreatedAt = start
            });
            db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
            {
                Id = Guid.NewGuid(),
                UserId = MobileTestHelpers.UserId,
                DeviceId = "android-main",
                PackageName = "com.example.fallback",
                WindowStartUtc = start,
                WindowEndUtc = start.AddMinutes(1),
                TotalTimeVisibleMs = 60_000,
                SourceKind = "usage-stats-fallback",
                CreatedAt = start
            });
        }
        await db.SaveChangesAsync();

        var service = Service(db);

        var first = await service.GetTimelineAsync(Query(page: 1, pageSize: 17), CancellationToken.None);
        var firstAgain = await service.GetTimelineAsync(Query(page: 1, pageSize: 17), CancellationToken.None);

        // 同一请求可复现
        Assert.Equal(
            first.Items.Select(i => i.Id).ToList(),
            firstAgain.Items.Select(i => i.Id).ToList());

        // 逐页取完，共 60 条，无重无漏
        var all = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var response = await service.GetTimelineAsync(Query(page: page, pageSize: 17), CancellationToken.None);
            all.AddRange(response.Items.Select(i => i.Id));
            Assert.Equal(60, response.TotalCount);
        }

        Assert.Equal(60, all.Count);
        Assert.Equal(60, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GetTimelineAsync_RangeWithoutMatchesReportsZeroTotals()
    {
        await using var db = MobileTestHelpers.CreateDb();

        var response = await Service(db).GetTimelineAsync(Query(), CancellationToken.None);

        Assert.Empty(response.Sessions);
        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal(0, response.SessionTotalCount);
        Assert.Equal(0, response.FallbackTotalCount);
        Assert.False(response.HasMore);
        Assert.False(response.Truncated);
    }
}
