using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
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
        services.AddScoped<CalendarAuditWriter>();
        services.AddScoped<CalendarDeleteService>();
        services.AddScoped<CalendarRecycleBinService>();

        services.AddSingleton<ISearchProvider, CalendarSearchProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CalendarEndpointPaths.Root)
            .RequireAuthorization();

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
        group.MapPost("/outlook/sync", async (
            [FromServices] OutlookSyncService outlookSvc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            await outlookSvc.SyncAsync(currentUser.UserId!.Value, ct);
            return Results.Ok(ApiResponse<string>.Ok("已同步"));
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

    public static string TaskPlan(string id) => $"{Root}/tasks/{id}/plan";
    public static string RecycleRestorePreview(string type, string id) => $"{RecycleBin}/{type}/{id}/restore-preview";
    public static string RecycleRestore(string type, string id) => $"{RecycleBin}/{type}/{id}/restore";
}
