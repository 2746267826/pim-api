using Pim.Core.Common;
using Xunit;

namespace Pim.UnitTests.Common;

/// <summary>
/// 业务日口径（EPIC #254 · D-1）：Asia/Shanghai，04:00 起算，左闭右开。
/// </summary>
public sealed class BusinessDayTests
{
    [Theory]
    [InlineData("2026-09-13T19:59:59Z", "2026-09-13")] // 09-14 03:59:59 CST → 仍属 09-13
    [InlineData("2026-09-13T20:00:00Z", "2026-09-14")] // 09-14 04:00:00 CST → 业务日切到 09-14
    [InlineData("2026-09-13T16:00:00Z", "2026-09-13")] // 09-14 00:00 CST → 仍属 09-13
    [InlineData("2026-09-12T20:00:00Z", "2026-09-13")] // 09-13 04:00 CST → 09-13 起点
    [InlineData("2026-09-12T19:59:59Z", "2026-09-12")] // 09-13 03:59:59 CST → 仍属 09-12
    public void GetBusinessDate_UsesShanghaiFourAmBoundary(string instant, string expected)
    {
        var timestamp = DateTimeOffset.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(DateOnly.Parse(expected), BusinessDay.GetBusinessDate(timestamp));
    }

    [Fact]
    public void GetRangeUtc_IsHalfOpenShanghaiFourAmWindow()
    {
        var (start, end) = BusinessDay.GetRangeUtc(new DateOnly(2026, 9, 13));

        Assert.Equal(DateTimeOffset.Parse("2026-09-12T20:00:00Z"), start);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T20:00:00Z"), end);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
    }

    [Fact]
    public void GetStartUtc_IsTheInverseOfGetBusinessDate()
    {
        var date = new DateOnly(2026, 9, 13);

        Assert.Equal(date, BusinessDay.GetBusinessDate(BusinessDay.GetStartUtc(date)));
        Assert.Equal(date, BusinessDay.GetBusinessDate(BusinessDay.GetEndUtc(date).AddTicks(-1)));
        Assert.Equal(date.AddDays(1), BusinessDay.GetBusinessDate(BusinessDay.GetEndUtc(date)));
    }

    [Fact]
    public void ParseAndFormat_RoundTrip()
    {
        Assert.True(BusinessDay.TryParseDate("2026-09-13", out var date));
        Assert.Equal("2026-09-13", BusinessDay.FormatDate(date));

        Assert.False(BusinessDay.TryParseDate("13/09/2026", out _));
        Assert.False(BusinessDay.TryParseDate(null, out _));
        Assert.False(BusinessDay.TryParseDate("", out _));
    }

    [Fact]
    public void TimeZone_ResolvesToUtcPlusEight()
    {
        var offset = BusinessDay.TimeZone.GetUtcOffset(new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(TimeSpan.FromHours(8), offset);
    }
}
