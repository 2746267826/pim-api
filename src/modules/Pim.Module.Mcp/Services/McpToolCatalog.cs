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

    /// <summary>
    /// Default template: ordinary reads on, writes off. Native-note reads are off because the
    /// R05 permission model requires an explicit opt-in; a legacy client whose permission map
    /// predates these tools must not gain notes.read merely by upgrading the server.
    /// </summary>
    public static Dictionary<string, Dictionary<string, bool>> DefaultPermissions()
    {
        var read = ReadTools.ToDictionary(t => t.Name, t => t.DefaultEnabled);
        var write = WriteTools.ToDictionary(t => t.Name, _ => false);
        return new Dictionary<string, Dictionary<string, bool>>
        {
            ["read"] = read,
            ["write"] = write,
        };
    }

    /// <summary>True if the tool name is a write tool.</summary>
    public static bool IsWrite(string toolName) => _writeNames.Contains(toolName);

    /// <summary>Default permission used when a client permission map has no key for a tool.</summary>
    public static bool IsEnabledByDefault(string toolName) => _allNames.Contains(toolName) &&
        (ReadTools.FirstOrDefault(t => t.Name == toolName)?.DefaultEnabled ?? false);

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
        Add(tools, "calendar", "get_events", "List events in an explicit ISO 8601 start/end range (maximum 366 days, exact boundary allowed), with stable pagination. Offsets are absolute instants; no timezone argument is accepted.");
        Add(tools, "calendar", "get_tasks", "List tasks with pagination. statuses is an OR array; empty means all, duplicates are ignored. status remains a deprecated single-value alias.");
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

        // PcTracker 26（WO-PC-BACKEND-20261001 REQ-6：get_pc_aw_heatmap 随遗留端点下线）
        Add(tools, "pctracker", "get_pc_summary", "PC activity summary.");
        Add(tools, "pctracker", "get_pc_detail", "PC activity detail.");
        Add(tools, "pctracker", "get_pc_timeline", "PC activity timeline.");
        Add(tools, "pctracker", "get_pc_timeline_v2", "PC activity timeline v2.");
        Add(tools, "pctracker", "get_pc_heatmap", "PC activity heatmap.");
        Add(tools, "pctracker", "get_pc_activity_analysis", "PC activity analysis.");
        Add(tools, "pctracker", "get_pc_quality", "PC data quality.");
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

        // Mobile 19
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
        Add(tools, "mobile", "get_mobile_liveness_summary", "Device liveness summary (read-only).");

        // QuickNotes 3
        Add(tools, "quicknotes", "get_quick_notes", "List quick notes.");
        Add(tools, "quicknotes", "get_quick_note", "Get a quick note by id.");
        Add(tools, "quicknotes", "get_quick_note_attachment_meta", "Quick note attachment metadata.");

        // NativeNotes 8：服务器唯一的原生笔记事实源，按当前 MCP 用户隔离。
        Add(tools, "native-notes", "get_native_notes",
            "List the current user's native notes with recycle-bin status, revision and conflict counts. Never returns another account's notes.");
        Add(tools, "native-notes", "get_native_note",
            "Get one native note aggregate (current version, conflict candidates, processing, derived tasks/results) with a user-visible receipt.");
        Add(tools, "native-notes", "get_native_note_versions",
            "Get the current version and every conflict candidate for one native note, plus the revision required for CAS writes.");
        Add(tools, "native-notes", "get_native_note_results",
            "Get derived processing tasks and result artifacts for one native note, including failure/retry visibility.");
        Add(tools, "native-notes", "get_native_note_processing",
            "Get the high-level processing status for one native note version (or the current version when omitted).");
        Add(tools, "native-notes", "get_native_note_operation",
            "Look up the receipt and logical state of a failed, unknown or succeeded native-note operation by its caller-supplied requestId.");
        Add(tools, "native-notes", "get_native_note_package",
            "Download a native-note material package as bounded base64 ZIP chunks (offset/maxBytes, at most 16 MB per call) up to the 64 MB upload limit. contentSha256/byteLength describe the whole package; chunkSha256/chunkByteLength describe the returned contentBase64 chunk. Follow nextOffset until hasMore=false.");
        Add(tools, "native-notes", "get_native_note_suggestions",
            "List task/calendar suggestions extracted from one native note with their status and revision.");

        // Files 7
        Add(tools, "files", "get_file_providers", "List file providers.");
        Add(tools, "files", "get_files", "List files.");
        Add(tools, "files", "get_file", "Get file metadata.");
        Add(tools, "files", "search_files", "Search files (metadata only).");
        Add(tools, "files", "read_file_text", "Read a text-extractable file's content (docx/pptx/text; transient, not stored).");
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
        AddW(tools, "calendar.events", "create_event", "Create a local calendar event. Outlook-bound calendars require confirmed writeback, which this MCP tool does not expose; canEdit is not event-write capability.");
        AddW(tools, "calendar.events", "update_event", "Update a local event (scope/recurrenceId for series). Outlook-bound events/calendars require confirmed writeback, not supported by this tool.");
        AddW(tools, "calendar.events", "delete_event", "Delete a local event. Outlook-bound events/calendars require confirmed writeback, not supported by this tool.");
        AddW(tools, "calendar.events", "restore_event", "Restore a deleted event from recycle bin.");
        AddW(tools, "calendar.events", "batch_delete_events", "Batch delete local events. Outlook-bound events require confirmed writeback, not supported by this tool.");

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

        // NativeNotes 12：全部写入都要求 requestId，用于幂等重放与 requestId 冲突检测。
        AddW(tools, "native-notes", "upload_native_note_package",
            "Upload a complete native-note material package (base64 ZIP) and optionally promote it to current. Requires requestId; CAS via expectedCurrentVersionId/expectedRevision.");
        AddW(tools, "native-notes", "promote_native_note_version",
            "Explicitly choose a verified native-note material version as current; the previous head and conflict candidates stay on disk.");
        AddW(tools, "native-notes", "cancel_native_note_upload",
            "Cancel an unfinished (Uploading) native-note material upload; completed versions are never removed.");
        AddW(tools, "native-notes", "restore_native_note",
            "Restore a recycled native note to Active; processing stays stopped until the user explicitly continues.");
        AddW(tools, "native-notes", "delete_native_note",
            "Move a native note to the recycle bin; queued and in-flight processing is stopped and materials stay recoverable until restored.");
        AddW(tools, "native-notes", "start_native_note_processing",
            "Start the server-side high-level processing chain for one complete native-note version. Returns an explicit error when AI runtime is unavailable instead of faking success.");
        AddW(tools, "native-notes", "retry_native_note_processing",
            "Retry selected failed native-note processing steps with duplicate-charge acknowledgement and revision CAS.");
        AddW(tools, "native-notes", "adopt_native_note_result",
            "Adopt one derived result version as the artifact head with expected-head-revision CAS.");
        AddW(tools, "native-notes", "extract_native_note_suggestions",
            "Queue task/calendar suggestion extraction from one fixed derived result version with explicit consent.");
        AddW(tools, "native-notes", "confirm_native_note_suggestion",
            "Confirm a draft task/calendar suggestion and create the corresponding task or event; requestId is idempotent.");
        AddW(tools, "native-notes", "dismiss_native_note_suggestion",
            "Dismiss a draft task/calendar suggestion; requestId is idempotent.");

        // Files 5
        AddW(tools, "files", "upload_file", "Upload a file to a provider path.");
        AddW(tools, "files", "move_file", "Move a file to another path.");
        AddW(tools, "files", "rename_file", "Rename a file.");
        AddW(tools, "files", "delete_file", "Delete a file (moves it to the cloud drive recycle bin and soft-deletes it in PIM).");
        AddW(tools, "files", "restore_file", "Restore a PIM-soft-deleted file; only possible while the file still exists in the cloud drive.");

        // PcTracker 分类 4
        AddW(tools, "pctracker.categories", "create_category", "Save an app-pattern classification rule, not a category tree node. Delete its returned id with delete_category(resourceType=appRule).");
        AddW(tools, "pctracker.categories", "update_categories_order", "Reorder category tree nodes, not app-pattern rules.");
        AddW(tools, "pctracker.categories", "delete_category", "Delete a category tree node (resourceType=tree, legacy default) or an app-pattern rule (resourceType=appRule). Never guesses the resource from its id.");
        AddW(tools, "pctracker.categories", "seed_categories", "Seed default PC categories.");

        // Mobile 目标 2
        AddW(tools, "mobile.goals", "create_mobile_goal", "Create a mobile usage goal.");
        AddW(tools, "mobile.goals", "delete_mobile_goal", "Delete a mobile usage goal.");

        return tools;
    }

    private static void AddW(List<McpToolInfo> list, string group, string name, string description)
        => Add(list, group, name, description, isWrite: true);

    private static void Add(List<McpToolInfo> list, string group, string name, string description, bool isWrite)
        => list.Add(new McpToolInfo(name, group, description, isWrite, false));

    private static void Add(List<McpToolInfo> list, string group, string name, string description)
        => list.Add(new McpToolInfo(name, group, description, false,
            !string.Equals(group, "native-notes", StringComparison.Ordinal)));
}
