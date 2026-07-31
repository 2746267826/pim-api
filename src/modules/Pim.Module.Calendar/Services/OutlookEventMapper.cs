using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public static class OutlookEventMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> MappedGraphPropertyKeys = new(StringComparer.Ordinal)
    {
        "@odata.etag", "id", "iCalUId", "subject", "body", "location",
        "start", "end", "originalStartTimeZone", "originalEndTimeZone",
        "changeKey", "type", "seriesMasterId", "recurrence", "isAllDay",
        "importance", "sensitivity", "showAs", "categories",
        "isReminderOn", "reminderMinutesBeforeStart",
        "organizer", "attendees",
        "isOnlineMeeting", "onlineMeetingProvider", "onlineMeeting", "onlineMeetingUrl",
        "webLink"
    };

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
        var descriptionFormat = GetDescriptionFormat(graph);
        target.DescriptionFormat = descriptionFormat;
        var bodyContent = GetStringOrNull(graph, "body", "content");
        target.Description = EventDescriptionSanitizer.Normalize(
            descriptionFormat == "html" ? NormalizeHtmlHeadings(bodyContent) : bodyContent,
            descriptionFormat);
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

        target.Importance = GetStringOrNull(graph, "importance");
        target.Sensitivity = GetStringOrNull(graph, "sensitivity");
        target.ShowAs = GetStringOrNull(graph, "showAs");
        target.CategoriesJson = EventFieldCodec.SerializeCategories(GetCategories(graph));

        var isReminderOn = GetBoolOrFalse(graph, "isReminderOn");
        target.IsReminderOn = isReminderOn;
        target.ReminderMinutesBeforeStart = isReminderOn
            ? GetIntOrNull(graph, "reminderMinutesBeforeStart")
            : null;

        target.OrganizerJson = EventFieldCodec.SerializePerson(GetOrganizer(graph));
        target.AttendeesJson = EventFieldCodec.SerializeAttendees(GetAttendees(graph));

        target.IsOnlineMeeting = GetBoolOrFalse(graph, "isOnlineMeeting");
        target.OnlineMeetingProvider = NormalizeOnlineMeetingProvider(GetStringOrNull(graph, "onlineMeetingProvider"));
        target.OnlineMeetingUrl = GetOnlineMeetingUrl(graph);
        target.ExternalLink = GetStringOrNull(graph, "webLink");

        WriteExternalMetadata(target, graph);
    }

    public static Dictionary<string, object?> BuildWritePayload(CreateEventRequest draft, string? transactionId)
    {
        var bodyContentType = string.Equals(draft.DescriptionFormat, "html", StringComparison.OrdinalIgnoreCase)
            ? "html"
            : "text";
        var payload = new Dictionary<string, object?>
        {
            ["subject"] = draft.Title,
            ["body"] = new Dictionary<string, object?>
            {
                ["contentType"] = bodyContentType,
                ["content"] = bodyContentType == "html"
                    ? EventDescriptionSanitizer.NormalizeHtml(draft.Description ?? string.Empty)
                    : draft.Description ?? string.Empty
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

        if (!string.IsNullOrEmpty(draft.Importance))
        {
            payload["importance"] = draft.Importance;
        }

        if (!string.IsNullOrEmpty(draft.Sensitivity))
        {
            payload["sensitivity"] = draft.Sensitivity;
        }

        if (!string.IsNullOrEmpty(draft.ShowAs))
        {
            payload["showAs"] = draft.ShowAs;
        }

        if (draft.Categories is not null)
        {
            payload["categories"] = draft.Categories;
        }

        if (draft.Attendees is not null)
        {
            payload["attendees"] = draft.Attendees
                .Select(attendee => (object)new Dictionary<string, object?>
                {
                    ["emailAddress"] = new Dictionary<string, object?>
                    {
                        ["name"] = attendee.Name,
                        ["address"] = attendee.Email
                    },
                    ["type"] = attendee.Type
                })
                .ToArray();
        }

        if (draft.IsReminderOn is bool isReminderOn)
        {
            payload["isReminderOn"] = isReminderOn;
            if (isReminderOn && draft.ReminderMinutesBeforeStart is int reminderMinutes)
            {
                payload["reminderMinutesBeforeStart"] = reminderMinutes;
            }
        }

        if (draft.IsOnlineMeeting is bool isOnlineMeeting)
        {
            payload["isOnlineMeeting"] = isOnlineMeeting;
            if (isOnlineMeeting
                && string.Equals(draft.OnlineMeetingProvider, "teams", StringComparison.OrdinalIgnoreCase))
            {
                payload["onlineMeetingProvider"] = "teamsForBusiness";
            }
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
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? GetStringOrNull(JsonElement element, string outer, string inner)
    {
        if (element.TryGetProperty(outer, out var outerProp) && outerProp.ValueKind == JsonValueKind.Object)
        {
            return outerProp.TryGetProperty(inner, out var innerProp) && innerProp.ValueKind == JsonValueKind.String
                ? innerProp.GetString()
                : null;
        }
        return null;
    }

    private static bool GetBoolOrFalse(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.True;
    }

    private static int? GetIntOrNull(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? NormalizeHtmlHeadings(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        // The sanitizer drops the content of tags it does not allow; demote h1 to
        // the allowed h2 level so heading text survives sanitization.
        return Regex.Replace(html, @"</?h1(?=[\s>])",
            match => match.Value.StartsWith("</", StringComparison.Ordinal) ? "</h2" : "<h2",
            RegexOptions.IgnoreCase);
    }

    private static string GetDescriptionFormat(JsonElement element)
    {
        var contentType = GetStringOrNull(element, "body", "contentType");
        if (string.Equals(contentType, "text", StringComparison.OrdinalIgnoreCase))
            return "plain";
        return "html";
    }

    private static List<string> GetCategories(JsonElement element)
    {
        var result = new List<string>();
        if (element.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in categories.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                    result.Add(item.GetString()!);
            }
        }
        return result;
    }

    private static EventPersonDto? GetOrganizer(JsonElement element)
    {
        if (!element.TryGetProperty("organizer", out var organizer) || organizer.ValueKind != JsonValueKind.Object)
            return null;

        if (organizer.TryGetProperty("emailAddress", out var emailAddress) && emailAddress.ValueKind == JsonValueKind.Object)
        {
            return new EventPersonDto(
                GetStringOrNull(emailAddress, "name"),
                GetStringOrNull(emailAddress, "address"));
        }

        return null;
    }

    private static List<EventAttendeeDto> GetAttendees(JsonElement element)
    {
        var result = new List<EventAttendeeDto>();
        if (element.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in attendees.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = GetStringOrNull(item, "type") ?? "required";
                var name = GetStringOrNull(item, "emailAddress", "name");
                var address = GetStringOrNull(item, "emailAddress", "address") ?? string.Empty;
                result.Add(new EventAttendeeDto(name, address, type));
            }
        }
        return result;
    }

    private static string? NormalizeOnlineMeetingProvider(string? provider)
    {
        if (string.IsNullOrEmpty(provider))
            return null;
        if (string.Equals(provider, "unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        if (provider.Equals("teamsForBusiness", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("teamsForConsumer", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("skypeForBusiness", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("skypeForConsumer", StringComparison.OrdinalIgnoreCase))
            return "teams";
        return "other";
    }

    private static string? GetOnlineMeetingUrl(JsonElement element)
    {
        if (element.TryGetProperty("onlineMeeting", out var meeting) && meeting.ValueKind == JsonValueKind.Object)
        {
            var joinUrl = GetStringOrNull(meeting, "joinUrl");
            if (!string.IsNullOrEmpty(joinUrl))
                return joinUrl;
        }

        return GetStringOrNull(element, "onlineMeetingUrl");
    }

    private static void WriteExternalMetadata(EventEntity target, JsonElement graph)
    {
        var unmapped = new Dictionary<string, object?>();
        foreach (var property in graph.EnumerateObject())
        {
            if (MappedGraphPropertyKeys.Contains(property.Name))
                continue;
            unmapped[property.Name] = property.Value.Clone();
        }

        var envelope = new Dictionary<string, object?>
        {
            ["mappingVersion"] = 2,
            ["sourceSnapshot"] = new Dictionary<string, object?>
            {
                ["body"] = graph.TryGetProperty("body", out var body) ? body.Clone() : null,
                ["event"] = graph.Clone()
            },
            ["unmapped"] = unmapped
        };

        target.ExternalMetadataJson = JsonSerializer.Serialize(envelope, JsonOptions);
    }

    private static string FormatDateTime(DateTimeOffset dto, bool isAllDay)
    {
        if (isAllDay)
            return dto.Date.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }
}
