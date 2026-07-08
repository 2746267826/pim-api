using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Search;
using Pim.Module.Calendar.Services;

namespace Pim.Module.Calendar;

public class CalendarModule : IModule
{
    public string Name => "calendar";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());

        services.AddScoped<CalendarService>();
        services.AddScoped<IcsService>();
        services.AddScoped<OutlookIcsService>();
        services.AddScoped<RecurrenceService>();
        services.AddScoped<SchedulingEngine>();
        services.AddScoped<OutlookSyncService>();
        services.AddScoped<OutlookTokenService>();
        services.AddScoped<IMicrosoftGraphClient, MicrosoftGraphDeviceCodeClient>();
        services.AddHttpClient("outlook");
        services.AddScoped<OutlookConflictService>();
        services.AddScoped<CalendarAuditWriter>();
        services.AddScoped<CalendarDeleteService>();
        services.AddScoped<CalendarRecycleBinService>();
        services.AddScoped<PlanningModelService>();
        services.AddScoped<DataCenterQueryService>();
        services.AddScoped<ReminderService>();

        services.AddSingleton<ISearchProvider, CalendarSearchProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CalendarEndpointPaths.Root)
            .RequireAuthorization();

        // Workbench queries
        group.MapGet("/layers", async (
            [FromQuery] DateTimeOffset start,
            [FromQuery] DateTimeOffset end,
            [FromQuery] string? layers,
            [FromQuery] bool? outlookOnly,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
        {
            var requestedLayers = string.IsNullOrWhiteSpace(layers)
                ? null
                : layers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await svc.GetCalendarLayersAsync(
                new CalendarLayerQuery(start, end, requestedLayers, outlookOnly ?? false),
                ct);
            return Results.Ok(ApiResponse<CalendarLayerResponse>.Ok(result));
        });

        group.MapPost("/data-center/query", async (
            [FromBody] DataCenterQueryRequest req,
            [FromServices] DataCenterQueryService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<DataCenterQueryResponse>.Ok(await svc.QueryAsync(req, ct))));

        group.MapGet("/projects", async (
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ListProjectsAsync(ct))));

        group.MapPost("/projects", async (
            [FromBody] CreateDomainProjectRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/projects",
                ApiResponse<object>.Ok(await svc.CreateProjectAsync(req, ct))));

        group.MapGet("/task-books", async (
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ListTaskBooksAsync(ct))));

        group.MapPost("/task-books", async (
            [FromBody] CreateTaskBookRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/task-books",
                ApiResponse<object>.Ok(await svc.CreateTaskBookAsync(req, ct))));

        group.MapPost("/tasks/{id:guid}/checklist", async (
            Guid id,
            [FromBody] AddTaskChecklistItemRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created($"/api/v1/calendar/tasks/{id}/checklist",
                ApiResponse<object>.Ok(await svc.AddChecklistItemAsync(id, req, ct))));

        group.MapGet("/habits", async (
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ListHabitsAsync(ct))));

        group.MapPost("/habits", async (
            [FromBody] CreateHabitRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/habits",
                ApiResponse<object>.Ok(await svc.CreateHabitAsync(req, ct))));

        group.MapPost("/habits/{id:guid}/occurrences", async (
            Guid id,
            [FromBody] CreateHabitOccurrenceRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created($"/api/v1/calendar/habits/{id}/occurrences",
                ApiResponse<object>.Ok(await svc.CreateHabitOccurrenceAsync(id, req, ct))));

        group.MapGet("/availability", async (
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ListAvailabilityAsync(ct))));

        group.MapPost("/availability", async (
            [FromBody] CreateAvailabilityWindowRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/availability",
                ApiResponse<object>.Ok(await svc.CreateAvailabilityWindowAsync(req, ct))));

        group.MapPost("/ai-placeholders", async (
            [FromBody] CreateAiPlanningPlaceholderRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/ai-placeholders",
                ApiResponse<object>.Ok(await svc.CreateAiPlaceholderAsync(req, ct))));

        group.MapPost("/ai-placeholders/{id:guid}/confirm", async (
            Guid id,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ConfirmAiPlaceholderAsync(id, ct))));

        group.MapGet("/reminders", async (
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.ListAsync(ct))));

        group.MapPost("/reminders", async (
            [FromBody] CreateReminderRequest req,
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Created("/api/v1/calendar/reminders",
                ApiResponse<object>.Ok(await svc.CreateAsync(req, ct))));

        group.MapPost("/reminders/{id:guid}/snooze", async (
            Guid id,
            [FromQuery] DateTimeOffset? scheduledAt,
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.SnoozeAsync(
                id,
                scheduledAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
                ct))));

        group.MapPost("/reminders/{id:guid}/dismiss", async (
            Guid id,
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.DismissAsync(id, ct))));

        group.MapPost("/reminders/{id:guid}/actions/{action}", async (
            Guid id,
            string action,
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.HandleActionAsync(id, action, ct))));

        group.MapGet("/reminders/delivery-log", async (
            [FromServices] ReminderService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.GetDeliveryLogAsync(ct))));

        // Calendars
        group.MapGet("/calendars", async (
            [FromQuery] string? kind,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<List<CalendarResponse>>.Ok(await svc.GetCalendarsAsync(kind, ct))));

        group.MapPost("/calendars", async (
            [FromBody] CreateCalendarRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Created($"/api/v1/calendar/calendars/{{id}}",
                ApiResponse<CalendarResponse>.Ok(await svc.CreateCalendarAsync(req, ct))));

        group.MapPut("/calendars/{id:guid}", async (
            Guid id, [FromBody] CreateCalendarRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarResponse>.Ok(await svc.UpdateCalendarAsync(id, req, ct))));

        group.MapPost("/calendars/{id:guid}/delete-preview", async (
            Guid id, [FromServices] CalendarDeleteService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarDeletePreviewResponse>.Ok(await svc.PreviewCalendarDeleteAsync(id, ct))));

        group.MapDelete("/calendars/{id:guid}", async (
            Guid id, [FromServices] CalendarDeleteService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.DeleteCalendarAsync(id, ct))));

        group.MapPost("/calendars/{id:guid}/restore", async (
            Guid id, [FromServices] CalendarRecycleBinService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.RestoreAsync("calendar", id, new CalendarRestoreRequest(), ct))));

        // Events
        group.MapGet("/events", async (
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end,
            [FromQuery] string? search,
            [FromQuery] Guid? calendarId,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] CalendarService svc,
            CancellationToken ct) =>
        {
            // If only start/end given (no search/calendarId/page), use old path for backward compat
            if (search is null && calendarId is null && page is null && pageSize is null)
            {
                // Old behavior: no pagination, returns List
                var events = await svc.GetEventsAsync(start ?? DateTimeOffset.MinValue, end ?? DateTimeOffset.MaxValue, ct);
                return Results.Ok(ApiResponse<List<EventResponse>>.Ok(events));
            }

            var result = await svc.GetEventsPagedAsync(search, calendarId, start, end, page ?? 1, pageSize ?? 50, ct);
            return Results.Ok(ApiResponse<PagedResult<EventResponse>>.Ok(result));
        });

        group.MapPost("/events", async (
            [FromBody] CreateEventRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateEventAsync(req, ct);
            return Results.Created($"/api/v1/calendar/events/{result.Id}",
                ApiResponse<EventResponse>.Ok(result));
        });

        group.MapPut("/events/{id:guid}", async (
            Guid id, [FromBody] UpdateEventRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<EventResponse>.Ok(await svc.UpdateEventAsync(id, req, ct))));

        group.MapDelete("/events/{id:guid}", async (
            Guid id, [FromServices] CalendarDeleteService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.DeleteEventAsync(id, ct))));

        group.MapPost("/events/{id:guid}/restore", async (
            Guid id,
            [FromBody] CalendarRestoreRequest? req,
            [FromServices] CalendarRecycleBinService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.RestoreAsync("event", id, req ?? new CalendarRestoreRequest(), ct))));

        group.MapPost("/events/batch-delete", async (
            [FromBody] BatchIdsRequest req,
            [FromServices] CalendarDeleteService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.BatchDeleteEventsAsync(req.Ids, ct))));

        // Tasks
        group.MapGet("/tasks", async (
            [FromQuery] bool? inbox,
            [FromQuery] string? search,
            [FromQuery] Guid? calendarId,
            [FromQuery] string? status,
            [FromQuery] int? priority,
            [FromQuery] DateTimeOffset? plannedFrom,
            [FromQuery] DateTimeOffset? plannedTo,
            [FromQuery] DateTimeOffset? dueFrom,
            [FromQuery] DateTimeOffset? dueTo,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] CalendarService svc,
            CancellationToken ct) =>
        {
            if (search is null && calendarId is null && status is null && priority is null
                && plannedFrom is null && plannedTo is null && dueFrom is null && dueTo is null
                && page is null && pageSize is null)
            {
                return Results.Ok(ApiResponse<List<TaskResponse>>.Ok(await svc.GetTasksAsync(inbox, ct)));
            }

            var result = await svc.GetTasksPagedAsync(
                inbox,
                search,
                calendarId,
                status,
                priority,
                plannedFrom,
                plannedTo,
                dueFrom,
                dueTo,
                page ?? 1,
                pageSize ?? 50,
                ct);
            return Results.Ok(ApiResponse<PagedResult<TaskResponse>>.Ok(result));
        });

        group.MapPost("/tasks", async (
            [FromBody] CreateTaskRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Created("/api/v1/calendar/tasks",
                ApiResponse<TaskResponse>.Ok(await svc.CreateTaskAsync(req, ct))));

        group.MapPost("/tasks/{id:guid}/move", async (
            Guid id, [FromBody] MoveTaskRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            await svc.MoveTaskAsync(id, req, ct);
            return Results.Ok(ApiResponse<string>.Ok("已移动"));
        });

        group.MapPost("/tasks/{id:guid}/plan", async (
            Guid id,
            [FromBody] PlanTaskRequest req,
            [FromServices] CalendarService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<TaskResponse>.Ok(await svc.PlanTaskAsync(id, req, ct))));

        group.MapGet("/tasks/{id:guid}/segments", async (
            Guid id,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<TaskExecutionSegmentResponse>>.Ok(
                await svc.ListSegmentsAsync(id, ct))));

        group.MapPost("/tasks/{id:guid}/segments", async (
            Guid id,
            [FromBody] CreateTaskExecutionSegmentRequest req,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
        {
            var result = await svc.CreateSegmentAsync(id, req, ct);
            return Results.Created($"/api/v1/calendar/tasks/{id}/segments/{result.Id}",
                ApiResponse<TaskExecutionSegmentResponse>.Ok(result));
        });

        group.MapDelete("/tasks/{taskId:guid}/segments/{segmentId:guid}", async (
            Guid taskId,
            Guid segmentId,
            [FromServices] PlanningModelService svc,
            CancellationToken ct) =>
        {
            await svc.DeleteSegmentAsync(taskId, segmentId, ct);
            return Results.Ok(ApiResponse<string>.Ok("deleted"));
        });

        group.MapPut("/tasks/{id:guid}", async (
            Guid id, [FromBody] UpdateTaskRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<TaskResponse>.Ok(await svc.UpdateTaskAsync(id, req, ct))));

        group.MapDelete("/tasks/{id:guid}", async (
            Guid id,
            [FromServices] CalendarDeleteService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.DeleteTaskAsync(id, ct))));

        group.MapPost("/tasks/{id:guid}/restore", async (
            Guid id,
            [FromBody] CalendarRestoreRequest? req,
            [FromServices] CalendarRecycleBinService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.RestoreAsync("task", id, req ?? new CalendarRestoreRequest(), ct))));

        group.MapPost("/tasks/batch-delete", async (
            [FromBody] BatchIdsRequest req,
            [FromServices] CalendarDeleteService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.BatchDeleteTasksAsync(req.Ids, ct))));

        group.MapPost("/tasks/batch-update", async (
            [FromBody] BatchTaskUpdateRequest req,
            [FromServices] CalendarService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.BatchUpdateTasksAsync(req, ct))));

        // Recycle bin
        group.MapGet("/recycle-bin", async (
            [FromQuery] string? type,
            [FromQuery] string? search,
            [FromQuery] DateTimeOffset? deletedFrom,
            [FromQuery] DateTimeOffset? deletedTo,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] CalendarRecycleBinService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<PagedResult<CalendarRecycleBinItem>>.Ok(
                await svc.ListAsync(type, search, deletedFrom, deletedTo, page ?? 1, pageSize ?? 50, ct))));

        group.MapPost("/recycle-bin/{type}/{id:guid}/restore-preview", async (
            string type,
            Guid id,
            [FromServices] CalendarRecycleBinService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarRestorePreviewResponse>.Ok(await svc.PreviewRestoreAsync(type, id, ct))));

        group.MapPost("/recycle-bin/{type}/{id:guid}/restore", async (
            string type,
            Guid id,
            [FromBody] CalendarRestoreRequest? req,
            [FromServices] CalendarRecycleBinService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.RestoreAsync(type, id, req ?? new CalendarRestoreRequest(), ct))));

        // Scheduling
        group.MapPost("/schedule", async (
            [FromBody] ScheduleRequest req,
            [FromServices] SchedulingEngine engine,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var solutions = await engine.GeneratePlansAsync(
                currentUser.UserId!.Value, req.TaskIds, ct);
            return Results.Ok(ApiResponse<List<ScheduleSolution>>.Ok(solutions));
        });

        // ICS
        group.MapPost("/import-ics", async (
            HttpRequest request,
            [FromServices] OutlookIcsService outlookIcsService,
            [FromServices] CalendarService calendarService,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(ApiResponse<string>.Error(400, "需要 multipart/form-data"));

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(ApiResponse<string>.Error(400, "缺少文件字段"));

            var calendarIdStr = form.TryGetValue("calendarId", out var cidVal) ? cidVal.ToString() : null;
            Guid? targetCalendarId = null;
            if (Guid.TryParse(calendarIdStr, out var cid))
                targetCalendarId = cid;

            using var reader = new StreamReader(file.OpenReadStream());
            var icsContent = await reader.ReadToEndAsync(ct);
            var report = await calendarService.ImportOutlookIcsAsync(icsContent, targetCalendarId, outlookIcsService, ct);

            return Results.Ok(ApiResponse<ImportReport>.Ok(report));
        });

        group.MapGet("/export-ics", async (
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end,
            [FromQuery] string? ids,
            [FromServices] CalendarService svc,
            [FromServices] IcsService icsService,
            CancellationToken ct) =>
        {
            var entities = await svc.GetEventEntitiesAsync(
                start ?? DateTimeOffset.MinValue,
                end ?? DateTimeOffset.MaxValue, ct);

            if (!string.IsNullOrEmpty(ids))
            {
                var idSet = new HashSet<Guid>();
                foreach (var part in ids.Split(','))
                {
                    if (Guid.TryParse(part.Trim(), out var g))
                        idSet.Add(g);
                }
                entities = entities.Where(e => idSet.Contains(e.Id)).ToList();
            }

            var icsContent = icsService.ExportEvents(entities);
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(icsContent),
                "text/calendar",
                "pim-events.ics");
        });

        // Outlook
        group.MapGet("/outlook/settings", async (
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(
                await outlookSvc.GetSettingsAsync(currentUser.UserId!.Value, ct))));

        group.MapPut("/outlook/settings", async (
            [FromBody] UpdateOutlookSettingsRequest req,
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(
                await outlookSvc.UpdateSettingsAsync(currentUser.UserId!.Value, req, ct))));

        group.MapPost("/outlook/device-code", async (
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OutlookDeviceCodeRequestResponse>.Ok(
                await outlookSvc.CreateDeviceCodeRequestAsync(currentUser.UserId!.Value, ct))));

        group.MapGet("/outlook/sync/batches", async (
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<OutlookSyncBatchResponse>>.Ok(
                await outlookSvc.ListBatchesAsync(currentUser.UserId!.Value, ct))));

        group.MapPost("/outlook/sync", async (
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await outlookSvc.SyncAsync(currentUser.UserId!.Value, ct);
            return Results.Ok(ApiResponse<OutlookSyncBatchResponse>.Ok(result));
        });

        group.MapGet("/outlook/events", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var items = await db.Set<EventEntity>()
                .AsNoTracking()
                .Include(e => e.Calendar)
                .Where(e => e.Calendar.UserId == userId && e.Source.StartsWith("outlook"))
                .OrderBy(e => e.DtStart)
                .ToListAsync(ct);
            return Results.Ok(ApiResponse<object>.Ok(items.Select(e => new
            {
                e.Id,
                e.Title,
                e.OutlookEventId,
                e.OutlookChangeKey,
                e.Source,
                e.DtStart,
                e.DtEnd
            }).ToList()));
        });

        group.MapPost("/outlook/events/batch-tag", async (
            [FromBody] BatchIdsRequest req,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var events = await db.Set<EventEntity>()
                .Include(e => e.Calendar)
                .Where(e => req.Ids.Contains(e.Id) && e.Calendar.UserId == userId)
                .ToListAsync(ct);
            foreach (var evt in events)
            {
                evt.Source = "outlook";
                evt.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<object>.Ok(new { affectedCount = events.Count }));
        });

        group.MapPost("/outlook/events/{id:guid}/pause-sync", async (
            Guid id,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var evt = await db.Set<EventEntity>()
                .Include(e => e.Calendar)
                .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == userId, ct);
            if (evt is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "Event does not exist."));

            evt.Source = "outlook-paused";
            evt.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<object>.Ok(new { evt.Id, evt.Source }));
        });

        group.MapPost("/outlook/events/{id:guid}/stop-sync-preview", async (
            Guid id,
            [FromServices] OutlookConflictService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.RequestStopSyncPreviewAsync(id, ct))));

        group.MapPost("/outlook/events/{id:guid}/stop-sync", async (
            Guid id,
            [FromServices] OutlookConflictService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<object>.Ok(await svc.RequestStopSyncPreviewAsync(id, ct))));

        group.MapGet("/outlook/events/{id:guid}/history", async (
            Guid id,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var evt = await db.Set<EventEntity>()
                .AsNoTracking()
                .Include(e => e.Calendar)
                .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == userId, ct);
            if (evt is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "Event does not exist."));

            return Results.Ok(ApiResponse<object>.Ok(new
            {
                evt.Id,
                evt.OutlookEventId,
                evt.OutlookChangeKey,
                evt.OutlookEtag,
                evt.SourceIcsComponent
            }));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        await Task.CompletedTask;
    }
}

public static class CalendarEndpointPaths
{
    public const string Root = "/api/v1/calendar";
    public const string RecycleBin = "/api/v1/calendar/recycle-bin";
    public const string EventBatchDelete = "/api/v1/calendar/events/batch-delete";
    public const string TaskBatchUpdate = "/api/v1/calendar/tasks/batch-update";
    public const string TaskBatchDelete = "/api/v1/calendar/tasks/batch-delete";
    public const string ImportIcs = "/api/v1/calendar/import-ics";
    public const string ExportIcs = "/api/v1/calendar/export-ics";
    public const string CalendarLayers = "/api/v1/calendar/layers";
    public const string DataCenterQuery = "/api/v1/calendar/data-center/query";
    public const string OutlookSettings = "/api/v1/calendar/outlook/settings";
    public const string OutlookDeviceCode = "/api/v1/calendar/outlook/device-code";
    public const string OutlookSync = "/api/v1/calendar/outlook/sync";
    public const string OutlookSyncBatches = "/api/v1/calendar/outlook/sync/batches";

    public static string TaskPlan(string id) => $"{Root}/tasks/{id}/plan";
    public static string RecycleRestorePreview(string type, string id) => $"{RecycleBin}/{type}/{id}/restore-preview";
    public static string RecycleRestore(string type, string id) => $"{RecycleBin}/{type}/{id}/restore";
}
