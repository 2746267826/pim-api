using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Module.Mcp.Services;

public static class McpToolInputPolicy
{
    private static readonly string[] TaskStatuses = ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

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
