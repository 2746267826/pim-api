using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Pim.Core.Exceptions;
using Pim.Module.Calendar.Entities;
using IcalCalendar = Ical.Net.Calendar;

namespace Pim.Module.Calendar.Services;

public class IcsService
{
    public string ExportEvents(IEnumerable<EventEntity> events)
    {
        var calendar = new IcalCalendar();
        var timeZoneIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            if (evt.IsAllDay
                && (!evt.AllDayStartDate.HasValue || !evt.AllDayEndDateExclusive.HasValue
                    || evt.AllDayEndDateExclusive <= evt.AllDayStartDate))
            {
                throw new DomainException(
                    02009,
                    $"全天事件缺少有效的独立日期范围，无法导出：UID={evt.Uid}, ID={evt.Id}。");
            }

            var isAllDay = evt.IsAllDay;
            CalDateTime start;
            CalDateTime end;
            if (isAllDay)
            {
                start = new CalDateTime(evt.AllDayStartDate!.Value);
                end = new CalDateTime(evt.AllDayEndDateExclusive!.Value);
            }
            else
            {
                var resolved = EventTimeZoneResolver.Resolve(
                    evt.TimeZoneId,
                    evt.SourceTimeZoneId,
                    evt.OriginalStartTimeZone);
                if (resolved.IsResolved && resolved.Id is not null && resolved.Id != "UTC")
                {
                    start = new CalDateTime(
                        TimeZoneInfo.ConvertTime(evt.DtStart, resolved.Zone!).DateTime,
                        resolved.Id);
                    end = new CalDateTime(
                        TimeZoneInfo.ConvertTime(evt.DtEnd, resolved.Zone!).DateTime,
                        resolved.Id);
                    timeZoneIds.Add(resolved.Id);
                }
                else
                {
                    start = new CalDateTime(evt.DtStart.UtcDateTime);
                    end = new CalDateTime(evt.DtEnd.UtcDateTime);
                }
            }

            var calEvent = new CalendarEvent
            {
                Uid = evt.Uid,
                Summary = evt.Title,
                Description = evt.Description,
                Location = evt.Location,
                Start = start,
                End = end,
                DtStamp = new CalDateTime(evt.DtStamp.UtcDateTime),
                Status = evt.Status
            };

            if (!string.IsNullOrEmpty(evt.RRule))
#pragma warning disable CS0618
                calEvent.RecurrenceRules.Add(new RecurrencePattern(evt.RRule));
#pragma warning restore CS0618

            calendar.Events.Add(calEvent);
        }

        foreach (var timeZoneId in timeZoneIds.OrderBy(id => id, StringComparer.Ordinal))
            calendar.AddTimeZone(new VTimeZone(timeZoneId));

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
