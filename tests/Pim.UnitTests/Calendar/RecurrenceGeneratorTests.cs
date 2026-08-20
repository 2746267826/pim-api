using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class RecurrenceGeneratorTests
{
    private static readonly RecurrenceService Service = new(NullLogger<RecurrenceService>.Instance);

    private static EventEntity Master(string rrule, DateTimeOffset dtStart, DateTimeOffset dtEnd, string? exDatesJson = null, Guid? id = null)
    {
        return new EventEntity
        {
            Id = id ?? Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = $"uid-{Guid.NewGuid()}",
            Title = "Master",
            DtStart = dtStart,
            DtEnd = dtEnd,
            RRule = rrule,
            IsSeriesMaster = true,
            ExDatesJson = exDatesJson ?? "[]",
        };
    }

    private static List<ExpandedEvent> Expand(EventEntity entity, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        return Service.ExpandEventsV2(new[] { entity }, rangeStart, rangeEnd);
    }

    [Fact]
    public void Daily_Count_GeneratesExactOccurrences()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var master = Master("FREQ=DAILY;COUNT=5", start, end);
        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        var result = Expand(master, rangeStart, rangeEnd);

        Assert.Equal(5, result.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(start.AddDays(i), result[i].OccurrenceStart);
            Assert.Equal(end.AddDays(i), result[i].OccurrenceEnd);
        }
    }

    [Fact]
    public void Daily_Interval_GeneratesEveryNthDay()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=DAILY;INTERVAL=2;COUNT=3", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
        Assert.Equal(start.AddDays(2), result[1].OccurrenceStart);
        Assert.Equal(start.AddDays(4), result[2].OccurrenceStart);
    }

    [Fact]
    public void Weekly_Count_GeneratesWeeklyOccurrences()
    {
        var start = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero); // Monday
        var master = Master("FREQ=WEEKLY;COUNT=4", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(4, result.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(start.AddDays(7 * i), result[i].OccurrenceStart);
    }

    [Fact]
    public void Weekly_Interval_GeneratesEverySecondWeek()
    {
        var start = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=WEEKLY;INTERVAL=2;COUNT=3", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
        Assert.Equal(start.AddDays(14), result[1].OccurrenceStart);
        Assert.Equal(start.AddDays(28), result[2].OccurrenceStart);
    }

    [Fact]
    public void Monthly_Count_GeneratesMonthlyOccurrences()
    {
        var start = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=MONTHLY;COUNT=3", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2026, 2, 15, 9, 0, 0, TimeSpan.Zero), result[1].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero), result[2].OccurrenceStart);
    }

    [Fact]
    public void Yearly_Count_GeneratesYearlyOccurrences()
    {
        var start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=YEARLY;COUNT=3", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2027, 3, 10, 9, 0, 0, TimeSpan.Zero), result[1].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2028, 3, 10, 9, 0, 0, TimeSpan.Zero), result[2].OccurrenceStart);
    }

    [Fact]
    public void Until_FiltersOccurrencesInclusive()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        // UNTIL Jan 5 09:00 UTC — should include Jan 1..5 inclusive (5 occurrences)
        var master = Master("FREQ=DAILY;UNTIL=20260105T090000Z", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(5, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), result[4].OccurrenceStart);
    }

    [Fact]
    public void Interval_With_Until_RespectsBoth()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        // Every 2 days until Jan 7 => Jan 1,3,5,7 = 4
        var master = Master("FREQ=DAILY;INTERVAL=2;UNTIL=20260107T090000Z", start, start.AddHours(1));
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(4, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 7, 9, 0, 0, TimeSpan.Zero), result[3].OccurrenceStart);
    }

    [Fact]
    public void RangeFilter_ExcludesOutsideWindow()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=DAILY;COUNT=10", start, start.AddHours(1));
        // Range only Jan 3-5
        var result = Expand(master, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        // Ical.Net generates from rangeStart; only occurrences within [rangeStart, rangeEnd) count.
        // From Jan3-5 exclusive => Jan3, Jan4
        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero), result[0].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero), result[1].OccurrenceStart);
    }

    [Fact]
    public void NoRRule_SingleEventWithinRange_ReturnsOne()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "single",
            Title = "Single",
            DtStart = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 2, 1, 11, 0, 0, TimeSpan.Zero),
            RRule = null,
        };
        var result = Expand(entity, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        var single = Assert.Single(result);
        Assert.Equal(entity.DtStart, single.OccurrenceStart);
        Assert.Equal(entity.Id, single.OccurrenceId);
    }

    [Fact]
    public void NoRRule_OutsideRange_ReturnsEmpty()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "single2",
            Title = "Single2",
            DtStart = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 2, 1, 11, 0, 0, TimeSpan.Zero),
            RRule = null,
        };
        var result = Expand(entity, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Empty(result);
    }

    [Fact]
    public void ExDates_ExcludesMatchingOccurrence()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var exDate = start.AddDays(1).ToString("O"); // Jan 2
        var exJson = JsonSerializer.Serialize(new[] { exDate });
        var master = Master("FREQ=DAILY;COUNT=5", start, start.AddHours(1), exJson);
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        // 5 minus 1 excluded = 4
        Assert.Equal(4, result.Count);
        Assert.DoesNotContain(result, r => r.OccurrenceStart == start.AddDays(1));
        Assert.Contains(result, r => r.OccurrenceStart == start);
        Assert.Contains(result, r => r.OccurrenceStart == start.AddDays(2));
    }

    [Fact]
    public void ExDates_MultipleExcludes()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var exJson = JsonSerializer.Serialize(new[] { start.AddDays(1).ToString("O"), start.AddDays(3).ToString("O") });
        var master = Master("FREQ=DAILY;COUNT=5", start, start.AddHours(1), exJson);
        var result = Expand(master, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, r => r.OccurrenceStart == start.AddDays(1));
        Assert.DoesNotContain(result, r => r.OccurrenceStart == start.AddDays(3));
    }

    [Fact]
    public void FarWindow_DailyCount_FilteredCorrectlyFromDtStart()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        // 100 daily occurrences, window far: 30 days after start => should return 5 occurrences in window
        var master = Master("FREQ=DAILY;COUNT=100", start, start.AddHours(1));
        var rangeStart = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero); // day 31
        var rangeEnd = new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero); // 5 days
        var result = Expand(master, rangeStart, rangeEnd);
        // Jan1 +31 days = Feb1, so Feb1-5 => 5 occurrences
        Assert.Equal(5, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), result[0].OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2026, 2, 5, 9, 0, 0, TimeSpan.Zero), result[4].OccurrenceStart);
    }

    [Fact]
    public void FarWindow_WeeklyFarWindow_NoEmptyDueToIncrementsLimit()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        // Weekly without COUNT, window 500 days later — must still emit occurrences
        var master = Master("FREQ=WEEKLY;COUNT=200", start, start.AddHours(1));
        var rangeStart = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var result = Expand(master, rangeStart, rangeEnd);
        // From Jan1 2026 weekly, occurrences in Jan 2027: Jan 7,14,21,28 => 4
        Assert.Equal(4, result.Count);
        Assert.All(result, r => Assert.True(r.OccurrenceStart >= rangeStart && r.OccurrenceStart < rangeEnd));
    }

    [Fact]
    public void FarWindow_BeyondCount_ReturnsEmpty()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=DAILY;COUNT=5", start, start.AddHours(1));
        var rangeStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = Expand(master, rangeStart, rangeEnd);
        Assert.Empty(result);
    }

    [Fact]
    public void UnboundedWindow_InfiniteDaily_CappedToMaxOccurrences()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        // No COUNT/UNTIL => infinite daily
        var master = Master("FREQ=DAILY", start, start.AddHours(1));
        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = DateTimeOffset.MaxValue;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Expand(master, rangeStart, rangeEnd);
        sw.Stop();

        // Should not hang and be capped
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Unbounded expansion took too long: {sw.ElapsedMilliseconds}ms");
        Assert.True(result.Count > 0 && result.Count <= 500, $"Expected capped count <=500, got {result.Count}");
        // With 730-day cap, daily => ~730 but also capped to 500, so expect 500
        Assert.Equal(500, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
    }

    [Fact]
    public void UnboundedWindow_InfiniteDaily_MaxValue_WithMinStart_Capped()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=DAILY", start, start.AddHours(1));
        // Simulate GetEventsPaged with no range => MinValue to MaxValue
        var result = Service.ExpandEventsV2(new[] { master }, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.True(result.Count > 0 && result.Count <= 500);
    }

    [Fact]
    public void VeryFarWindow_CappedTo730Days()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Master("FREQ=DAILY", start, start.AddHours(1));
        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(2000); // >730
        var result = Expand(master, rangeStart, rangeEnd);
        // Capped to 730 days => capped to 500 due to occurrence limit
        Assert.Equal(500, result.Count);
        Assert.True(result.Last().OccurrenceStart < rangeStart.AddDays(730));
    }
}
