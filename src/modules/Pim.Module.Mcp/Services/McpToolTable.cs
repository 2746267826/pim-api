namespace Pim.Module.Mcp.Services;

/// <summary>
/// Special execution behaviors ported 1:1 from the retired Python reference
/// (<c>scripts/mcp/pim_mcp_server.py</c>). Tools without a special kind follow the
/// plain "validate -> verify -> dispatch -> post-process" path.
/// </summary>
public enum McpToolKind
{
    /// <summary>Standard read/write tool: validate params, dispatch, post-process.</summary>
    None,

    /// <summary>GET /health — token optional (anonymous fallback).</summary>
    Health,

    /// <summary>GET /api/version — token optional (anonymous fallback).</summary>
    Version,

    /// <summary>HEAD then GET attachment download endpoint, return metadata only.</summary>
    AttachmentMeta,

    /// <summary>GET event by id with data-center + 730-day scan fallback.</summary>
    EventById,

    /// <summary>GET task by id with data-center + list-scan fallback.</summary>
    TaskById,

    /// <summary>GET habit occurrences with data-center query fallback.</summary>
    HabitOccurrences,

    /// <summary>GET task checklist with embedded-checklist fallback.</summary>
    TaskChecklist,

    /// <summary>GET calendars and filter by id locally.</summary>
    CalendarById,

    /// <summary>GET location history and return the latest point.</summary>
    MobileLocationLatest,

    /// <summary>GET file open-link — urls always hashed.</summary>
    FileOpenLink,

    /// <summary>Search events (q -> search query param) with /search fallback on 400.</summary>
    SearchEvents,

    /// <summary>Search tasks (q -> search query param) with /search fallback on 400.</summary>
    SearchTasks,

    /// <summary>Schedule preview — body taskIds defaults to [] when omitted.</summary>
    SchedulePreview,

    /// <summary>ICS export — ids falls back to calendarId; calendarId sent only when ids absent.</summary>
    ExportIcs,
}

/// <summary>
/// HTTP mapping for one of the 151 MCP tools. This table is the in-process twin of the
/// Python tool bodies: it decides which arguments go to the query string, which go to the
/// JSON body, which are path segments, and which extra behaviors apply.
/// </summary>
public sealed record McpToolSpec
{
    public required string Name { get; init; }
    public required string Method { get; init; }
    public required string Route { get; init; }

    /// <summary>Arguments sent as query parameters (optional "src=queryName" rename).</summary>
    public IReadOnlyList<string> QueryParams { get; init; } = Array.Empty<string>();

    /// <summary>Tool sends a JSON body (all non-path, non-query arguments).</summary>
    public bool HasBody { get; init; }

    /// <summary>POST/PUT tool that sends no body at all (Python passed no json_body).</summary>
    public bool NoBody { get; init; }

    /// <summary>Apply url redaction to the response when redactUrls is true.</summary>
    public bool RedactUrls { get; init; }

    /// <summary>Always apply url redaction (get_file_open_link).</summary>
    public bool AlwaysRedact { get; init; }

    /// <summary>Local pagination over a bare list response (page/pageSize/total added).</summary>
    public bool LocalPagination { get; init; }

    /// <summary>Local pagination + &gt;50KB truncation hint (projects/task-books/habits/delivery-log/reports).</summary>
    public bool LocalPaginationTruncation { get; init; }

    /// <summary>Convert ISO start/end to YYYY-MM-DD before sending (heatmap/keystats/productivity range).</summary>
    public bool DateSpanConversion { get; init; }

    /// <summary>multipart/form-data upload.</summary>
    public bool Multipart { get; init; }

    public string FileField { get; init; } = "file";
    public string? FileName { get; init; }

    /// <summary>Multipart body source parameter: base64-decoded bytes (uploads) or raw text (import_ics).</summary>
    public string MultipartContentParam { get; init; } = "fileContentBase64";

    /// <summary>Write-tool arguments that must be non-empty ("X is required" errors).</summary>
    public IReadOnlyList<string> Required { get; init; } = Array.Empty<string>();

    public McpToolKind Kind { get; init; }
}

/// <summary>
/// Static table of all 151 MCP tools (101 read + 50 write). Source of truth for the
/// executor's HTTP mapping; the wire contract (name/description/inputSchema) lives in
/// the embedded <c>mcp-tools.json</c> dumped from the Python reference.
/// </summary>
public static class McpToolTable
{
    public static IReadOnlyDictionary<string, McpToolSpec> All { get; } = Build();

    public static McpToolSpec? TryGet(string toolName)
        => All.TryGetValue(toolName, out var spec) ? spec : null;

    private static Dictionary<string, McpToolSpec> Build()
    {
        var specs = new[]
        {
            // ===================== Calendar reads (31) =====================
            S("get_calendar_layers", "GET", "/api/v1/calendar/layers", Q("start", "end", "layers", "timezone", "redactUrls"), redact: true),
            S("query_data_center", "POST", "/api/v1/calendar/data-center/query", body: true),
            S("preview_data_center_batch", "POST", "/api/v1/calendar/data-center/batch/preview", body: true),
            S("get_data_center_audit_export", "GET", "/api/v1/calendar/data-center/audit/export", Q("start", "end", "timezone")),
            S("preview_data_center_restore", "POST", "/api/v1/calendar/data-center/restore/preview", body: true),
            S("get_projects", "GET", "/api/v1/calendar/projects", localPageTrunc: true),
            S("get_task_books", "GET", "/api/v1/calendar/task-books", localPageTrunc: true),
            S("get_habits", "GET", "/api/v1/calendar/habits", localPageTrunc: true),
            S("get_availability_windows", "GET", "/api/v1/calendar/availability"),
            S("get_reminders", "GET", "/api/v1/calendar/reminders"),
            S("get_reminder_delivery_log", "GET", "/api/v1/calendar/reminders/delivery-log", localPageTrunc: true),
            S("get_reports", "GET", "/api/v1/calendar/reports", localPageTrunc: true),
            S("get_report", "GET", "/api/v1/calendar/reports/{report_id}"),
            S("get_calendars", "GET", "/api/v1/calendar/calendars"),
            S("get_events", "GET", "/api/v1/calendar/events", Q("start", "end", "calendarId", "page", "pageSize"), redact: true),
            S("get_tasks", "GET", "/api/v1/calendar/tasks", Q("status", "calendarId", "page", "pageSize")),
            S("get_task_segments", "GET", "/api/v1/calendar/tasks/{task_id}/segments"),
            S("get_recycle_bin", "GET", "/api/v1/calendar/recycle-bin", Q("type", "start=deletedFrom", "end=deletedTo", "page", "pageSize")),
            S("preview_recycle_bin_restore", "POST", "/api/v1/calendar/recycle-bin/{type}/{id}/restore-preview", body: true),
            S("get_export_ics", "GET", "/api/v1/calendar/export-ics", kind: McpToolKind.ExportIcs),
            S("get_outlook_settings", "GET", "/api/v1/calendar/outlook/settings"),
            S("get_outlook_sync_batches", "GET", "/api/v1/calendar/outlook/sync/batches", Q("page", "pageSize")),
            S("get_outlook_local_data_preview", "GET", "/api/v1/calendar/outlook/local-data/preview"),
            S("get_event_by_id", "GET", "/api/v1/calendar/events/{event_id}", kind: McpToolKind.EventById),
            S("get_task_by_id", "GET", "/api/v1/calendar/tasks/{task_id}", kind: McpToolKind.TaskById),
            S("get_habit_occurrences", "GET", "/api/v1/calendar/habits/{habit_id}/occurrences", Q("start", "end"), kind: McpToolKind.HabitOccurrences),
            S("get_schedule_preview", "POST", "/api/v1/calendar/schedule", body: true, kind: McpToolKind.SchedulePreview),
            S("get_calendar_by_id", "GET", "/api/v1/calendar/calendars", kind: McpToolKind.CalendarById),
            S("get_task_checklist", "GET", "/api/v1/calendar/tasks/{task_id}/checklist", kind: McpToolKind.TaskChecklist),
            S("search_calendar_events", "GET", "/api/v1/calendar/events", Q("q=search", "start", "end", "page", "pageSize"), kind: McpToolKind.SearchEvents),
            S("search_calendar_tasks", "GET", "/api/v1/calendar/tasks", Q("q=search", "start", "end", "page", "pageSize"), kind: McpToolKind.SearchTasks),

            // ===================== PcTracker reads (27) =====================
            S("get_pc_summary", "GET", "/api/v1/pc/summary", Q("date", "timezone")),
            S("get_pc_detail", "GET", "/api/v1/pc/detail", Q("dateFrom", "dateTo", "date", "timezone", "page", "pageSize"), redact: true),
            S("get_pc_timeline", "GET", "/api/v1/pc/aw/timeline", Q("date", "timezone")),
            S("get_pc_timeline_v2", "GET", "/api/v1/pc/timeline/v2", Q("date", "timezone"), redact: true),
            S("get_pc_heatmap", "GET", "/api/v1/pc/heatmap/grid", Q("start", "end", "dimension", "timezone"), dateSpan: true),
            S("get_pc_activity_analysis", "GET", "/api/v1/pc/activity-analysis", Q("date", "blockMinutes", "timezone")),
            S("get_pc_quality", "GET", "/api/v1/pc/quality", Q("date", "dateFrom", "dateTo", "timezone")),
            S("get_pc_aw_heatmap", "GET", "/api/v1/pc/aw/heatmap", Q("start", "end", "timezone"), dateSpan: true),
            S("get_pc_keystats_range", "GET", "/api/v1/pc/keystats/range", Q("start", "end", "timezone"), dateSpan: true),
            S("get_pc_focus_blocks", "GET", "/api/v1/pc/aggregation/focus-blocks", Q("start", "end", "timezone")),
            S("get_pc_app_usage", "GET", "/api/v1/pc/aggregation/app-usage", Q("start", "end", "timezone", "limit")),
            S("get_pc_late_night", "GET", "/api/v1/pc/aggregation/late-night", Q("start", "end", "timezone")),
            S("get_pc_category_distribution", "GET", "/api/v1/pc/aggregation/category-distribution", Q("start", "end", "timezone")),
            S("get_pc_categories", "GET", "/api/v1/pc/categories"),
            S("get_pc_category_tree", "GET", "/api/v1/pc/categories/tree"),
            S("get_pc_category_dictionary", "GET", "/api/v1/pc/categories/dictionary"),
            S("get_pc_productivity_dashboard", "GET", "/api/v1/pc/productivity/dashboard", Q("date", "timezone")),
            S("get_pc_productivity_range", "GET", "/api/v1/pc/productivity/range", Q("start", "end", "timezone"), dateSpan: true),
            S("get_classification_rules", "GET", "/api/v1/pc/classification/rules"),
            S("get_classification_suggestions", "GET", "/api/v1/pc/classification/suggestions", Q("date")),
            S("get_classification_queue", "GET", "/api/v1/pc/classification/queue", Q("limit", "mode")),
            S("get_classification_project_tags_recent", "GET", "/api/v1/pc/classification/project-tags/recent", Q("limit")),
            S("get_app_knowledge_apps", "GET", "/api/v1/pc/app-knowledge/apps", Q("search"), localPage: true),
            S("get_app_knowledge_contexts", "GET", "/api/v1/pc/app-knowledge/apps/{appId}/contexts"),
            S("get_app_signatures", "GET", "/api/v1/pc/app-signatures", Q("search"), localPage: true),
            S("lookup_app_signature", "GET", "/api/v1/pc/app-signatures/lookup/{processName}"),
            S("get_classification_settings", "GET", "/api/v1/pc/classification/settings"),

            // ===================== Mobile reads (18) =====================
            S("get_mobile_summary", "GET", "/api/v1/mobile/summary", Q("date", "deviceId", "timezone")),
            S("get_mobile_timeline", "GET", "/api/v1/mobile/timeline", Q("date", "deviceId", "timezone"), redact: true),
            S("get_mobile_location_history", "GET", "/api/v1/mobile/location/history", Q("start", "end", "maxAccuracyMeters", "deviceId")),
            S("get_mobile_location_latest", "GET", "/api/v1/mobile/location/history", Q("maxAccuracyMeters", "deviceId"), kind: McpToolKind.MobileLocationLatest),
            S("get_mobile_location_tracks", "GET", "/api/v1/mobile/location/analytics/tracks", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone", "maxAccuracyMeters")),
            S("get_mobile_location_overview", "GET", "/api/v1/mobile/location/analytics/overview", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_location_frequent_places", "GET", "/api/v1/mobile/location/analytics/frequent-places", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_location_movement_stats", "GET", "/api/v1/mobile/location/analytics/movement-stats", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_quality", "GET", "/api/v1/mobile/quality", Q("date", "deviceId", "timezone")),
            S("get_mobile_analytics_overview", "GET", "/api/v1/mobile/analytics/overview", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_analytics_heatmap", "GET", "/api/v1/mobile/analytics/heatmap", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_analytics_charts", "GET", "/api/v1/mobile/analytics/charts", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone")),
            S("get_mobile_timeline_blocks", "GET", "/api/v1/mobile/analytics/timeline-blocks", Q("start=rangeStartUtc", "end=rangeEndUtc", "timezone", "page", "pageSize")),
            S("get_mobile_devices", "GET", "/api/v1/mobile/devices"),
            S("get_mobile_devices_manage", "GET", "/api/v1/mobile/devices/manage", Q("sortBy")),
            S("get_mobile_apps_catalog_overrides", "GET", "/api/v1/mobile/apps/catalog-overrides"),
            S("get_mobile_apps_category_rules", "GET", "/api/v1/mobile/apps/category-rules"),
            S("get_mobile_goals", "GET", "/api/v1/mobile/analytics/goals"),

            // ===================== QuickNotes reads (3) =====================
            S("get_quick_notes", "GET", "/api/v1/quick-notes", Q("status", "search", "page", "pageSize")),
            S("get_quick_note", "GET", "/api/v1/quick-notes/{note_id}"),
            S("get_quick_note_attachment_meta", "GET", "/api/v1/quick-notes/attachments/{attachment_id}/download", kind: McpToolKind.AttachmentMeta),

            // ===================== Files reads (8) =====================
            S("get_file_providers", "GET", "/api/v1/files/providers"),
            S("get_files", "GET", "/api/v1/files/items", Q("folderId=path", "page", "pageSize"), redact: true),
            S("get_file", "GET", "/api/v1/files/items/{file_id}"),
            S("get_file_versions", "GET", "/api/v1/files/items/{file_id}/versions"),
            S("get_file_trash", "GET", "/api/v1/files/trash", Q("page", "pageSize")),
            S("search_files", "GET", "/api/v1/files/search", Q("q", "page", "pageSize")),
            S("get_file_suggestions", "GET", "/api/v1/files/suggestions", Q("page", "pageSize")),
            S("get_file_open_link", "GET", "/api/v1/files/items/{file_id}/open-link", kind: McpToolKind.FileOpenLink),

            // ===================== Core/Infra reads (14) =====================
            S("get_today_sections", "GET", "/api/v1/today/sections", Q("date", "timezone")),
            S("get_today_section", "GET", "/api/v1/today/sections/{sectionId}", Q("date")),
            S("search_pim", "GET", "/api/v1/search", Q("q", "type", "limit")),
            S("get_system_status", "GET", "/api/v1/status"),
            S("get_system_health", "GET", "/health", kind: McpToolKind.Health),
            S("get_status_summary", "GET", "/api/v1/status/summary"),
            S("get_ai_status", "GET", "/api/v1/ai/status"),
            S("get_ai_requests", "GET", "/api/v1/ai/requests", Q("from_time=from", "to", "module", "status", "page", "pageSize")),
            S("get_ai_usage_summary", "GET", "/api/v1/ai/usage/summary", Q("from_time=from", "to")),
            S("get_audit_timeline", "GET", "/api/v1/operations/audit/{objectType}/{objectId}"),
            S("get_audit_export", "GET", "/api/v1/operations/audit/export", Q("start", "end")),
            S("get_confirmations_pending", "GET", "/api/v1/operations/confirmations/pending"),
            S("get_endpoints", "GET", "/api/v1/endpoints"),
            S("get_version", "GET", "/api/version", kind: McpToolKind.Version),

            // ===================== Calendar writes (30) =====================
            W("create_event", "POST", "/api/v1/calendar/events", req: R("calendarId", "title", "dtStart", "dtEnd")),
            W("update_event", "PUT", "/api/v1/calendar/events/{eventId}", query: Q("scope", "recurrenceId", "originalEventId"), req: R("eventId")),
            W("delete_event", "DELETE", "/api/v1/calendar/events/{eventId}", query: Q("scope", "recurrenceId", "originalEventId"), req: R("eventId")),
            W("restore_event", "POST", "/api/v1/calendar/events/{eventId}/restore", req: R("eventId")),
            W("batch_delete_events", "POST", "/api/v1/calendar/events/batch-delete", req: R("ids")),

            W("create_task", "POST", "/api/v1/calendar/tasks", req: R("title")),
            W("update_task", "PUT", "/api/v1/calendar/tasks/{taskId}", req: R("taskId")),
            W("delete_task", "DELETE", "/api/v1/calendar/tasks/{taskId}", req: R("taskId")),
            W("restore_task", "POST", "/api/v1/calendar/tasks/{taskId}/restore", req: R("taskId")),
            W("move_task", "POST", "/api/v1/calendar/tasks/{taskId}/move", req: R("taskId")),
            W("plan_task", "POST", "/api/v1/calendar/tasks/{taskId}/plan", req: R("taskId", "plannedStart")),
            W("create_task_segment", "POST", "/api/v1/calendar/tasks/{taskId}/segments", req: R("taskId", "startsAt", "endsAt", "status", "source")),
            W("delete_task_segment", "DELETE", "/api/v1/calendar/tasks/{taskId}/segments/{segmentId}", req: R("taskId", "segmentId")),
            W("add_task_checklist_item", "POST", "/api/v1/calendar/tasks/{taskId}/checklist", req: R("taskId", "title")),
            W("batch_delete_tasks", "POST", "/api/v1/calendar/tasks/batch-delete", req: R("ids")),
            W("batch_update_tasks", "POST", "/api/v1/calendar/tasks/batch-update", req: R("ids")),
            W("create_task_book", "POST", "/api/v1/calendar/task-books", req: R("name")),
            W("create_project", "POST", "/api/v1/calendar/projects", req: R("name")),
            W("schedule_tasks", "POST", "/api/v1/calendar/schedule", req: R("taskIds")),

            W("create_reminder", "POST", "/api/v1/calendar/reminders", req: R("relatedObjectType", "relatedObjectId", "title", "scheduledAt")),
            W("snooze_reminder", "POST", "/api/v1/calendar/reminders/{reminderId}/snooze", query: Q("scheduledAt"), req: R("reminderId")),
            W("dismiss_reminder", "POST", "/api/v1/calendar/reminders/{reminderId}/dismiss", req: R("reminderId"), noBody: true),

            W("create_habit", "POST", "/api/v1/calendar/habits", req: R("title")),
            W("create_habit_occurrence", "POST", "/api/v1/calendar/habits/{habitId}/occurrences", req: R("habitId", "startsAt", "endsAt")),

            W("create_availability_window", "POST", "/api/v1/calendar/availability", req: R("title", "startsAt", "endsAt")),
            W("import_ics", "POST", "/api/v1/calendar/import-ics", req: R("icsContent"), multipart: true, fileName: "import.ics", multipartContentParam: "icsContent"),
            W("create_calendar", "POST", "/api/v1/calendar/calendars", req: R("name")),
            W("update_calendar", "PUT", "/api/v1/calendar/calendars/{calendarId}", req: R("calendarId", "name")),
            W("delete_calendar", "DELETE", "/api/v1/calendar/calendars/{calendarId}", req: R("calendarId")),
            W("restore_calendar", "POST", "/api/v1/calendar/calendars/{calendarId}/restore", req: R("calendarId"), noBody: true),

            // ===================== QuickNotes writes (8) =====================
            W("create_quick_note", "POST", "/api/v1/quick-notes", req: R("contentMarkdown")),
            W("update_quick_note", "PUT", "/api/v1/quick-notes/{noteId}", req: R("noteId")),
            W("delete_quick_note", "DELETE", "/api/v1/quick-notes/{noteId}", req: R("noteId")),
            W("archive_quick_note", "POST", "/api/v1/quick-notes/{noteId}/archive", req: R("noteId"), noBody: true),
            W("restore_quick_note", "POST", "/api/v1/quick-notes/{noteId}/restore", req: R("noteId")),
            W("process_quick_note", "POST", "/api/v1/quick-notes/{noteId}/process", req: R("noteId"), noBody: true),
            W("upload_quick_note_attachment", "POST", "/api/v1/quick-notes/attachments", req: R("fileContentBase64", "fileName"), multipart: true),
            W("delete_quick_note_attachment", "DELETE", "/api/v1/quick-notes/attachments/{attachmentId}", req: R("attachmentId")),

            // ===================== Files writes (6) =====================
            W("upload_file", "POST", "/api/v1/files/items/upload", req: R("providerId", "path", "fileContentBase64", "fileName"), multipart: true),
            W("move_file", "POST", "/api/v1/files/items/{fileId}/move", req: R("fileId", "destinationPath")),
            W("rename_file", "POST", "/api/v1/files/items/{fileId}/rename", req: R("fileId", "name")),
            W("delete_file", "DELETE", "/api/v1/files/items/{fileId}", req: R("fileId")),
            W("restore_file", "POST", "/api/v1/files/trash/{fileId}/restore", query: Q("trashId"), req: R("fileId", "trashId")),
            W("index_file", "POST", "/api/v1/files/items/{fileId}/index", req: R("fileId")),

            // ===================== PcTracker writes (4) =====================
            W("create_category", "POST", "/api/v1/pc/categories", req: R("appPattern", "categoryName", "color", "priority")),
            W("update_categories_order", "PUT", "/api/v1/pc/categories/reorder", req: R("items")),
            W("delete_category", "DELETE", "/api/v1/pc/categories/{categoryId}", req: R("categoryId")),
            W("seed_categories", "POST", "/api/v1/pc/categories/seed", noBody: true),

            // ===================== Mobile writes (2) =====================
            W("create_mobile_goal", "POST", "/api/v1/mobile/analytics/goals", req: R("limitSeconds")),
            W("delete_mobile_goal", "DELETE", "/api/v1/mobile/analytics/goals/{goalId}", req: R("goalId")),
        };

        var map = new Dictionary<string, McpToolSpec>(StringComparer.Ordinal);
        foreach (var spec in specs)
            map[spec.Name] = spec;
        return map;
    }

    private static string[] Q(params string[] query) => query;

    private static string[] R(params string[] required) => required;

    private static McpToolSpec S(
        string name,
        string method,
        string route,
        string[]? q = null,
        bool body = false,
        bool redact = false,
        bool localPage = false,
        bool localPageTrunc = false,
        bool dateSpan = false,
        bool noBody = false,
        McpToolKind kind = McpToolKind.None)
        => new()
        {
            Name = name,
            Method = method,
            Route = route,
            QueryParams = q ?? Array.Empty<string>(),
            HasBody = body,
            NoBody = noBody,
            RedactUrls = redact,
            LocalPagination = localPage,
            LocalPaginationTruncation = localPageTrunc,
            DateSpanConversion = dateSpan,
            Kind = kind,
        };

    private static McpToolSpec W(
        string name,
        string method,
        string route,
        string[]? query = null,
        string[]? req = null,
        bool multipart = false,
        string? fileName = null,
        bool noBody = false,
        string multipartContentParam = "fileContentBase64")
        => new()
        {
            Name = name,
            Method = method,
            Route = route,
            QueryParams = query ?? Array.Empty<string>(),
            HasBody = !multipart && !noBody && method is "POST" or "PUT",
            Required = req ?? Array.Empty<string>(),
            Multipart = multipart,
            FileName = fileName,
            NoBody = noBody,
            MultipartContentParam = multipartContentParam,
        };
}