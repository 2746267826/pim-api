using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Pim.Module.Calendar.Entities;
using IcalCalendar = Ical.Net.Calendar;

namespace Pim.Module.Calendar.Services;

public class IcsService
{
    public string ExportEvents(IEnumerable<EventEntity> events)
    {
        var calendar = new IcalCalendar();
        calendar.AddTimeZone(new VTimeZone("Asia/Shanghai"));

        foreach (var evt in events)
        {
            var calEvent = new CalendarEvent
            {
                Uid = evt.Uid,
                Summary = evt.Title,
                Description = evt.Description,
                Location = evt.Location,
                Start = new CalDateTime(evt.DtStart.UtcDateTime),
                End = new CalDateTime(evt.DtEnd.UtcDateTime),
                DtStamp = new CalDateTime(evt.DtStamp.UtcDateTime),
                Status = evt.Status
            };

            if (!string.IsNullOrEmpty(evt.RRule))
#pragma warning disable CS0618
                calEvent.RecurrenceRules.Add(new RecurrencePattern(evt.RRule));
#pragma warning restore CS0618

            calendar.Events.Add(calEvent);
        }

        var serializer = new CalendarSerializer();
        return serializer.SerializeToString(calendar)!;
    }

    public List<ParsedEvent> ImportEvents(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return new List<ParsedEvent>();

        var calendar = IcalCalendar.Load(icsContent);
        if (calendar?.Events is null)
            return new List<ParsedEvent>();

        return calendar.Events.Select(e =>
        {
            var startUtc = e.Start?.AsUtc;
            var endUtc = e.End?.AsUtc;
            return new ParsedEvent(
                e.Uid ?? Guid.NewGuid().ToString(),
                e.Summary ?? "Untitled",
                e.Description,
                e.Location,
                startUtc is not null ? new DateTimeOffset(startUtc.Value, TimeSpan.Zero) : DateTimeOffset.MinValue,
                endUtc is not null ? new DateTimeOffset(endUtc.Value, TimeSpan.Zero) : DateTimeOffset.MinValue,
#pragma warning disable CS0618
                e.RecurrenceRules.FirstOrDefault()?.ToString()
#pragma warning restore CS0618
            );
        }).ToList();
    }
}

public record ParsedEvent(
    string Uid, string Title, string? Description,
    string? Location, DateTimeOffset Start, DateTimeOffset End, string? RRule
);
