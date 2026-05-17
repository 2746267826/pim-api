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
        services.AddScoped<SchedulingEngine>();
        services.AddScoped<OutlookSyncService>();

        services.AddSingleton<ISearchProvider, CalendarSearchProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/calendar")
            .RequireAuthorization();

        // Calendars
        group.MapGet("/calendars", async (
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<List<CalendarResponse>>.Ok(await svc.GetCalendarsAsync(ct))));

        group.MapPost("/calendars", async (
            [FromBody] CreateCalendarRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Created($"/api/v1/calendar/calendars/{{id}}",
                ApiResponse<CalendarResponse>.Ok(await svc.CreateCalendarAsync(req, ct))));

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
            Guid id, [FromBody] CreateEventRequest req,
            [FromServices] CalendarService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<EventResponse>.Ok(await svc.UpdateEventAsync(id, req, ct))));

        group.MapDelete("/events/{id:guid}", async (
            Guid id, [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            await svc.DeleteEventAsync(id, ct);
            return Results.Ok(ApiResponse<string>.Ok("deleted"));
        });

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

        group.MapDelete("/tasks/{id:guid}", async (
            Guid id,
            [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            await svc.DeleteTaskAsync(id, ct);
            return Results.Ok(ApiResponse<string>.Ok("deleted"));
        });

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
            using var reader = new StreamReader(request.Body);
            var icsContent = await reader.ReadToEndAsync(ct);
            var parsed = icsService.ImportEvents(icsContent);

            var calendars = await calendarService.GetCalendarsAsync(ct);
            var calendarId = calendars.FirstOrDefault()?.Id
                ?? (await calendarService.CreateCalendarAsync(
                    new CreateCalendarRequest("默认日历", null), ct)).Id;

            var imported = 0;
            foreach (var evt in parsed)
            {
                try
                {
                    await calendarService.CreateEventAsync(
                        new CreateEventRequest(calendarId, evt.Title, evt.Description,
                            evt.Location, evt.Start, evt.End, evt.RRule), ct);
                    imported++;
                }
                catch
                {
                    // skip events that fail validation
                }
            }

            return Results.Ok(ApiResponse<int>.Ok(imported));
        });

        group.MapGet("/export-ics", async (
            [FromQuery] DateTimeOffset start,
            [FromQuery] DateTimeOffset end,
            [FromServices] CalendarService svc,
            [FromServices] IcsService icsService,
            CancellationToken ct) =>
        {
            var eventEntities = await svc.GetEventEntitiesAsync(start, end, ct);
            var icsContent = icsService.ExportEvents(eventEntities);
            return Results.Ok(ApiResponse<string>.Ok(icsContent));
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
