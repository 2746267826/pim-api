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
        if (toolName == "get_tasks" && args["status"] is { } status)
        {
            if (status.GetValueKind() != JsonValueKind.String
                || !TaskStatuses.Contains(status.GetValue<string>(), StringComparer.Ordinal))
                return Error("status must be one of NEEDS-ACTION, IN-PROCESS, COMPLETED, CANCELLED (one value only; comma-separated values are not supported)");
        }

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

    private static JsonObject Error(string message) => new() { ["error"] = message, ["code"] = 400 };
}
