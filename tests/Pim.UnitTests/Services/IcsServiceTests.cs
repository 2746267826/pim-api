using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class IcsServiceTests
{
    private readonly IcsService _service = new();

    [Fact]
    public void ExportEvents_SingleEvent_ProducesValidIcs()
    {
        var evt = new EventEntity
        {
            Uid = "test-uid-1@example.com",
            Title = "Test Event",
            Description = "A test event",
            Location = "Room 1",
            DtStart = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero),
            DtStamp = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            Status = "CONFIRMED"
        };

        var ics = _service.ExportEvents(new[] { evt });

        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("UID:test-uid-1@example.com", ics);
        Assert.Contains("SUMMARY:Test Event", ics);
        Assert.Contains("DESCRIPTION:A test event", ics);
        Assert.Contains("LOCATION:Room 1", ics);
        Assert.Contains("STATUS:CONFIRMED", ics);
        Assert.Contains("END:VEVENT", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public void ImportEvents_ValidIcsContent_ParsesCorrectly()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//PIM//Calendar//EN
        BEGIN:VEVENT
        UID:test@example.com
        SUMMARY:My Event
        DESCRIPTION:Description here
        LOCATION:Office
        STATUS:CONFIRMED
        DTSTART:20260515T090000Z
        DTEND:20260515T100000Z
        END:VEVENT
        END:VCALENDAR
        """;

        var events = _service.ImportEvents(ics);

        Assert.Single(events);
        Assert.Equal("test@example.com", events[0].Uid);
        Assert.Equal("My Event", events[0].Title);
        Assert.Equal("Description here", events[0].Description);
        Assert.Equal("Office", events[0].Location);
    }

    [Fact]
    public void ExportThenImport_RoundTrip_PreservesEventData()
    {
        var original = new EventEntity
        {
            Uid = "roundtrip@example.com",
            Title = "Round Trip Event",
            Description = "Testing round trip",
            Location = "Desk",
            DtStart = new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 6, 1, 15, 0, 0, TimeSpan.Zero),
            DtStamp = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            Status = "TENTATIVE"
        };

        var ics = _service.ExportEvents(new[] { original });
        var parsed = _service.ImportEvents(ics);

        Assert.Single(parsed);
        Assert.Equal(original.Uid, parsed[0].Uid);
        Assert.Equal(original.Title, parsed[0].Title);
        Assert.Equal(original.Description, parsed[0].Description);
        Assert.Equal(original.Location, parsed[0].Location);
    }

    [Fact]
    public void ExportEvents_EmptyList_ProducesEmptyCalendar()
    {
        var ics = _service.ExportEvents(Array.Empty<EventEntity>());

        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("END:VCALENDAR", ics);
        Assert.DoesNotContain("BEGIN:VEVENT", ics);
    }

    [Fact]
    public void ImportEvents_EmptyContent_ReturnsEmptyList()
    {
        var events = _service.ImportEvents(string.Empty);

        Assert.Empty(events);
    }

    [Fact]
    public void ExportEvents_WithRRule_IncludesRecurrenceRule()
    {
        var evt = new EventEntity
        {
            Uid = "recurring@example.com",
            Title = "Weekly Meeting",
            DtStart = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero),
            DtStamp = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            RRule = "FREQ=WEEKLY;BYDAY=MO"
        };

        var ics = _service.ExportEvents(new[] { evt });

        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO", ics);
    }
}
