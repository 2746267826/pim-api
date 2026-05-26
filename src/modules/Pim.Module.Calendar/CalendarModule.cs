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
        services.AddScoped<RecurrenceService>();
        services.AddScoped<SchedulingEngine>();
        services.AddScoped<OutlookSyncService>();
        services.AddScoped<CalendarAuditWriter>();
        services.AddScoped<CalendarDeleteService>();

        services.AddSingleton<ISearchProvider, CalendarSearchProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/calendar")
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

        group.MapPost("/events/batch-delete", async (
            [FromBody] BatchIdsRequest req,
            [FromServices] CalendarDeleteService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.BatchDeleteEventsAsync(req.Ids, ct))));

        // Tasks
        group.MapGet("/tasks", async (
            [FromQuery] bool? inbox,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<List<TaskResponse>>.Ok(await svc.GetTasksAsync(inbox, ct))));

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
            return Results.Ok(ApiResponse<string>.Ok("moved"));
        });

        group.MapPut("/tasks/{id:guid}", async (
            Guid id, [FromBody] UpdateTaskRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<TaskResponse>.Ok(await svc.UpdateTaskAsync(id, req, ct))));

        group.MapDelete("/tasks/{id:guid}", async (
            Guid id,
            [FromServices] CalendarDeleteService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.DeleteTaskAsync(id, ct))));

        group.MapPost("/tasks/batch-delete", async (
            [FromBody] BatchIdsRequest req,
            [FromServices] CalendarDeleteService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<CalendarOperationResult>.Ok(await svc.BatchDeleteTasksAsync(req.Ids, ct))));

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
            [FromServices] IcsService icsService,
            [FromServices] CalendarService calendarService,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(ApiResponse<string>.Error(400, "Expected multipart/form-data"));

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(ApiResponse<string>.Error(400, "No file field"));

            var calendarIdStr = form.TryGetValue("calendarId", out var cidVal) ? cidVal.ToString() : null;
            Guid? targetCalendarId = null;
            if (Guid.TryParse(calendarIdStr, out var cid))
                targetCalendarId = cid;

            using var reader = new StreamReader(file.OpenReadStream());
            var icsContent = await reader.ReadToEndAsync(ct);
            var parsed = icsService.ImportEvents(icsContent);

            var entities = await calendarService.GetEventEntitiesAsync(
                DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
            var existingKeys = entities.Select(e => (e.Title, e.DtStart)).ToHashSet();
            var existingUids = entities.Select(e => e.Uid).ToHashSet();

            var calendars = await calendarService.GetCalendarsAsync(null, ct);
            var calendarId = targetCalendarId ?? calendars.FirstOrDefault()?.Id
                ?? (await calendarService.CreateCalendarAsync(
                    new CreateCalendarRequest("默认日历", null, Kind: "calendar"), ct)).Id;

            int imported = 0, skipped = 0;
            foreach (var evt in parsed)
            {
                if (existingUids.Contains(evt.Uid) || existingKeys.Contains((evt.Title, evt.Start)))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await calendarService.CreateEventAsync(
                        new CreateEventRequest(calendarId, evt.Title, evt.Description,
                            evt.Location, evt.Start, evt.End, evt.RRule, evt.Uid), ct);
                    imported++;
                }
                catch
                {
                    skipped++;
                }
            }

            return Results.Ok(ApiResponse<ImportResult>.Ok(new ImportResult(imported, skipped)));
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
            return Results.Ok(ApiResponse<string>.Ok("synced"));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        await Task.CompletedTask;
    }
}
