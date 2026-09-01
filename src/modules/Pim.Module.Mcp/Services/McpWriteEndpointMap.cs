using System.Text.RegularExpressions;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Maps each write tool to the REST endpoint(s) it is allowed to call. Used by the
/// scoped-JWT middleware so a token issued by /verify for one tool cannot be reused
/// against unrelated REST endpoints (e.g. turning an allowed read into an unauthorized write).
/// </summary>
public static class McpWriteEndpointMap
{
    private sealed record Endpoint(string Method, Regex PathPattern);

    private static readonly Dictionary<string, Endpoint> Map = BuildMap();

    private static Dictionary<string, Endpoint> BuildMap()
    {
        var map = new Dictionary<string, Endpoint>(StringComparer.Ordinal);

        // Calendar events
        Add(map, "create_event", "POST", "/api/v1/calendar/events");
        Add(map, "update_event", "PUT", "/api/v1/calendar/events/{id}");
        Add(map, "delete_event", "DELETE", "/api/v1/calendar/events/{id}");
        Add(map, "restore_event", "POST", "/api/v1/calendar/events/{id}/restore");
        Add(map, "batch_delete_events", "POST", "/api/v1/calendar/events/batch-delete");

        // Calendar tasks
        Add(map, "create_task", "POST", "/api/v1/calendar/tasks");
        Add(map, "update_task", "PUT", "/api/v1/calendar/tasks/{id}");
        Add(map, "delete_task", "DELETE", "/api/v1/calendar/tasks/{id}");
        Add(map, "restore_task", "POST", "/api/v1/calendar/tasks/{id}/restore");
        Add(map, "move_task", "POST", "/api/v1/calendar/tasks/{id}/move");
        Add(map, "plan_task", "POST", "/api/v1/calendar/tasks/{id}/plan");
        Add(map, "create_task_segment", "POST", "/api/v1/calendar/tasks/{id}/segments");
        Add(map, "delete_task_segment", "DELETE", "/api/v1/calendar/tasks/{taskId}/segments/{segmentId}");
        Add(map, "add_task_checklist_item", "POST", "/api/v1/calendar/tasks/{id}/checklist");
        Add(map, "batch_delete_tasks", "POST", "/api/v1/calendar/tasks/batch-delete");
        Add(map, "batch_update_tasks", "POST", "/api/v1/calendar/tasks/batch-update");
        Add(map, "create_task_book", "POST", "/api/v1/calendar/task-books");
        Add(map, "create_project", "POST", "/api/v1/calendar/projects");
        Add(map, "schedule_tasks", "POST", "/api/v1/calendar/schedule");

        // Calendar reminders
        Add(map, "create_reminder", "POST", "/api/v1/calendar/reminders");
        Add(map, "snooze_reminder", "POST", "/api/v1/calendar/reminders/{id}/snooze");
        Add(map, "dismiss_reminder", "POST", "/api/v1/calendar/reminders/{id}/dismiss");

        // Calendar habits
        Add(map, "create_habit", "POST", "/api/v1/calendar/habits");
        Add(map, "create_habit_occurrence", "POST", "/api/v1/calendar/habits/{id}/occurrences");

        // Calendar availability / calendars / import
        Add(map, "create_availability_window", "POST", "/api/v1/calendar/availability");
        Add(map, "import_ics", "POST", "/api/v1/calendar/import-ics");
        Add(map, "create_calendar", "POST", "/api/v1/calendar/calendars");
        Add(map, "update_calendar", "PUT", "/api/v1/calendar/calendars/{id}");
        Add(map, "delete_calendar", "DELETE", "/api/v1/calendar/calendars/{id}");
        Add(map, "restore_calendar", "POST", "/api/v1/calendar/calendars/{id}/restore");

        // QuickNotes
        Add(map, "create_quick_note", "POST", "/api/v1/quick-notes");
        Add(map, "update_quick_note", "PUT", "/api/v1/quick-notes/{id}");
        Add(map, "delete_quick_note", "DELETE", "/api/v1/quick-notes/{id}");
        Add(map, "archive_quick_note", "POST", "/api/v1/quick-notes/{id}/archive");
        Add(map, "restore_quick_note", "POST", "/api/v1/quick-notes/{id}/restore");
        Add(map, "process_quick_note", "POST", "/api/v1/quick-notes/{id}/process");
        Add(map, "upload_quick_note_attachment", "POST", "/api/v1/quick-notes/attachments");
        Add(map, "delete_quick_note_attachment", "DELETE", "/api/v1/quick-notes/attachments/{id}");

        // Files
        Add(map, "upload_file", "POST", "/api/v1/files/items/upload");
        Add(map, "move_file", "POST", "/api/v1/files/items/{id}/move");
        Add(map, "rename_file", "POST", "/api/v1/files/items/{id}/rename");
        Add(map, "delete_file", "DELETE", "/api/v1/files/items/{id}");
        Add(map, "restore_file", "POST", "/api/v1/files/trash/{id}/restore");
        Add(map, "index_file", "POST", "/api/v1/files/items/{id}/index");

        // PcTracker categories
        Add(map, "create_category", "POST", "/api/v1/pc/categories");
        Add(map, "update_categories_order", "PUT", "/api/v1/pc/categories/reorder");
        Add(map, "delete_category", "DELETE", "/api/v1/pc/categories/{id}");
        Add(map, "seed_categories", "POST", "/api/v1/pc/categories/seed");

        // Mobile goals
        Add(map, "create_mobile_goal", "POST", "/api/v1/mobile/analytics/goals");
        Add(map, "delete_mobile_goal", "DELETE", "/api/v1/mobile/analytics/goals/{goalId}");

        return map;
    }

    private static void Add(Dictionary<string, Endpoint> map, string tool, string method, string pathTemplate)
    {
        // {param} segments become [^/]+; literal parts are escaped.
        var parts = Regex.Split(pathTemplate, @"\{[^}]*\}");
        var regex = "^";
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                regex += "[^/]+";
            regex += Regex.Escape(parts[i]);
        }
        regex += "$";
        map[tool] = new Endpoint(method, new Regex(regex, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    /// <summary>True if a (method, path) request targets any write endpoint.</summary>
    public static bool IsWriteEndpoint(string method, string path)
    {
        foreach (var endpoint in Map.Values)
        {
            if (string.Equals(endpoint.Method, method, StringComparison.OrdinalIgnoreCase)
                && endpoint.PathPattern.IsMatch(path))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the request (method, path) is exactly the write tool's allowed endpoint.</summary>
    public static bool IsAllowedForTool(string tool, string method, string path)
    {
        if (!Map.TryGetValue(tool, out var endpoint))
            return false;
        return string.Equals(endpoint.Method, method, StringComparison.OrdinalIgnoreCase)
            && endpoint.PathPattern.IsMatch(path);
    }
}
