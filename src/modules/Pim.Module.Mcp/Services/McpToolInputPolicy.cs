using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Module.Mcp.Services;

public static class McpToolInputPolicy
{
    private static readonly string[] TaskStatuses = ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

    /// <summary>
    /// Legacy tool-specific aliases kept for backward compatibility. The embedded
    /// contract advertises the camelCase name as required and marks the snake_case
    /// name deprecated; normalization happens before validation so legacy-only calls
    /// still execute.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Canonical, string Legacy)> ParameterAliases =
        new Dictionary<string, (string Canonical, string Legacy)>(StringComparer.Ordinal)
        {
            ["get_report"] = ("reportId", "report_id"),
            ["get_task_segments"] = ("taskId", "task_id"),
            ["get_event_by_id"] = ("eventId", "event_id"),
            ["get_task_by_id"] = ("taskId", "task_id"),
            ["get_habit_occurrences"] = ("habitId", "habit_id"),
            ["get_calendar_by_id"] = ("calendarId", "calendar_id"),
            ["get_task_checklist"] = ("taskId", "task_id"),
            ["get_quick_note"] = ("noteId", "note_id"),
            ["get_quick_note_attachment_meta"] = ("attachmentId", "attachment_id"),
            ["get_file"] = ("fileId", "file_id"),
            ["read_file_text"] = ("fileId", "file_id"),
            ["get_file_open_link"] = ("fileId", "file_id"),
            ["get_ai_requests"] = ("fromTime", "from_time"),
            ["get_ai_usage_summary"] = ("fromTime", "from_time"),
        };

    /// <summary>
    /// Collapses a legacy snake_case argument into its canonical camelCase name.
    /// Equal duplicate values are accepted; conflicting values are rejected instead
    /// of silently preferring one spelling.
    /// </summary>
    public static JsonObject? NormalizeAliases(string toolName, JsonObject args)
    {
        if (!ParameterAliases.TryGetValue(toolName, out var alias))
            return null;

        args.TryGetPropertyValue(alias.Legacy, out var legacyNode);
        if (legacyNode is null)
        {
            args.Remove(alias.Legacy);
            return null;
        }

        if (!args.TryGetPropertyValue(alias.Canonical, out var canonicalNode) || canonicalNode is null)
        {
            args.Remove(alias.Legacy);
            args[alias.Canonical] = legacyNode.DeepClone();
            return null;
        }

        if (!JsonNode.DeepEquals(canonicalNode, legacyNode))
        {
            return Error(
                $"{alias.Canonical} and {alias.Legacy} cannot both be provided with different values");
        }

        args.Remove(alias.Legacy);
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
            if (args["start"] is null || args["end"] is null)
                return Error("get_events requires start and end (ISO 8601, maximum 366 days); an unbounded history query is not supported");
            foreach (var name in new[] { "start", "end" })
            {
                if (args[name] is not { } value)
                    continue;
                if (value.GetValueKind() != JsonValueKind.String
                    || !DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out _))
                    return Error($"{name} must be an ISO 8601 date/time");
            }
        }

        if (toolName == "get_today_sections" && args.ContainsKey("sectionId"))
            return Error("get_today_sections lists section metadata; use get_today_section(sectionId=...) to read one section");

        if (toolName == "delete_category" && args["resourceType"] is { } resource)
        {
            if (resource.GetValueKind() != JsonValueKind.String || resource.GetValue<string>() is not ("tree" or "appRule"))
                return Error("resourceType must be tree or appRule; use appRule for an id returned by create_category");
        }

        return null;
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
