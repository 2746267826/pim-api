using System.Security.Cryptography;
using System.Text;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class RecurrenceService
{
    private readonly ILogger<RecurrenceService> _logger;

    public RecurrenceService(ILogger<RecurrenceService> logger)
    {
        _logger = logger;
    }

    public List<ExpandedEvent> ExpandEvents(
        IEnumerable<EventEntity> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var results = new List<ExpandedEvent>();
        int recurring = 0, simple = 0, errors = 0;
        foreach (var entity in events)
        {
            if (string.IsNullOrEmpty(entity.RRule))
            {
                simple++;
                if (entity.DtEnd > rangeStart && entity.DtStart < rangeEnd)
                    results.Add(new ExpandedEvent(entity, entity.Id, entity.DtStart, entity.DtEnd));
            }
            else
            {
                recurring++;
                var expanded = ExpandRecurring(entity, rangeStart, rangeEnd);
                if (expanded.Count == 0 && entity.RRule is not null)
                    errors++;
                results.AddRange(expanded);
            }
        }
        _logger.LogInformation("[Recurrence] {EventCount} events: {Recurring} recurring, {Simple} simple, {Results} results, {Errors} failed expansions (range: {Start} to {End})",
            events.Count(), recurring, simple, results.Count, errors, rangeStart, rangeEnd);
        return results;
    }

    private static List<ExpandedEvent> ExpandRecurring(
        EventEntity entity,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var results = new List<ExpandedEvent>();
        var duration = entity.DtEnd - entity.DtStart;

        try
        {
            var calEvent = new CalendarEvent
            {
                DtStart = new CalDateTime(entity.DtStart.UtcDateTime),
                DtEnd = new CalDateTime(entity.DtEnd.UtcDateTime),
            };
#pragma warning disable CS0618
            calEvent.RecurrenceRules.Add(new RecurrencePattern(entity.RRule));
#pragma warning restore CS0618

            var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 500 };
            var rangeStartDt = new CalDateTime(rangeStart.UtcDateTime);

            var occurrences = calEvent.GetOccurrences(rangeStartDt, options);

            foreach (var occ in occurrences)
            {
                var start = new DateTimeOffset(occ.Period.StartTime.Value, TimeSpan.Zero);
                if (start >= rangeEnd)
                    break;

                var end = new DateTimeOffset(occ.Period.EndTime?.Value ?? start.Add(duration).UtcDateTime, TimeSpan.Zero);
                results.Add(new ExpandedEvent(
                    entity,
                    DeriveOccurrenceId(entity.Id, start),
                    start, end));
            }
        }
        catch
        {
            if (entity.DtEnd > rangeStart && entity.DtStart < rangeEnd)
                results.Add(new ExpandedEvent(entity, entity.Id, entity.DtStart, entity.DtEnd));
        }

        return results;
    }

    private static Guid DeriveOccurrenceId(Guid eventId, DateTimeOffset occurrenceStart)
    {
        var input = $"{eventId:D}|{occurrenceStart:yyyyMMddTHHmmssZ}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}

public class ExpandedEvent
{
    public EventEntity Entity { get; }
    public Guid OccurrenceId { get; }
    public DateTimeOffset OccurrenceStart { get; }
    public DateTimeOffset OccurrenceEnd { get; }

    public ExpandedEvent(EventEntity entity, Guid occurrenceId, DateTimeOffset occurrenceStart, DateTimeOffset occurrenceEnd)
    {
        Entity = entity;
        OccurrenceId = occurrenceId;
        OccurrenceStart = occurrenceStart;
        OccurrenceEnd = occurrenceEnd;
    }
}
