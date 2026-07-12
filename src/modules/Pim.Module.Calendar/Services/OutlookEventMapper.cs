using System.Globalization;
using System.Text.Json;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public static class OutlookEventMapper
{
    public static void ApplyGraphEvent(
        EventEntity target,
        JsonElement graph,
        Guid bindingId,
        Guid calendarId,
        Guid connectionId,
        Guid generation)
    {
        target.OutlookEventId = graph.GetProperty("id").GetString()!;
        target.Title = graph.GetProperty("subject").GetString() ?? string.Empty;
        target.Description = GetStringOrNull(graph, "body", "content");
        target.Location = GetStringOrNull(graph, "location", "displayName");
        target.OutlookChangeKey = GetStringOrNull(graph, "changeKey");
        target.OutlookEtag = GetStringOrNull(graph, "@odata.etag");
        target.OriginalStartTimeZone = GetStringOrNull(graph, "originalStartTimeZone");
        target.OriginalEndTimeZone = GetStringOrNull(graph, "originalEndTimeZone");
        target.IsAllDay = GetBoolOrFalse(graph, "isAllDay");
        target.OutlookCalendarBindingId = bindingId;
        target.CalendarId = calendarId;
        target.OutlookConnectionId = connectionId;
        target.LastSeenSyncGeneration = generation;
        target.Source = "outlook";
        target.Status = "CONFIRMED";

        var iCalUId = GetStringOrNull(graph, "iCalUId");
        if (!string.IsNullOrEmpty(iCalUId))
        {
            target.Uid = iCalUId;
            target.SourceUid = iCalUId;
        }
        else
        {
            var graphId = graph.GetProperty("id").GetString()!;
            target.Uid = graphId + "@outlook";
            target.SourceUid = graphId + "@outlook";
        }

        var type = GetStringOrNull(graph, "type");
        target.OutlookEventType = type;
        if (type == "seriesMaster")
        {
            target.OutlookSeriesMasterId = null;
        }
        else
        {
            target.OutlookSeriesMasterId = GetStringOrNull(graph, "seriesMasterId");
        }

        if (graph.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind != JsonValueKind.Null)
        {
            target.GraphRecurrenceJson = recurrence.GetRawText();
        }
        else
        {
            target.GraphRecurrenceJson = "{}";
        }

        var start = graph.GetProperty("start");
        var end = graph.GetProperty("end");
        var startRaw = start.GetProperty("dateTime").GetString()!;
        var endRaw = end.GetProperty("dateTime").GetString()!;

        target.DtStart = ParseGraphDateTime(startRaw);
        target.DtEnd = ParseGraphDateTime(endRaw);

        if (target.IsAllDay)
        {
            target.AllDayStartDate = DateOnly.Parse(startRaw[..10], CultureInfo.InvariantCulture);
            target.AllDayEndDateExclusive = DateOnly.Parse(endRaw[..10], CultureInfo.InvariantCulture);
        }
        else
        {
            target.AllDayStartDate = null;
            target.AllDayEndDateExclusive = null;
        }
    }

    public static Dictionary<string, object?> BuildWritePayload(CreateEventRequest draft, string? transactionId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["subject"] = draft.Title,
            ["body"] = new Dictionary<string, object?>
            {
                ["contentType"] = "text",
                ["content"] = draft.Description ?? string.Empty
            },
            ["location"] = new Dictionary<string, object?>
            {
                ["displayName"] = draft.Location ?? string.Empty
            },
            ["start"] = new Dictionary<string, object?>
            {
                ["dateTime"] = FormatDateTime(draft.DtStart, draft.IsAllDay),
                ["timeZone"] = "UTC"
            },
            ["end"] = new Dictionary<string, object?>
            {
                ["dateTime"] = FormatDateTime(draft.DtEnd, draft.IsAllDay),
                ["timeZone"] = "UTC"
            },
            ["isAllDay"] = draft.IsAllDay
        };

        if (!string.IsNullOrEmpty(transactionId))
        {
            payload["transactionId"] = transactionId;
        }

        return payload;
    }

    private static readonly DateTimeStyles ParseStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private static DateTimeOffset ParseGraphDateTime(string raw)
    {
        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, ParseStyles);
    }

    private static string? GetStringOrNull(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? prop.GetString()
            : null;
    }

    private static string? GetStringOrNull(JsonElement element, string outer, string inner)
    {
        if (element.TryGetProperty(outer, out var outerProp) && outerProp.ValueKind != JsonValueKind.Null)
        {
            return outerProp.TryGetProperty(inner, out var innerProp) && innerProp.ValueKind != JsonValueKind.Null
                ? innerProp.GetString()
                : null;
        }
        return null;
    }

    private static bool GetBoolOrFalse(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.True;
    }

    private static string FormatDateTime(DateTimeOffset dto, bool isAllDay)
    {
        if (isAllDay)
            return dto.Date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }
}
