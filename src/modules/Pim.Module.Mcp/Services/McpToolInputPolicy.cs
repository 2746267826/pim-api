using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Module.Mcp.Services;

public static class McpToolInputPolicy
{
    private static readonly string[] TaskStatuses = ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

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
