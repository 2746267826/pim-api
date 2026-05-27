using System.Text.Json;
using Ical.Net.CalendarComponents;
using IcalCalendar = Ical.Net.Calendar;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookIcsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OutlookIcsParseResult Parse(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return new OutlookIcsParseResult(Array.Empty<OutlookIcsParsedEvent>());

        IcalCalendar? calendar;
        try
        {
            calendar = IcalCalendar.Load(icsContent);
        }
        catch
        {
            return new OutlookIcsParseResult(Array.Empty<OutlookIcsParsedEvent>(), "parse_error");
        }

        if (calendar?.Events is null)
            return new OutlookIcsParseResult(Array.Empty<OutlookIcsParsedEvent>());

        var rawComponents = ExtractRawEventComponents(icsContent);
        var events = calendar.Events.Select((e, index) =>
        {
            var raw = index < rawComponents.Count ? rawComponents[index] : string.Empty;
            var startUtc = e.Start?.AsUtc;
            var endUtc = e.End?.AsUtc;
            var invalidReason = HasRawDateProperty(raw) && (startUtc is null || endUtc is null)
                ? "parse_error"
                : null;
            var sourceTimeZoneId = GetSourceTimeZoneId(e);
            var recurrenceId = GetRawPropertyValues(raw, "RECURRENCE-ID").FirstOrDefault()
                ?? GetPropertyValue(e, "RECURRENCE-ID");
            var exDates = GetRawPropertyValues(raw, "EXDATE").ToList();
            if (exDates.Count == 0)
                exDates = GetPropertyValues(e, "EXDATE").ToList();
            var recurrenceMetadata = new Dictionary<string, object?>
            {
                ["recurrenceId"] = recurrenceId,
                ["exDates"] = exDates,
                ["sourceFields"] = new[] { "RECURRENCE-ID", "EXDATE", "RRULE" }
            };

            return new OutlookIcsParsedEvent(
                e.Uid ?? Guid.NewGuid().ToString(),
                e.Summary ?? "Untitled",
                e.Description,
                e.Location,
                startUtc is not null ? new DateTimeOffset(startUtc.Value, TimeSpan.Zero) : DateTimeOffset.MinValue,
                endUtc is not null ? new DateTimeOffset(endUtc.Value, TimeSpan.Zero) : DateTimeOffset.MinValue,
#pragma warning disable CS0618
                e.RecurrenceRules.FirstOrDefault()?.ToString(),
#pragma warning restore CS0618
                IsAllDay(e),
                sourceTimeZoneId,
                raw,
                JsonSerializer.Serialize(BuildMetadata(calendar.Method, e, raw), JsonOptions),
                recurrenceId,
                JsonSerializer.Serialize(exDates, JsonOptions),
                JsonSerializer.Serialize(recurrenceMetadata, JsonOptions),
                invalidReason);
        }).ToList();

        return new OutlookIcsParseResult(events);
    }

    private static Dictionary<string, object?> BuildMetadata(string? method, CalendarEvent e, string rawComponent)
    {
        var metadata = new Dictionary<string, object?>();
        var outlookProperties = new Dictionary<string, string?>();
        var attendees = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrWhiteSpace(method))
            metadata["method"] = method;

        foreach (var property in e.Properties)
        {
            var name = property.Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var value = GetRawPropertyValues(rawComponent, name).FirstOrDefault()
                ?? property.Value?.ToString();
            switch (name.ToUpperInvariant())
            {
                case "ORGANIZER":
                    metadata["organizer"] = value;
                    metadata["organizerParameters"] = GetParameters(property);
                    break;
                case "ATTENDEE":
                    attendees.Add(new Dictionary<string, object?>
                    {
                        ["value"] = value,
                        ["parameters"] = GetParameters(property)
                    });
                    break;
                case "SEQUENCE":
                    metadata["sequence"] = int.TryParse(value, out var sequence) ? sequence : value;
                    break;
                case "CLASS":
                    metadata["class"] = value;
                    break;
                case "TRANSP":
                    metadata["transp"] = value;
                    break;
                case "PRIORITY":
                    metadata["priority"] = int.TryParse(value, out var priority) ? priority : value;
                    break;
                case "CATEGORIES":
                    metadata["categories"] = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                    break;
                case "X-ALT-DESC":
                    metadata["htmlDescription"] = value;
                    break;
            }

            if (name.StartsWith("X-MICROSOFT", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-MS-OLK", StringComparison.OrdinalIgnoreCase))
            {
                outlookProperties[name] = value;
            }
        }

        if (attendees.Count > 0)
            metadata["attendees"] = attendees;
        if (outlookProperties.Count > 0)
            metadata["outlookProperties"] = outlookProperties;

        return metadata;
    }

    private static Dictionary<string, string?> GetParameters(Ical.Net.ICalendarProperty property) =>
        property.Parameters.ToDictionary(p => p.Name, p => p.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

    private static bool IsAllDay(CalendarEvent e) =>
        e.Start is not null && !e.Start.HasTime;

    private static string? GetSourceTimeZoneId(CalendarEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.Start?.TzId))
            return e.Start.TzId;

        var startProperty = e.Properties.FirstOrDefault(p => string.Equals(p.Name, "DTSTART", StringComparison.OrdinalIgnoreCase));
        return startProperty?.Parameters.FirstOrDefault(p => string.Equals(p.Name, "TZID", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
    }

    private static string? GetPropertyValue(CalendarEvent e, string name) =>
        e.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString();

    private static bool HasRawDateProperty(string rawComponent) =>
        GetRawPropertyValues(rawComponent, "DTSTART").Any() ||
        GetRawPropertyValues(rawComponent, "DTEND").Any();

    private static IEnumerable<string> GetPropertyValues(CalendarEvent e, string name) =>
        e.Properties
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))!;

    private static IEnumerable<string> GetRawPropertyValues(string rawComponent, string name)
    {
        foreach (var line in UnfoldLines(rawComponent))
        {
            if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.Length > name.Length && line[name.Length] is not ':' and not ';')
                continue;

            var colon = line.IndexOf(':');
            if (colon >= 0 && colon + 1 < line.Length)
                yield return line[(colon + 1)..];
        }
    }

    private static IEnumerable<string> UnfoldLines(string value)
    {
        var lines = value.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var current = string.Empty;
        foreach (var line in lines)
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                current += line[1..];
                continue;
            }

            if (current.Length > 0)
                yield return current;
            current = line;
        }

        if (current.Length > 0)
            yield return current;
    }

    private static List<string> ExtractRawEventComponents(string icsContent)
    {
        var normalized = icsContent.Replace("\r\n", "\n").Replace("\r", "\n");
        var components = new List<string>();
        var start = 0;
        while (true)
        {
            var begin = normalized.IndexOf("BEGIN:VEVENT", start, StringComparison.OrdinalIgnoreCase);
            if (begin < 0)
                break;

            var end = normalized.IndexOf("END:VEVENT", begin, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                break;

            end += "END:VEVENT".Length;
            components.Add(normalized[begin..end].Replace("\n", "\r\n"));
            start = end;
        }

        return components;
    }
}

public sealed record OutlookIcsParseResult(IReadOnlyList<OutlookIcsParsedEvent> Events, string? ErrorReason = null);

public sealed record OutlookIcsParsedEvent(
    string Uid,
    string Title,
    string? Description,
    string? Location,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? RRule,
    bool IsAllDay,
    string? SourceTimeZoneId,
    string SourceIcsComponent,
    string ExternalMetadataJson,
    string? RecurrenceId,
    string ExDatesJson,
    string RecurrenceMetadataJson,
    string? InvalidReason = null
);
