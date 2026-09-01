using Pim.Module.Mcp.DTOs;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Catalog of MCP tools exposed by the PIM MCP server. This is the single source of
/// truth for the WebUI permission editor and for default permission templates.
/// Names must stay in sync with <c>scripts/mcp/pim_mcp_server.py</c> tool functions.
/// </summary>
public static class McpToolCatalog
{
    public static IReadOnlyList<McpToolInfo> ReadTools { get; } = BuildRead();

    public static IReadOnlyList<McpToolInfo> WriteTools { get; } = BuildWrite();

    /// <summary>Default template: read all on, write all off (safe baseline).</summary>
    public static Dictionary<string, Dictionary<string, bool>> DefaultPermissions()
    {
        var read = ReadTools.ToDictionary(t => t.Name, _ => true);
        var write = WriteTools.ToDictionary(t => t.Name, _ => false);
        return new Dictionary<string, Dictionary<string, bool>>
        {
            ["read"] = read,
            ["write"] = write,
        };
    }

    /// <summary>True if the tool name is a write tool.</summary>
    public static bool IsWrite(string toolName) => _writeNames.Contains(toolName);

    /// <summary>True if the tool name exists in the catalog.</summary>
    public static bool Contains(string toolName) => _allNames.Contains(toolName);

    private static readonly HashSet<string> _writeNames;
    private static readonly HashSet<string> _allNames;

    static McpToolCatalog()
    {
        _writeNames = WriteTools.Select(t => t.Name).ToHashSet();
        _allNames = ReadTools.Concat(WriteTools).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static List<McpToolInfo> BuildRead()
    {
        var tools = new List<McpToolInfo>();

        // Calendar 31
        Add(tools, "calendar", "get_calendar_layers", "Calendar layers for today/week overview.");
        Add(tools, "calendar", "query_data_center", "Universal data-center query.");
        Add(tools, "calendar", "preview_data_center_batch", "Preview a batch operation.");
        Add(tools, "calendar", "get_data_center_audit_export", "Export data-center audit log.");
        Add(tools, "calendar", "preview_data_center_restore", "Preview a restore operation.");
        Add(tools, "calendar", "get_projects", "List domain projects.");
        Add(tools, "calendar", "get_task_books", "List task books.");
        Add(tools, "calendar", "get_habits", "List habits.");
        Add(tools, "calendar", "get_availability_windows", "List availability windows.");
        Add(tools, "calendar", "get_reminders", "List reminders.");
        Add(tools, "calendar", "get_reminder_delivery_log", "Reminder delivery log.");
        Add(tools, "calendar", "get_reports", "List reports.");
        Add(tools, "calendar", "get_report", "Get a report by id.");
        Add(tools, "calendar", "get_calendars", "List calendars.");
        Add(tools, "calendar", "get_events", "List events in range.");
        Add(tools, "calendar", "get_tasks", "List tasks.");
        Add(tools, "calendar", "get_task_segments", "Task execution segments.");
        Add(tools, "calendar", "get_recycle_bin", "Recycle bin items.");
        Add(tools, "calendar", "preview_recycle_bin_restore", "Preview recycle-bin restore.");
        Add(tools, "calendar", "get_export_ics", "Export calendar ICS.");
        Add(tools, "calendar", "get_outlook_settings", "Outlook sync settings.");
        Add(tools, "calendar", "get_outlook_sync_batches", "Outlook sync batches.");
        Add(tools, "calendar", "get_outlook_local_data_preview", "Outlook local data preview.");
        Add(tools, "calendar", "get_event_by_id", "Get event by id.");
        Add(tools, "calendar", "get_task_by_id", "Get task by id.");
        Add(tools, "calendar", "get_habit_occurrences", "Habit occurrences.");
        Add(tools, "calendar", "get_schedule_preview", "Schedule preview.");
        Add(tools, "calendar", "get_calendar_by_id", "Get calendar by id.");
        Add(tools, "calendar", "get_task_checklist", "Task checklist items.");
        Add(tools, "calendar", "search_calendar_events", "Search events.");
        Add(tools, "calendar", "search_calendar_tasks", "Search tasks.");

        // PcTracker 27
        Add(tools, "pctracker", "get_pc_summary", "PC activity summary.");
        Add(tools, "pctracker", "get_pc_detail", "PC activity detail.");
        Add(tools, "pctracker", "get_pc_timeline", "PC activity timeline.");
        Add(tools, "pctracker", "get_pc_timeline_v2", "PC activity timeline v2.");
        Add(tools, "pctracker", "get_pc_heatmap", "PC activity heatmap.");
        Add(tools, "pctracker", "get_pc_activity_analysis", "PC activity analysis.");
        Add(tools, "pctracker", "get_pc_quality", "PC data quality.");
        Add(tools, "pctracker", "get_pc_aw_heatmap", "PC AW heatmap.");
        Add(tools, "pctracker", "get_pc_keystats_range", "PC keystats in range.");
        Add(tools, "pctracker", "get_pc_focus_blocks", "PC focus blocks.");
        Add(tools, "pctracker", "get_pc_app_usage", "PC app usage.");
        Add(tools, "pctracker", "get_pc_late_night", "PC late-night usage.");
        Add(tools, "pctracker", "get_pc_category_distribution", "PC category distribution.");
        Add(tools, "pctracker", "get_pc_categories", "PC categories.");
        Add(tools, "pctracker", "get_pc_category_tree", "PC category tree.");
        Add(tools, "pctracker", "get_pc_category_dictionary", "PC category dictionary.");
        Add(tools, "pctracker", "get_pc_productivity_dashboard", "PC productivity dashboard.");
        Add(tools, "pctracker", "get_pc_productivity_range", "PC productivity range.");
        Add(tools, "pctracker", "get_classification_rules", "Classification rules.");
        Add(tools, "pctracker", "get_classification_suggestions", "Classification suggestions.");
        Add(tools, "pctracker", "get_classification_queue", "Classification queue.");
        Add(tools, "pctracker", "get_classification_project_tags_recent", "Recent project tags.");
        Add(tools, "pctracker", "get_app_knowledge_apps", "App knowledge apps.");
        Add(tools, "pctracker", "get_app_knowledge_contexts", "App knowledge contexts.");
        Add(tools, "pctracker", "get_app_signatures", "App signatures.");
        Add(tools, "pctracker", "lookup_app_signature", "Lookup app signature.");
        Add(tools, "pctracker", "get_classification_settings", "Classification settings.");

        // Mobile 18
        Add(tools, "mobile", "get_mobile_summary", "Mobile usage summary.");
        Add(tools, "mobile", "get_mobile_timeline", "Mobile timeline.");
        Add(tools, "mobile", "get_mobile_location_history", "Mobile location history.");
        Add(tools, "mobile", "get_mobile_location_latest", "Mobile latest location.");
        Add(tools, "mobile", "get_mobile_location_tracks", "Mobile location tracks.");
        Add(tools, "mobile", "get_mobile_location_overview", "Mobile location overview.");
        Add(tools, "mobile", "get_mobile_location_frequent_places", "Frequent places.");
        Add(tools, "mobile", "get_mobile_location_movement_stats", "Movement stats.");
        Add(tools, "mobile", "get_mobile_quality", "Mobile data quality.");
        Add(tools, "mobile", "get_mobile_analytics_overview", "Mobile analytics overview.");
        Add(tools, "mobile", "get_mobile_analytics_heatmap", "Mobile analytics heatmap.");
        Add(tools, "mobile", "get_mobile_analytics_charts", "Mobile analytics charts.");
        Add(tools, "mobile", "get_mobile_timeline_blocks", "Mobile timeline blocks.");
        Add(tools, "mobile", "get_mobile_devices", "Mobile devices.");
        Add(tools, "mobile", "get_mobile_devices_manage", "Mobile device management view.");
        Add(tools, "mobile", "get_mobile_apps_catalog_overrides", "Mobile catalog overrides.");
        Add(tools, "mobile", "get_mobile_apps_category_rules", "Mobile category rules.");
        Add(tools, "mobile", "get_mobile_goals", "Mobile usage goals.");

        // QuickNotes 3
        Add(tools, "quicknotes", "get_quick_notes", "List quick notes.");
        Add(tools, "quicknotes", "get_quick_note", "Get a quick note by id.");
        Add(tools, "quicknotes", "get_quick_note_attachment_meta", "Quick note attachment metadata.");

        // Files 8
        Add(tools, "files", "get_file_providers", "List file providers.");
        Add(tools, "files", "get_files", "List files.");
        Add(tools, "files", "get_file", "Get file metadata.");
        Add(tools, "files", "get_file_versions", "File versions.");
        Add(tools, "files", "get_file_trash", "File trash.");
        Add(tools, "files", "search_files", "Search files.");
        Add(tools, "files", "get_file_suggestions", "File suggestions.");
        Add(tools, "files", "get_file_open_link", "Build file open link.");

        // Core/Infra 14
        Add(tools, "core", "get_today_sections", "Today dashboard sections.");
        Add(tools, "core", "get_today_section", "Today section detail.");
        Add(tools, "core", "search_pim", "Unified PIM search.");
        Add(tools, "core", "get_system_status", "System status.");
        Add(tools, "core", "get_system_health", "System health.");
        Add(tools, "core", "get_status_summary", "Status summary.");
        Add(tools, "core", "get_ai_status", "AI status.");
        Add(tools, "core", "get_ai_requests", "AI request logs.");
        Add(tools, "core", "get_ai_usage_summary", "AI usage summary.");
        Add(tools, "core", "get_audit_timeline", "Audit timeline.");
        Add(tools, "core", "get_audit_export", "Audit export.");
        Add(tools, "core", "get_confirmations_pending", "Pending confirmations.");
        Add(tools, "core", "get_endpoints", "Registered endpoints.");
        Add(tools, "core", "get_version", "API version info.");

        return tools;
    }

    private static List<McpToolInfo> BuildWrite()
    {
        var tools = new List<McpToolInfo>();

        // Calendar 事件 5
        AddW(tools, "calendar.events", "create_event", "Create a calendar event.");
        AddW(tools, "calendar.events", "update_event", "Update a calendar event (scope/recurrenceId for series).");
        AddW(tools, "calendar.events", "delete_event", "Delete a calendar event.");
        AddW(tools, "calendar.events", "restore_event", "Restore a deleted event from recycle bin.");
        AddW(tools, "calendar.events", "batch_delete_events", "Batch delete events.");

        // Calendar 任务 14
        AddW(tools, "calendar.tasks", "create_task", "Create a task.");
        AddW(tools, "calendar.tasks", "update_task", "Update a task (status/priority/due...).");
        AddW(tools, "calendar.tasks", "delete_task", "Delete a task.");
        AddW(tools, "calendar.tasks", "restore_task", "Restore a deleted task from recycle bin.");
        AddW(tools, "calendar.tasks", "move_task", "Move a task (project/order).");
        AddW(tools, "calendar.tasks", "plan_task", "Schedule a task on the calendar.");
        AddW(tools, "calendar.tasks", "create_task_segment", "Add an execution segment to a task.");
        AddW(tools, "calendar.tasks", "delete_task_segment", "Delete a task execution segment.");
        AddW(tools, "calendar.tasks", "add_task_checklist_item", "Add a checklist item to a task.");
        AddW(tools, "calendar.tasks", "batch_delete_tasks", "Batch delete tasks.");
        AddW(tools, "calendar.tasks", "batch_update_tasks", "Batch update tasks.");
        AddW(tools, "calendar.tasks", "create_task_book", "Create a task book.");
        AddW(tools, "calendar.tasks", "create_project", "Create a domain project.");
        AddW(tools, "calendar.tasks", "schedule_tasks", "Run the scheduling engine for tasks.");

        // Calendar 提醒 3
        AddW(tools, "calendar.reminders", "create_reminder", "Create a reminder.");
        AddW(tools, "calendar.reminders", "snooze_reminder", "Snooze a reminder.");
        AddW(tools, "calendar.reminders", "dismiss_reminder", "Dismiss a reminder.");

        // Calendar 习惯 2
        AddW(tools, "calendar.habits", "create_habit", "Create a habit routine.");
        AddW(tools, "calendar.habits", "create_habit_occurrence", "Log a habit occurrence.");

        // Calendar 日历/导入 6
        AddW(tools, "calendar.calendars", "create_availability_window", "Create an availability window.");
        AddW(tools, "calendar.calendars", "import_ics", "Import an ICS calendar file.");
        AddW(tools, "calendar.calendars", "create_calendar", "Create a calendar.");
        AddW(tools, "calendar.calendars", "update_calendar", "Update a calendar.");
        AddW(tools, "calendar.calendars", "delete_calendar", "Delete a calendar.");
        AddW(tools, "calendar.calendars", "restore_calendar", "Restore a deleted calendar.");

        // QuickNotes 8
        AddW(tools, "quicknotes", "create_quick_note", "Create a quick note.");
        AddW(tools, "quicknotes", "update_quick_note", "Update a quick note.");
        AddW(tools, "quicknotes", "delete_quick_note", "Delete a quick note.");
        AddW(tools, "quicknotes", "archive_quick_note", "Archive a quick note.");
        AddW(tools, "quicknotes", "restore_quick_note", "Restore a quick note.");
        AddW(tools, "quicknotes", "process_quick_note", "Process a quick note (AI/rules).");
        AddW(tools, "quicknotes", "upload_quick_note_attachment", "Upload a quick note attachment.");
        AddW(tools, "quicknotes", "delete_quick_note_attachment", "Delete a quick note attachment.");

        // Files 6
        AddW(tools, "files", "upload_file", "Upload a file to a provider path.");
        AddW(tools, "files", "move_file", "Move a file to another path.");
        AddW(tools, "files", "rename_file", "Rename a file.");
        AddW(tools, "files", "delete_file", "Delete a file (to trash).");
        AddW(tools, "files", "restore_file", "Restore a file from trash.");
        AddW(tools, "files", "index_file", "Trigger file indexing (RAG).");

        // PcTracker 分类 4
        AddW(tools, "pctracker.categories", "create_category", "Create a PC activity category.");
        AddW(tools, "pctracker.categories", "update_categories_order", "Reorder PC categories.");
        AddW(tools, "pctracker.categories", "delete_category", "Delete a PC category.");
        AddW(tools, "pctracker.categories", "seed_categories", "Seed default PC categories.");

        // Mobile 目标 2
        AddW(tools, "mobile.goals", "create_mobile_goal", "Create a mobile usage goal.");
        AddW(tools, "mobile.goals", "delete_mobile_goal", "Delete a mobile usage goal.");

        return tools;
    }

    private static void AddW(List<McpToolInfo> list, string group, string name, string description)
        => Add(list, group, name, description, isWrite: true);

    private static void Add(List<McpToolInfo> list, string group, string name, string description, bool isWrite)
        => list.Add(new McpToolInfo(name, group, description, isWrite));

    private static void Add(List<McpToolInfo> list, string group, string name, string description)
        => Add(list, group, name, description, false);
}
