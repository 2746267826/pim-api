using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileLocationQueryServiceTests
{
    [Fact]
    public void Normalize_DefaultsToLastSevenBeijingDays()
    {
        var service = new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z")));

        var context = service.Normalize(new MobileLocationQueryRequest());

        Assert.Equal("Asia/Shanghai", context.Range.Timezone);
        Assert.Equal("2026-07-02", context.Range.LocalStartDate);
        Assert.Equal("2026-07-08", context.Range.LocalEndDate);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T16:00:00Z"), context.Range.RangeStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T16:00:00Z"), context.Range.RangeEndUtc);
        Assert.Equal(50, context.MaxAccuracyMeters);
        Assert.False(context.IncludeRejected);
        Assert.Equal(50, context.PageSize);
    }

    [Fact]
    public void Normalize_ClampsPageSizeAndReordersRange()
    {
        var service = new MobileLocationQueryService(MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-08T04:00:00Z")));

        var context = service.Normalize(new MobileLocationQueryRequest(
            RangeStartUtc: DateTimeOffset.Parse("2026-07-08T16:00:00Z"),
            RangeEndUtc: DateTimeOffset.Parse("2026-07-01T16:00:00Z"),
            MaxAccuracyMeters: -1,
            PageSize: 500));

        Assert.Equal(DateTimeOffset.Parse("2026-07-01T16:00:00Z"), context.Range.RangeStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T16:00:00Z"), context.Range.RangeEndUtc);
        Assert.Equal(200, context.PageSize);
        Assert.Equal(50, context.MaxAccuracyMeters);
    }
}
