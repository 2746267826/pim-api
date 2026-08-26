using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerServiceTests
{
    [Fact]
    public void BusinessDayStart_ReturnsUtcOffsetForPostgresTimestamptzQueries()
    {
        var date = new DateTime(2026, 5, 20);
        var start = PcTrackerService.GetBusinessDayStartForQuery(date);

        Assert.Equal(TimeSpan.Zero, start.Offset);
        // 业务日 04:00 固定为 Asia/Shanghai 时区的 04:00，对应 UTC 2026-05-19 20:00
        var shanghai = ResolveTestTimeZone();
        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 20, 4, 0, 0, DateTimeKind.Unspecified), shanghai);
        var expected = new DateTimeOffset(expectedUtc, TimeSpan.Zero);
        Assert.Equal(expected, start);
    }

    private static TimeZoneInfo ResolveTestTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
