using Pim.Client.Core.Utils;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class BusinessDayUtilsTests
{
    [Fact]
    public void GetBusinessDate_BeforeFourAmShanghai_BelongsToPreviousCalendarDay()
    {
        // 2026-03-24 03:59:59 CST (+08:00) is 2026-03-23 19:59:59 UTC
        var ts = new DateTimeOffset(2026, 3, 24, 3, 59, 59, TimeSpan.FromHours(8));
        var businessDate = BusinessDayUtils.GetBusinessDate(ts);

        Assert.Equal(new DateOnly(2026, 3, 23), businessDate);
        Assert.Equal("2026-03-23", BusinessDayUtils.GetBusinessDateString(ts));
    }

    [Fact]
    public void GetBusinessDate_AtOrAfterFourAmShanghai_BelongsToCurrentCalendarDay()
    {
        // 2026-03-24 04:00:00 CST (+08:00) is 2026-03-23 20:00:00 UTC
        var tsAtFour = new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8));
        var businessDateAtFour = BusinessDayUtils.GetBusinessDate(tsAtFour);

        Assert.Equal(new DateOnly(2026, 3, 24), businessDateAtFour);
        Assert.Equal("2026-03-24", BusinessDayUtils.GetBusinessDateString(tsAtFour));

        // 2026-03-24 23:59:59 CST
        var tsLate = new DateTimeOffset(2026, 3, 24, 23, 59, 59, TimeSpan.FromHours(8));
        Assert.Equal(new DateOnly(2026, 3, 24), BusinessDayUtils.GetBusinessDate(tsLate));
    }

    [Fact]
    public void GetNextBusinessDayStart_ReturnsNextFourAmShanghaiInUtc()
    {
        // 2026-03-24 02:00:00 CST (+08:00) -> Next business day start is 2026-03-24 04:00:00 CST (2026-03-23 20:00:00 UTC)
        var ts1 = new DateTimeOffset(2026, 3, 24, 2, 0, 0, TimeSpan.FromHours(8));
        var next1 = BusinessDayUtils.GetNextBusinessDayStart(ts1);
        var expected1 = new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(expected1, next1);

        // 2026-03-24 05:00:00 CST (+08:00) -> Next business day start is 2026-03-25 04:00:00 CST (2026-03-24 20:00:00 UTC)
        var ts2 = new DateTimeOffset(2026, 3, 24, 5, 0, 0, TimeSpan.FromHours(8));
        var next2 = BusinessDayUtils.GetNextBusinessDayStart(ts2);
        var expected2 = new DateTimeOffset(2026, 3, 25, 4, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(expected2, next2);
    }

    [Fact]
    public void PcTrackerService_GetBusinessDayForTimestamp_MatchesBusinessDayUtils()
    {
        var testTimestamps = new[]
        {
            new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 24, 3, 59, 59, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 24, 23, 59, 59, TimeSpan.FromHours(8))
        };

        foreach (var ts in testTimestamps)
        {
            var clientDate = BusinessDayUtils.GetBusinessDate(ts);
            var serverDate = PcTrackerService.GetBusinessDayForTimestamp(ts);
            Assert.Equal(clientDate.ToDateTime(TimeOnly.MinValue), serverDate);
        }
    }
}
