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
        Assert.Equal(date.Date.AddHours(4), start.ToLocalTime().DateTime);
    }
}
