using Pim.Api.Today;
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
}
