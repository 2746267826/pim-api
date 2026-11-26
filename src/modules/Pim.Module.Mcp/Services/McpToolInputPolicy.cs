using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Module.Mcp.Services;

public static class McpToolInputPolicy
{
    private static readonly string[] TaskStatuses = ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];
    private static readonly string[] UnsupportedEventTimezoneArgs = ["timezone", "timeZone", "timeZoneId"];

    /// <summary>
    /// R01 removed snake_case aliases. They have no compatibility window: any call
    /// that still uses one is rejected before path/query/JSON/multipart mapping and
    /// told the canonical camelCase name. The MCP contract no longer advertises them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RemovedLegacyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["report_id"] = "reportId",
            ["task_id"] = "taskId",
            ["event_id"] = "eventId",
            ["habit_id"] = "habitId",
            ["calendar_id"] = "calendarId",
            ["note_id"] = "noteId",
            ["attachment_id"] = "attachmentId",
            ["file_id"] = "fileId",
            ["from_time"] = "fromTime",
        };

    /// <summary>
    /// Rejects removed snake_case aliases instead of normalizing them. This runs on the
    /// raw argument names so a null-valued legacy key cannot slip through as "absent".
    /// </summary>
    public static JsonObject? RejectLegacyAliases(IEnumerable<string>? argumentNames)
    {
        if (argumentNames is null)
            return null;

        foreach (var name in argumentNames)
        {
            if (RemovedLegacyAliases.TryGetValue(name, out var canonical))
                return Error($"{name} is no longer supported; use {canonical} instead");
        }

        return null;
    }

    public static JsonObject? Apply(string toolName, JsonObject args)
    {
        if (toolName == "get_tasks" && ValidateTaskStatusFilter(args) is { } statusError)
            return statusError;

        if (toolName is "get_events" or "get_tasks")
        {
            foreach (var name in new[] { "page", "pageSize" })
            {
                if (args[name] is not { } value)
                    args[name] = name == "page" ? 1 : 20;
                else if (value.GetValueKind() != JsonValueKind.Number || !value.AsValue().TryGetValue<int>(out _))
                    return Error($"{name} must be an integer");
            }
        }

        if (toolName == "get_events")
        {
            foreach (var name in UnsupportedEventTimezoneArgs)
            {
                if (args.ContainsKey(name))
                    return Error("get_events does not accept a timezone argument; encode the absolute instant with an ISO 8601 offset in start/end (for example 2026-09-01T08:00:00+08:00)");
            }

            if (args["start"] is null || args["end"] is null)
                return Error("get_events requires start and end (ISO 8601, maximum 366 days); an unbounded history query is not supported");

            DateTimeOffset startInstant = default;
            DateTimeOffset endInstant = default;
            foreach (var name in new[] { "start", "end" })
            {
                var value = args[name]!;
                if (value.GetValueKind() != JsonValueKind.String)
                    return Error($"{name} must be an ISO 8601 date/time");
                if (!McpTimeRange.TryParse(value.GetValue<string>(), out var parsed, out var parseError))
                    return Error($"{name} must be an ISO 8601 date/time: {parseError}");
                if (name == "start")
                    startInstant = parsed;
                else
                    endInstant = parsed;
            }

            if (startInstant > endInstant)
                return Error("invalid time range: start must be <= end");
            if (endInstant - startInstant > McpTimeRange.MaxSpan)
                return Error("time range too large: max span 366 days");

            args["start"] = McpTimeRange.ToUtcQueryValue(startInstant);
            args["end"] = McpTimeRange.ToUtcQueryValue(endInstant);
        }

        if (toolName == "search_calendar_events"
            && NormalizeSearchEventWindow(args, DateTimeOffset.UtcNow) is { } searchWindowError)
            return searchWindowError;

        if (toolName is "create_event" or "update_event"
            && NormalizeEventWriteArgs(args) is { } eventWriteError)
            return eventWriteError;

        if (toolName == "get_today_sections" && args.ContainsKey("sectionId"))
            return Error("get_today_sections lists section metadata; use get_today_section(sectionId=...) to read one section");

        if (toolName == "delete_category" && args["resourceType"] is { } resource)
        {
            if (resource.GetValueKind() != JsonValueKind.String || resource.GetValue<string>() is not ("tree" or "appRule"))
                return Error("resourceType must be tree or appRule; use appRule for an id returned by create_category");
        }

        return null;
    }

    private static JsonObject? NormalizeSearchEventWindow(JsonObject args, DateTimeOffset now)
    {
        DateTimeOffset? Parse(string name, out JsonObject? error)
        {
            error = null;
            if (args[name] is not { } node)
                return null;
            if (node.GetValueKind() != JsonValueKind.String)
            {
                error = Error($"{name} must be an ISO 8601 date/time");
                return null;
            }
            if (!McpTimeRange.TryParse(node.GetValue<string>(), out var parsed, out var parseError))
            {
                error = Error($"{name} must be an ISO 8601 date/time: {parseError}");
                return null;
            }
            return parsed;
        }

        var start = Parse("start", out var startError);
        if (startError is not null) return startError;
        var end = Parse("end", out var endError);
        if (endError is not null) return endError;

        if (start is null && end is null)
        {
            var window = McpTimeRange.CenteredWindow(now);
            start = window.Start;
            end = window.End;
        }
        else if (start is not null && end is null)
        {
            end = start.Value.Add(McpTimeRange.MaxSpan);
        }
        else if (start is null && end is not null)
        {
            start = end.Value.Subtract(McpTimeRange.MaxSpan);
        }

        if (start!.Value > end!.Value)
            return Error("invalid time range: start must be <= end");
        if (end.Value - start.Value > McpTimeRange.MaxSpan)
            return Error("time range too large: max span 366 days");

        args["start"] = McpTimeRange.ToUtcQueryValue(start.Value);
        args["end"] = McpTimeRange.ToUtcQueryValue(end.Value);
        return null;
    }

    private static JsonObject? NormalizeEventWriteArgs(JsonObject args)
    {
        foreach (var name in new[] { "dtStart", "dtEnd" })
        {
            if (args[name] is not { } node)
                return Error($"{name} is required as an ISO 8601 string with an explicit UTC offset");
            if (node.GetValueKind() != JsonValueKind.String)
                return Error($"{name} must be an ISO 8601 string with an explicit UTC offset");

            var raw = node.GetValue<string>();
            if (!McpTimeRange.HasExplicitOffset(raw))
            {
                return Error(
                    $"{name} must include an explicit UTC offset or Z (for example 2026-09-01T09:00:00Z or 2026-09-01T17:00:00+08:00); "
                    + "MCP does not guess the server or device timezone for writes");
            }

            if (!McpTimeRange.TryParse(raw, out var parsed, out var parseError))
                return Error($"{name} must be an ISO 8601 date/time: {parseError}");

            args[name] = McpTimeRange.ToUtcQueryValue(parsed);
        }

        if (args["timeZoneId"] is { } zoneNode)
        {
            if (zoneNode.GetValueKind() == JsonValueKind.Null)
                return null;

            if (zoneNode.GetValueKind() != JsonValueKind.String)
                return Error("timeZoneId must be a valid IANA time zone string");

            var zone = zoneNode.GetValue<string>().Trim();
            if (zone.Length == 0 || zone.Length > 100 || !IsIanaTimeZone(zone))
            {
                return Error(
                    "timeZoneId must be a valid IANA time zone (for example Asia/Shanghai); "
                    + "omit it when the source zone is unknown");
            }

            args["timeZoneId"] = zone;
        }

        return null;
    }

    private static bool IsIanaTimeZone(string value)
    {
        if (value is "UTC" or "Etc/UTC" or "GMT")
            return true;
        if (!value.Contains('/', StringComparison.Ordinal))
            return false;

        return TimeZoneInfo.TryFindSystemTimeZoneById(value, out _);
    }

    /// <summary>
    /// R04：MCP <c>get_tasks</c> 对外提供 <c>statuses</c> 字符串数组（OR），同时保留
    /// <c>status</c> 单值参数兼容旧调用。两者同时出现即整单 400；空数组合法且表示
    /// 不筛选；重复值去重；空元素、未知值、大小写不符或带前后空格一律整单 400。
    /// </summary>
    private static JsonObject? ValidateTaskStatusFilter(JsonObject args)
    {
        var hasStatus = args.TryGetPropertyValue("status", out var statusNode) && statusNode is not null;
        var hasStatuses = args.TryGetPropertyValue("statuses", out var statusesNode) && statusesNode is not null;

        if (hasStatus && hasStatuses)
            return Error("status and statuses cannot both be provided; use statuses for multiple values or status for one value");

        if (hasStatus)
        {
            if (statusNode!.GetValueKind() != JsonValueKind.String
                || !TaskStatuses.Contains(statusNode.GetValue<string>(), StringComparer.Ordinal))
            {
                return Error(
                    "status must be one of NEEDS-ACTION, IN-PROCESS, COMPLETED, CANCELLED; "
                    + "for multiple statuses use the statuses array");
            }

            return null;
        }

        if (!hasStatuses)
            return null;

        if (statusesNode!.GetValueKind() != JsonValueKind.Array)
            return Error("statuses must be an array of supported task statuses");

        var values = new List<string>();
        foreach (var item in statusesNode.AsArray())
        {
            if (item is null || item.GetValueKind() != JsonValueKind.String)
                return Error("statuses must contain only supported task status strings");

            var value = item.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)
                || !TaskStatuses.Contains(value, StringComparer.Ordinal))
            {
                return Error(
                    "statuses must contain only NEEDS-ACTION, IN-PROCESS, COMPLETED, CANCELLED; "
                    + "blank, unknown, lowercase, or padded values are rejected");
            }

            values.Add(value);
        }

        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != values.Count)
            args["statuses"] = new JsonArray(distinct.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

        return null;
    }

    private static JsonObject Error(string message) => new() { ["error"] = message, ["code"] = 400 };
}
