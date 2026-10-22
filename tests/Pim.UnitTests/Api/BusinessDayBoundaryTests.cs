using Microsoft.AspNetCore.Http;
using Pim.Api.Endpoints;
using Pim.Api.Today;
using Pim.Core.Caching;
using Pim.Module.PcTracker;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 业务日兜底与缓存键（review P2-4 / P2-5）：宿主时区是 UTC 时，
/// 上海 04:00–08:00 期间「本地日期」还停在昨天。服务端兜底的默认日期与缓存键都必须按业务日算，
/// 否则跨 04:00 边界会返回 / 继续命中上一业务日的数据。
/// </summary>
public sealed class BusinessDayBoundaryTests
{
    // 2026-10-21 03:59 Asia/Shanghai == 2026-10-20 19:59 UTC
    private static readonly DateTimeOffset BeforeBoundary =
        new(2026, 10, 20, 19, 59, 0, TimeSpan.Zero);

    // 2026-10-21 04:01 Asia/Shanghai == 2026-10-20 20:01 UTC
    private static readonly DateTimeOffset AfterBoundary =
        new(2026, 10, 20, 20, 1, 0, TimeSpan.Zero);

    [Fact]
    public void TodayCacheKey_ChangesAtBusinessDayBoundary()
    {
        // 03:59：日历日是 10-21，但 PC 业务日仍是 10-20。
        Assert.Equal("2026-10-21|2026-10-20", TodaySectionService.ImplicitDateCacheKey(BeforeBoundary));
        // 04:01：两者都翻到 10-21 —— 键必须变化，否则会继续返回 03:59 的快照。
        Assert.Equal("2026-10-21|2026-10-21", TodaySectionService.ImplicitDateCacheKey(AfterBoundary));
    }

    [Fact]
    public void PcDefaultDate_UsesBusinessDay_NotUtcDate()
    {
        // UTC 日期还是 10-20；业务日必须已经翻到 10-21（04:01）。
        Assert.Equal(new DateTime(2026, 10, 20), PcTrackerService.BusinessToday(BeforeBoundary));
        Assert.Equal(new DateTime(2026, 10, 21), PcTrackerService.BusinessToday(AfterBoundary));
    }

    [Fact]
    public void TodayEndpointCacheKey_UsesImplicitBusinessDay()
    {
        // 端点的真实取键函数（不是只有纯函数）：去掉 implicitDate 覆盖后这里会失败（review P3-2）。
        var request = new DefaultHttpContext().Request;
        request.Path = "/api/v1/today/sections";

        var before = TodayEndpoints.BuildCacheKey(request, date: null, BeforeBoundary);
        var after = TodayEndpoints.BuildCacheKey(request, date: null, AfterBoundary);
        Assert.NotEqual(before, after);

        // 显式传 date 时不再叠加 implicitDate，避免同一请求出现两种键。
        Assert.Equal(
            AggregateResultCacheKeys.Build(request),
            TodayEndpoints.BuildCacheKey(request, "2026-10-21", BeforeBoundary));
    }

    private static string KeyOf(IReadOnlyList<KeyValuePair<string, string>> overrides)
        => string.Join("&", overrides.Select(pair => $"{pair.Key}={pair.Value}"));

    [Fact]
    public void QualityCacheKey_DoesNotFoldDateIntoDateFrom()
    {
        // 服务层区间是 [dateFrom ?? date, dateTo ?? date ?? from]：
        // dateFrom=10-01 是「一天」，date=10-10&dateFrom=10-01 是「十天」，不能同键（review P2-1）。
        var today = new DateTime(2026, 10, 21);
        var oneDay = PcTrackerModule.QualityCacheOverrides(
            date: null, dateFrom: new DateTime(2026, 10, 1), dateTo: null, today);
        var tenDays = PcTrackerModule.QualityCacheOverrides(
            date: new DateTime(2026, 10, 10), dateFrom: new DateTime(2026, 10, 1), dateTo: null, today);

        Assert.NotEqual(KeyOf(oneDay), KeyOf(tenDays));
        // 传了任何一个起点，就不再引入「今天」；两个请求的差异只来自原始 date。
        Assert.Contains(oneDay, pair => pair.Key == "implicitDate" && pair.Value == "");
        Assert.Contains(tenDays, pair => pair.Key == "implicitDate" && pair.Value == "");
        Assert.Contains(tenDays, pair => pair.Key == "date" && pair.Value == "2026-10-10");
    }

    [Fact]
    public void QualityCacheKey_UsesTodayOnlyWhenBothStartsMissing()
    {
        var day1 = new DateTime(2026, 10, 20);
        var day2 = new DateTime(2026, 10, 21);

        // 两个起点都缺省：跨 04:00 必须换键（否则会继续命中上一业务日的报告）。
        Assert.NotEqual(
            KeyOf(PcTrackerModule.QualityCacheOverrides(null, null, null, day1)),
            KeyOf(PcTrackerModule.QualityCacheOverrides(null, null, null, day2)));

        // 给了任何一个起点：区间已完全确定，不再受「今天」影响。
        Assert.Equal(
            KeyOf(PcTrackerModule.QualityCacheOverrides(new DateTime(2026, 10, 10), null, null, day1)),
            KeyOf(PcTrackerModule.QualityCacheOverrides(new DateTime(2026, 10, 10), null, null, day2)));
    }
}
