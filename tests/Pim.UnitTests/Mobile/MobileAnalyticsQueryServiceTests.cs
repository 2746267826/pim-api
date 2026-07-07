using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileAnalyticsQueryServiceTests
{
    [Fact]
    public void Normalize_DefaultsToLastSevenBeijingDays()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        var service = new MobileAnalyticsQueryService(MobileTestHelpers.Time(now));

        var query = service.Normalize(new MobileAnalyticsQueryRequest());

        Assert.Equal("Asia/Shanghai", query.Range.Timezone);
        Assert.Equal("2026-07-02", query.Range.LocalStartDate);
        Assert.Equal("2026-07-08", query.Range.LocalEndDate);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T16:00:00Z"), query.Range.RangeStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T16:00:00Z"), query.Range.RangeEndUtc);
        Assert.False(query.IncludeSystemNoise);
        Assert.Equal(1, query.MinDurationSeconds);
        Assert.Equal(50, query.PageSize);
    }

    [Fact]
    public void Normalize_ClampsPageSizeAndKeepsFilters()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        var service = new MobileAnalyticsQueryService(MobileTestHelpers.Time(now));

        var query = service.Normalize(new MobileAnalyticsQueryRequest(
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            "Asia/Shanghai",
            "phone-main",
            MobileLifeCategories.Social,
            "com.tencent.mobileqq",
            "events",
            true,
            0,
            "15m",
            "cursor",
            999));

        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), query.Range.RangeStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T00:00:00Z"), query.Range.RangeEndUtc);
        Assert.True(query.IncludeSystemNoise);
        Assert.Equal(0, query.MinDurationSeconds);
        Assert.Equal(200, query.PageSize);
        Assert.Equal("15m", query.Granularity);
        Assert.Equal("cursor", query.Cursor);
        Assert.Equal("phone-main", query.DeviceId);
        Assert.Equal("社交沟通", query.LifeCategory);
    }
}
