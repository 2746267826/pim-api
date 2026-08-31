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
        "start", "end", "originalStart", "originalStartTimeZone", "originalEndTimeZone",
        "changeKey", "type", "seriesMasterId", "recurrence", "isAllDay", "isCancelled",
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
        var bodyContent = GetStringOrNull(graph, "body", "content");
        target.Description = EventDescriptionSanitizer.Normalize(
            descriptionFormat == "html" ? NormalizeHtmlHeadings(bodyContent) : bodyContent,
            descriptionFormat);
        target.DescriptionFormat = target.Description is null ? null : descriptionFormat;
        target.Location = GetStringOrNull(graph, "location", "displayName");
        target.OutlookChangeKey = GetStringOrNull(graph, "changeKey");
        target.OutlookEtag = GetStringOrNull(graph, "@odata.etag");
        target.OriginalStartTimeZone = GetStringOrNull(graph, "originalStartTimeZone");
        target.OriginalEndTimeZone = GetStringOrNull(graph, "originalEndTimeZone");
        // TimeZoneId/SourceTimeZoneId were previously left null -> clients could not map UTC back to original local time (issue #171)
        // Populate from originalStartTimeZone (preferred) or start.timeZone, truncated to 100 chars.
        {
            var startTz = GetStringOrNull(graph, "start", "timeZone");
            var effectiveTz = !string.IsNullOrWhiteSpace(target.OriginalStartTimeZone)
                ? target.OriginalStartTimeZone
                : startTz;
            if (!string.IsNullOrWhiteSpace(effectiveTz))
            {
                var normalized = effectiveTz!.Length > 100 ? effectiveTz[..100] : effectiveTz;
                target.TimeZoneId = normalized;
                target.SourceTimeZoneId = normalized;
            }
            else
            {
                // Graph omitted timezone (should not happen with Prefer: UTC, but clear stale value on updates)
                target.TimeZoneId = null;
                target.SourceTimeZoneId = null;
            }
        }
        target.IsAllDay = GetBoolOrFalse(graph, "isAllDay");
        target.OutlookCalendarBindingId = bindingId;
        target.CalendarId = calendarId;
        target.OutlookConnectionId = connectionId;
        target.LastSeenSyncGeneration = generation;
        target.Source = "outlook";
        var isCancelled = GetBoolOrFalse(graph, "isCancelled");
        target.Status = isCancelled ? "CANCELLED" : "CONFIRMED";

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

        JsonElement? recurrenceElement = null;
        if (graph.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind != JsonValueKind.Null && recurrence.ValueKind != JsonValueKind.Undefined)
        {
            if (recurrence.ValueKind == JsonValueKind.Object && recurrence.EnumerateObject().Any())
            {
                target.GraphRecurrenceJson = recurrence.GetRawText();
                recurrenceElement = recurrence;
            }
            else if (recurrence.ValueKind == JsonValueKind.Object)
            {
                target.GraphRecurrenceJson = "{}";
            }
            else
            {
                target.GraphRecurrenceJson = recurrence.GetRawText();
                if (recurrence.ValueKind == JsonValueKind.Object)
                    recurrenceElement = recurrence;
            }
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

        // Map Graph recurrence ↔ RRule / master flags (with exception originalStart handling)
        MapRecurrenceToEntity(target, type, recurrenceElement, graph);

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

        var recurrencePayload = BuildGraphRecurrencePayload(draft.RRule, draft.DtStart, draft.IsAllDay);
        if (recurrencePayload is not null)
        {
            payload["recurrence"] = recurrencePayload;
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

    private static void MapRecurrenceToEntity(EventEntity target, string? type, JsonElement? recurrenceElement, JsonElement graph)
    {
        // Reset recurrence-derived flags first; RRule will be set if recurrence present.
        if (type == "seriesMaster")
        {
            if (recurrenceElement.HasValue)
            {
                var rrule = TryConvertGraphRecurrenceToRRule(recurrenceElement.Value, target.DtStart);
                if (!string.IsNullOrEmpty(rrule))
                {
                    target.RRule = rrule;
                    target.IsSeriesMaster = true;
                    target.IsException = false;
                    target.SeriesMasterId = null;
                    target.RecurrenceId = null;
                    target.OutlookEventType = "seriesMaster";
                    target.OutlookSeriesMasterId = null;
                    return;
                }
            }
            // seriesMaster without valid recurrence: clear master flags
            target.RRule = null;
            target.IsSeriesMaster = false;
            target.IsException = false;
            target.SeriesMasterId = null;
            target.RecurrenceId = null;
        }
        else if (type == "exception")
        {
            target.IsException = true;
            target.IsSeriesMaster = false;
            target.RRule = null;
            // Resolve RecurrenceId from originalStart if not already set
            if (string.IsNullOrEmpty(target.RecurrenceId))
            {
                string? originalStartRaw = null;
                if (graph.TryGetProperty("originalStart", out var originalStartEl) && originalStartEl.ValueKind == JsonValueKind.Object)
                {
                    originalStartRaw = GetStringOrNull(originalStartEl, "dateTime");
                    if (originalStartRaw is null && originalStartEl.TryGetProperty("dateTime", out var dtProp) && dtProp.ValueKind == JsonValueKind.String)
                        originalStartRaw = dtProp.GetString();
                }
                if (!string.IsNullOrEmpty(originalStartRaw))
                {
                    try
                    {
                        var parsed = DateTimeOffset.Parse(originalStartRaw!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                        target.RecurrenceId = parsed.ToString("O");
                    }
                    catch { }
                }
                // fallback: use current DtStart as occurrence start (already UTC)
                if (string.IsNullOrEmpty(target.RecurrenceId))
                {
                    target.RecurrenceId = target.DtStart.ToString("O");
                }
            }
            else
            {
                // normalize existing RecurrenceId to O format
                if (DateTimeOffset.TryParse(target.RecurrenceId, out var existing))
                    target.RecurrenceId = existing.ToString("O");
            }
            // SeriesMasterId GUID cannot be resolved in mapper (needs DB), keep OutlookSeriesMasterId string.
        }
        else if (type == "occurrence")
        {
            target.IsException = false;
            target.IsSeriesMaster = false;
            target.RRule = null;
        }
        else // singleInstance or unknown
        {
            target.IsException = false;
            target.IsSeriesMaster = false;
            target.RRule = null;
            target.SeriesMasterId = null;
            target.RecurrenceId = null;
        }
    }

    private static readonly Dictionary<string, string> GraphDayToByDay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sunday"] = "SU", ["monday"] = "MO", ["tuesday"] = "TU", ["wednesday"] = "WE",
        ["thursday"] = "TH", ["friday"] = "FR", ["saturday"] = "SA"
    };
    private static readonly Dictionary<string, string> ByDayToGraphDay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SU"] = "sunday", ["MO"] = "monday", ["TU"] = "tuesday", ["WE"] = "wednesday",
        ["TH"] = "thursday", ["FR"] = "friday", ["SA"] = "saturday"
    };

    private static string? TryConvertGraphRecurrenceToRRule(JsonElement recurrence, DateTimeOffset dtStart)
    {
        try
        {
            if (!recurrence.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object)
                return null;
            if (!recurrence.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object)
                return null;

            var patternType = GetStringOrNull(pattern, "type");
            if (string.IsNullOrEmpty(patternType))
                return null;
            var freq = MapGraphPatternTypeToFreq(patternType);
            if (freq is null)
                return null;

            var interval = GetIntOrNull(pattern, "interval") ?? 1;
            var rangeType = GetStringOrNull(range, "type") ?? "noEnd";

            var rrule = $"FREQ={freq}";
            if (interval > 1)
                rrule += $";INTERVAL={interval}";

            if (string.Equals(freq, "WEEKLY", StringComparison.OrdinalIgnoreCase))
            {
                if (pattern.TryGetProperty("daysOfWeek", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
                {
                    var byDays = new List<string>();
                    foreach (var d in daysEl.EnumerateArray())
                    {
                        if (d.ValueKind == JsonValueKind.String && GraphDayToByDay.TryGetValue(d.GetString()!, out var by))
                            byDays.Add(by);
                    }
                    if (byDays.Count > 0)
                        rrule += $";BYDAY={string.Join(",", byDays)}";
                }
            }

            if (string.Equals(rangeType, "numbered", StringComparison.OrdinalIgnoreCase))
            {
                var count = GetIntOrNull(range, "numberOfOccurrences");
                if (count.HasValue && count.Value > 0)
                    rrule += $";COUNT={count.Value}";
            }
            else if (string.Equals(rangeType, "endDate", StringComparison.OrdinalIgnoreCase))
            {
                var endDateStr = GetStringOrNull(range, "endDate");
                if (!string.IsNullOrEmpty(endDateStr) && DateOnly.TryParse(endDateStr, CultureInfo.InvariantCulture, out var endDate))
                {
                    // Use dtStart time component for UNTIL, in UTC.
                    var utc = dtStart.ToUniversalTime();
                    var until = new DateTimeOffset(endDate.Year, endDate.Month, endDate.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
                    // If time is 00:00, Graph endDate is date-only inclusive; use end of day 23:59:59? Keep as start time.
                    rrule += $";UNTIL={until.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
                }
            }

            return rrule;
        }
        catch
        {
            return null;
        }
    }

    private static string? MapGraphPatternTypeToFreq(string patternType)
    {
        return patternType.ToLowerInvariant() switch
        {
            "daily" => "DAILY",
            "weekly" => "WEEKLY",
            "absolutemonthly" => "MONTHLY",
            "relativemonthly" => "MONTHLY",
            "monthly" => "MONTHLY",
            "absoluteyearly" => "YEARLY",
            "relativeyearly" => "YEARLY",
            "yearly" => "YEARLY",
            _ => null
        };
    }

    private static Dictionary<string, object?>? BuildGraphRecurrencePayload(string? rrule, DateTimeOffset dtStart, bool isAllDay)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            return null;

        var map = ParseRRule(rrule);
        if (!map.TryGetValue("FREQ", out var freqRaw) || string.IsNullOrEmpty(freqRaw))
            return null;
        var freqUpper = freqRaw!.ToUpperInvariant();
        string graphType = freqUpper switch
        {
            "DAILY" => "daily",
            "WEEKLY" => "weekly",
            "MONTHLY" => "absoluteMonthly",
            "YEARLY" => "absoluteYearly",
            _ => "daily"
        };
        int interval = 1;
        if (map.TryGetValue("INTERVAL", out var intervalRaw) && int.TryParse(intervalRaw, out var iv) && iv > 0)
            interval = iv;

        var pattern = new Dictionary<string, object?>
        {
            ["type"] = graphType,
            ["interval"] = interval
        };
        if (string.Equals(freqUpper, "WEEKLY", StringComparison.OrdinalIgnoreCase) && map.TryGetValue("BYDAY", out var byDayRaw) && !string.IsNullOrWhiteSpace(byDayRaw))
        {
            var days = byDayRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim().ToUpperInvariant())
                .Where(s => ByDayToGraphDay.ContainsKey(s))
                .Select(s => ByDayToGraphDay[s])
                .ToArray();
            if (days.Length > 0)
                pattern["daysOfWeek"] = days;
        }

        var startDate = dtStart.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Dictionary<string, object?> range;
        if (map.TryGetValue("COUNT", out var countRaw) && int.TryParse(countRaw, out var count) && count > 0)
        {
            range = new Dictionary<string, object?>
            {
                ["type"] = "numbered",
                ["startDate"] = startDate,
                ["numberOfOccurrences"] = count,
                ["recurrenceTimeZone"] = "UTC"
            };
        }
        else if (map.TryGetValue("UNTIL", out var untilRaw) && !string.IsNullOrEmpty(untilRaw))
        {
            var endDate = ParseUntilToDateString(untilRaw!);
            range = new Dictionary<string, object?>
            {
                ["type"] = "endDate",
                ["startDate"] = startDate,
                ["endDate"] = endDate,
                ["recurrenceTimeZone"] = "UTC"
            };
        }
        else
        {
            range = new Dictionary<string, object?>
            {
                ["type"] = "noEnd",
                ["startDate"] = startDate,
                ["recurrenceTimeZone"] = "UTC"
            };
        }

        return new Dictionary<string, object?>
        {
            ["pattern"] = pattern,
            ["range"] = range
        };
    }

    private static Dictionary<string, string> ParseRRule(string rrule)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                dict[kv[0].Trim().ToUpperInvariant()] = kv[1].Trim();
        }
        return dict;
    }

    private static string ParseUntilToDateString(string untilRaw)
    {
        // UNTIL formats: 20261215T090000Z or 20261215
        try
        {
            if (untilRaw.Length >= 8)
            {
                var datePart = untilRaw.Substring(0, 8);
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (DateTimeOffset.TryParse(untilRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch { }
        return DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
