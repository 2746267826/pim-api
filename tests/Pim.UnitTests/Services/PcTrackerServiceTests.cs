using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerServiceTests
{
    [Fact]
    public void BusinessDayStart_ReturnsUtcOffsetForPostgresTimestamptzQueries()
    {
        var start = PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 5, 20));

        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 20, 0, 0, TimeSpan.Zero), start);
    }
}
