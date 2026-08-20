using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pim.Core.Audit;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Modules;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Hangfire;
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

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CalendarService>();
        services.AddScoped<IcsService>();
        services.AddScoped<OutlookIcsService>();
        services.AddScoped<RecurrenceService>();
        services.AddScoped<SchedulingEngine>();
        services.AddScoped<OutlookSyncService>();
        services.AddScoped<OutlookTokenService>();
        services.AddScoped<IMicrosoftGraphClient, MicrosoftGraphDeviceCodeClient>();
        services.AddSingleton<OutlookTokenCacheLock>();
        services.AddScoped<OutlookTokenCacheStore>();
        services.AddScoped<IMsalPublicClientAdapter, MsalPublicClientAdapter>();
        services.AddScoped<IOutlookAccessTokenProvider, MsalOutlookAuthCoordinator>();
        services.AddSingleton<OutlookAuthorizationSessionRunner>();
        services.AddHttpClient("outlook");
        services.AddScoped<OutlookConflictService>();
        services.AddScoped<CalendarAuditWriter>();
        services.AddScoped<CalendarDeleteService>();
        services.AddScoped<CalendarRecycleBinService>();
        services.AddScoped<PlanningModelService>();
        services.AddScoped<DataCenterQueryService>();
        services.AddScoped<DataCenterGovernanceService>();
        services.AddScoped<ReminderService>();
        services.AddScoped<ReportService>();

        services.AddSingleton<ISearchProvider, CalendarSearchProvider>();

        // New lightweight chain (Task 7)
        services.AddScoped<GraphCalendarClient>();
        services.AddScoped<EventAttachmentService>();
        services.AddScoped<OutlookCalendarSyncService>();
        services.AddScoped<OutlookEventWriteService>();
        services.AddScoped<OutlookCalendarSyncJob>();
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

        group.MapPost("/data-center/batch/preview", async (
            [FromBody] DataCenterBatchOperationRequest req,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<DataCenterBatchPreviewResponse>.Ok(
                await svc.PreviewBatchOperationAsync(req, ct))));

        group.MapPost("/data-center/batch/request-confirmation", async (
            [FromBody] DataCenterBatchOperationRequest req,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(
                await svc.RequestBatchConfirmationAsync(req, ct))));

        group.MapPost("/data-center/batch/execute", async (
            [FromBody] DataCenterExecuteBatchRequest req,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<DataCenterBatchExecutionResponse>.Ok(
                await svc.ExecuteConfirmedBatchAsync(req.ConfirmationId, ct))));

        group.MapGet("/data-center/audit/export", async (
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<AuditExportResponse>.Ok(
                await svc.ExportAuditAsync(
                    start ?? DateTimeOffset.MinValue,
                    end ?? DateTimeOffset.MaxValue,
                    ct))));

        group.MapPost("/data-center/restore/preview", async (
            [FromBody] DataCenterRestoreRequest req,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<RestorePreviewResponse>.Ok(
                await svc.PreviewRestoreAsync(req, ct))));

        group.MapPost("/data-center/restore/request-confirmation", async (
            [FromBody] DataCenterRestoreRequest req,
            [FromServices] DataCenterGovernanceService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(
                await svc.RequestRestoreConfirmationAsync(req, ct))));

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

        group.MapGet("/reports", async (
            [FromServices] ReportService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<ReportArtifactDto>>.Ok(await svc.ListAsync(ct))));

        group.MapPost("/reports/generate", async (
            [FromBody] GenerateReportRequest req,
            [FromServices] ReportService svc,
            CancellationToken ct) =>
        {
            var report = await svc.GenerateAsync(req, ct);
            return Results.Created($"/api/v1/calendar/reports/{report.Id}",
                ApiResponse<ReportArtifactDto>.Ok(report));
        });

        group.MapGet("/reports/{id:guid}", async (
            Guid id,
            [FromServices] ReportService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<ReportArtifactDto>.Ok(await svc.GetAsync(id, ct))));

        group.MapPost("/reports/{id:guid}/archive", async (
            Guid id,
            [FromServices] ReportService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<ReportArtifactDto>.Ok(await svc.ArchiveAsync(id, ct))));

        group.MapPost("/reports/suggestions/{id:guid}/request-action", async (
            Guid id,
            [FromServices] ReportService svc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(
                await svc.RequestSuggestionActionAsync(id, ct))));

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
            [FromQuery] string? scope,
            [FromQuery] string? recurrenceId,
            [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            string? normalizedRecurrenceId = recurrenceId;
            if (!string.IsNullOrEmpty(recurrenceId))
            {
                if (!DateTimeOffset.TryParse(recurrenceId, out var parsed))
                    throw new DomainException(02009, "RecurrenceId 格式无效");
                normalizedRecurrenceId = parsed.ToString("O");
                if (!string.IsNullOrEmpty(req.RecurrenceId) && !string.Equals(req.RecurrenceId, normalizedRecurrenceId, StringComparison.Ordinal) && !string.Equals(req.RecurrenceId, recurrenceId, StringComparison.Ordinal))
                    throw new DomainException(02009, "RecurrenceId 与查询参数不一致");
                if (string.IsNullOrEmpty(req.RecurrenceId))
                    req = req with { RecurrenceId = normalizedRecurrenceId };
                else if (!DateTimeOffset.TryParse(req.RecurrenceId, out var bodyParsed))
                    throw new DomainException(02009, "RecurrenceId 格式无效");
                else
                    req = req with { RecurrenceId = bodyParsed.ToString("O") };
            }
            else if (!string.IsNullOrEmpty(req.RecurrenceId))
            {
                if (!DateTimeOffset.TryParse(req.RecurrenceId, out var bodyParsed2))
                    throw new DomainException(02009, "RecurrenceId 格式无效");
                req = req with { RecurrenceId = bodyParsed2.ToString("O") };
            }
            return Results.Ok(ApiResponse<EventResponse>.Ok(await svc.UpdateEventAsync(id, req, scope, ct)));
        });

        group.MapDelete("/events/{id:guid}", async (
            Guid id,
            [FromQuery] string? scope,
            [FromQuery] string? recurrenceId,
            [FromServices] CalendarService svc, CancellationToken ct) =>
        {
            await svc.DeleteEventAsync(id, scope, recurrenceId, ct);
            return Results.Ok(ApiResponse<string>.Ok("已删除"));
        });

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

        group.MapGet("/events/{eventId:guid}/attachments/{attachmentId}/download", async (
            Guid eventId,
            string attachmentId,
            [FromServices] EventAttachmentService svc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.NotFound();

            try
            {
                var download = await svc.DownloadOutlookAttachmentAsync(userId, eventId, attachmentId, ct);
                return download is null
                    ? Results.NotFound()
                    : Results.File(download.Content, download.ContentType, download.FileName);
            }
            catch (OutlookReauthenticationRequiredException)
            {
                return Results.Conflict(ApiResponse<string>.Error(02009, "Outlook 连接需要重新授权。"));
            }
        });

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

        // Outlook (rewired to new lightweight chain)
        group.MapGet("/outlook/settings", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            return Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(MapSettings(connection)));
        });

        group.MapPut("/outlook/settings", async (
            [FromBody] UpdateOutlookClientIdRequest req,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (connection is null)
            {
                connection = new OutlookConnectionEntity { UserId = userId };
                db.Set<OutlookConnectionEntity>().Add(connection);
            }

            connection.ClientId = req.ClientId.ToString("D");
            connection.TenantId = "common";
            connection.Authority = "https://login.microsoftonline.com/common";
            connection.Scopes = "Calendars.ReadWrite offline_access User.Read openid profile";
            connection.Provider = "outlook";
            connection.Status = string.IsNullOrWhiteSpace(connection.Status) ? "not-connected" : connection.Status;
            connection.TokenHealth = string.IsNullOrWhiteSpace(connection.TokenHealth) ? "missing" : connection.TokenHealth;
            connection.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(MapSettings(connection)));
        });

        group.MapPost("/outlook/device-code", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookAuthorizationSessionRunner runner,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct)
                ?? throw new DomainException(02005, "Outlook is not connected.");
            if (string.IsNullOrWhiteSpace(connection.ClientId))
                throw new DomainException(02005, "Microsoft Client ID is not configured.");

            var session = new OutlookAuthorizationSessionEntity
            {
                UserId = userId,
                ConnectionId = connection.Id,
                Status = "starting"
            };
            db.Set<OutlookAuthorizationSessionEntity>().Add(session);
            await db.SaveChangesAsync(ct);

            var result = await runner.StartAsync(session.Id, userId, ct);
            return Results.Ok(ApiResponse<OutlookAuthorizationSessionResponse>.Ok(ToSessionResponse(result)));
        });

        group.MapPost("/outlook/device-code/poll", async (
            [FromBody] OutlookAuthorizationSessionRequest req,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var session = await db.Set<OutlookAuthorizationSessionEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == req.SessionId && s.UserId == userId, ct);
            if (session is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "Session not found."));

            return Results.Ok(ApiResponse<OutlookAuthorizationSessionResponse>.Ok(ToSessionResponse(session)));
        });

        group.MapPost("/outlook/device-code/{sessionId:guid}/cancel", async (
            Guid sessionId,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookAuthorizationSessionRunner runner,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            try
            {
                await runner.CancelAsync(sessionId, userId, ct);
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "Session not found."));
            }
            return Results.Ok(ApiResponse<string>.Ok("已取消"));
        });

        group.MapPost("/outlook/check", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] GraphCalendarClient graph,
            [FromServices] OutlookCalendarSyncService syncSvc,
            [FromServices] TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (connection is null)
                return Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(MapSettings(null)));

            try
            {
                var me = await graph.GetMeAsync(connection.Id, ct);
                await syncSvc.DiscoverAsync(userId, ct);
                connection.Status = "connected";
                connection.TokenHealth = "healthy";
                connection.LastError = null;
            }
            catch (OutlookReauthenticationRequiredException)
            {
                connection.Status = "reauth-required";
                connection.TokenHealth = "interaction-required";
            }
            catch (GraphRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                connection.Status = "reauth-required";
                connection.TokenHealth = "interaction-required";
            }

            connection.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<OutlookSettingsResponse>.Ok(MapSettings(connection)));
        });

        group.MapPost("/outlook/calendars/discover", async (
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookCalendarSyncService syncSvc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<OutlookCalendarBindingResponse>>.Ok(
                await syncSvc.DiscoverAsync(currentUser.UserId!.Value, ct))));

        group.MapPut("/outlook/calendars/selection", async (
            [FromBody] UpdateCalendarSelectionRequest req,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookCalendarSyncService syncSvc,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            await syncSvc.SetSelectionAsync(userId, req.SelectedBindingIds, ct);
            var bindings = await syncSvc.ListCalendarsAsync(userId, ct);
            return Results.Ok(ApiResponse<IReadOnlyList<OutlookCalendarBindingResponse>>.Ok(bindings));
        });

        group.MapPost("/outlook/sync", async (
            [FromBody] OutlookSyncRequest req,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookCalendarSyncService syncSvc,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<OutlookSyncBatchResponse>.Ok(
                await syncSvc.SyncAsync(currentUser.UserId!.Value, req, ct))));

        group.MapPost("/outlook/sync/{batchId:guid}/cancel", async (
            Guid batchId,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var batch = await db.Set<OutlookSyncBatchEntity>()
                .FirstOrDefaultAsync(b => b.Id == batchId && b.UserId == userId && b.Status == "running", ct);
            if (batch is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "Batch not found or not running."));

            batch.CancelRequested = true;
            batch.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<string>.Ok("已取消"));
        });

        group.MapGet("/outlook/sync/batches", async (
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var query = db.Set<OutlookSyncBatchEntity>()
                .AsNoTracking()
                .Where(b => b.UserId == userId)
                .OrderByDescending(b => b.StartedAt);

            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);
            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync(ct);

            return Results.Ok(ApiResponse<object>.Ok(new
            {
                items = items.Select(MapBatch),
                total,
                page = p,
                pageSize = ps
            }));
        });

        group.MapPost("/outlook/events/writeback", async (
            [FromBody] OutlookWriteRequest req,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] OutlookEventWriteService writeSvc,
            CancellationToken ct) =>
        {
            var result = await writeSvc.ExecuteAsync(currentUser.UserId!.Value, req, ct);
            if (result.Status == "conflict" || result.ErrorCode == "CONFLICT")
                return Results.Conflict(ApiResponse<OutlookWriteResult>.Ok(result));
            return Results.Ok(ApiResponse<OutlookWriteResult>.Ok(result));
        });

        group.MapPost("/outlook/disconnect", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (connection is not null)
            {
                // Request cancellation of running non-writeback sync batches
                var runningBatches = await db.Set<OutlookSyncBatchEntity>()
                    .Where(b => b.UserId == userId && b.Status == "running" && b.Mode != "writeback")
                    .ToListAsync(ct);
                var now = timeProvider.GetUtcNow();
                foreach (var batch in runningBatches)
                {
                    batch.CancelRequested = true;
                    batch.UpdatedAt = now;
                }

                // Clear encrypted cache
                connection.AccessTokenEncrypted = [];
                connection.MsalCacheEncrypted = null;
                connection.RefreshTokenEncrypted = null;
                connection.Status = "not-connected";
                connection.TokenHealth = "missing";
                connection.HomeAccountId = null;
                connection.AccountDisplayName = null;
                connection.AccountLoginHint = null;
                connection.LastSyncedAt = null;
                connection.LastError = null;
                connection.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(ApiResponse<string>.Ok("已断开"));
        });

        group.MapGet("/outlook/local-data/preview", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (connection is null)
                return Results.Ok(ApiResponse<OutlookLocalDataPreview>.Ok(new(0, 0, 0)));

            var bindingCount = await db.Set<OutlookCalendarBindingEntity>()
                .CountAsync(b => b.ConnectionId == connection.Id, ct);
            var calendarCount = await db.Set<OutlookCalendarBindingEntity>()
                .Where(b => b.ConnectionId == connection.Id)
                .Join(db.Set<CalendarEntity>().IgnoreQueryFilters(),
                    b => b.PimCalendarId, c => c.Id,
                    (_, c) => c)
                .CountAsync(c => c.DeletedAt == null, ct);
            var eventCount = await db.Set<OutlookCalendarBindingEntity>()
                .Where(b => b.ConnectionId == connection.Id)
                .Join(db.Set<EventEntity>().IgnoreQueryFilters(),
                    b => b.PimCalendarId, e => e.CalendarId,
                    (_, e) => e)
                .CountAsync(e => e.DeletedAt == null && e.Source.StartsWith("outlook"), ct);

            return Results.Ok(ApiResponse<OutlookLocalDataPreview>.Ok(
                new OutlookLocalDataPreview(bindingCount, calendarCount, eventCount)));
        });

        group.MapDelete("/outlook/local-data", async (
            [FromServices] PimDbContext db,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId!.Value;
            var connection = await db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (connection is null)
                return Results.Ok(ApiResponse<string>.Ok("无本地数据"));

            var now = timeProvider.GetUtcNow();

            // Soft-delete Microsoft events
            var msEvents = await db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .Where(e => e.Calendar.UserId == userId && e.Source.StartsWith("outlook"))
                .ToListAsync(ct);
            foreach (var e in msEvents)
            {
                e.DeletedAt = now;
                e.UpdatedAt = now;
            }

            // Soft-delete outlook-sourced calendars
            var msCalendars = await db.Set<CalendarEntity>()
                .IgnoreQueryFilters()
                .Where(c => c.UserId == userId && c.Source == "outlook")
                .ToListAsync(ct);
            foreach (var c in msCalendars)
            {
                c.DeletedAt = now;
                c.UpdatedAt = now;
            }

            // Remove bindings
            var bindings = await db.Set<OutlookCalendarBindingEntity>()
                .Where(b => b.ConnectionId == connection.Id)
                .ToListAsync(ct);
            db.Set<OutlookCalendarBindingEntity>().RemoveRange(bindings);

            // Clear encrypted cache (leave connection row disconnected)
            connection.AccessTokenEncrypted = [];
            connection.MsalCacheEncrypted = null;
            connection.RefreshTokenEncrypted = null;
            connection.Status = "not-connected";
            connection.TokenHealth = "missing";
            connection.HomeAccountId = null;
            connection.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            return Results.Ok(ApiResponse<string>.Ok("已清理"));
        });
    }

    private static OutlookSettingsResponse MapSettings(OutlookConnectionEntity? connection)
    {
        if (connection is null || string.IsNullOrWhiteSpace(connection.ClientId))
            return new OutlookSettingsResponse("outlook", "common", null,
                "Calendars.ReadWrite offline_access User.Read openid profile",
                "not-connected", "missing", null, null, "not-configured");

        var uiStatus = connection.Status switch
        {
            "not-connected" => "failed",
            "waiting-for-user" => "waiting-auth",
            "connected" => "connected",
            "reauth-required" => "reauth-required",
            "failed" => "failed",
            _ => "failed"
        };

        return new OutlookSettingsResponse(
            connection.Provider,
            connection.TenantId,
            connection.ClientId,
            connection.Scopes,
            connection.Status,
            connection.TokenHealth,
            connection.LastSyncedAt,
            connection.LastError,
            uiStatus);
    }

    private static OutlookAuthorizationSessionResponse ToSessionResponse(OutlookAuthorizationSessionEntity s) =>
        new(s.Id, s.Status, s.VerificationUri, s.UserCode, s.ExpiresAt,
            s.AccountDisplayName, s.AccountLoginHint, s.ErrorCode, s.ErrorMessage, null);

    private static OutlookSyncBatchResponse MapBatch(OutlookSyncBatchEntity b)
    {
        var steps = string.IsNullOrEmpty(b.StepsJson) || b.StepsJson == "[]"
            ? Array.Empty<OutlookSyncStep>()
            : JsonSerializer.Deserialize<OutlookSyncStep[]>(b.StepsJson) ?? Array.Empty<OutlookSyncStep>();

        return new OutlookSyncBatchResponse(
            b.Id, b.Provider, b.Status, b.ReadCount, b.CreatedCount, b.UpdatedCount,
            b.ConflictCount, b.ConfirmationCount, b.FailureCount,
            steps, b.ErrorSummary, b.StartedAt, b.FinishedAt,
            b.Mode, b.RequestedWindowStart, b.RequestedWindowEnd,
            b.PerCalendarJson, b.CancelRequested);
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        try
        {
            await serviceProvider.GetRequiredService<OutlookAuthorizationSessionRunner>()
                .FailInterruptedSessionsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            serviceProvider.GetService<ILogger<CalendarModule>>()?.LogWarning(
                exception,
                "Microsoft authorization session cleanup was skipped because the database is unavailable.");
        }

        // Mark interrupted sync batches
        try
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            var now = timeProvider.GetUtcNow();
            var running = await db.Set<OutlookSyncBatchEntity>()
                .Where(b => b.Status == "running")
                .ToListAsync(CancellationToken.None);
            foreach (var batch in running)
            {
                batch.Status = "interrupted";
                batch.FinishedAt = now;
                batch.UpdatedAt = now;
            }

            if (running.Count > 0)
                await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            serviceProvider.GetService<ILogger<CalendarModule>>()?.LogWarning(
                exception,
                "Interrupted batch cleanup was skipped because the database is unavailable.");
        }

        // Schedule recurring job
        try
        {
            var jobClient = serviceProvider.GetService<IBackgroundJobClient>();
            var recurringJobs = serviceProvider.GetService<IRecurringJobManager>();
            var logger = serviceProvider.GetService<ILogger<CalendarModule>>();

            if (jobClient is null || recurringJobs is null)
            {
                logger?.LogWarning(
                    "Background job infrastructure is not available; scheduled sync is disabled.");
            }
            else
            {
                jobClient.Enqueue<OutlookCalendarSyncJob>(j => j.RunAllAsync());
                recurringJobs.AddOrUpdate<OutlookCalendarSyncJob>(
                    "outlook-calendar-sync",
                    j => j.RunAllAsync(),
                    "*/5 * * * *");
            }
        }
        catch (Exception exception)
        {
            serviceProvider.GetService<ILogger<CalendarModule>>()?.LogWarning(
                exception,
                "Failed to schedule the recurring outlook sync job.");
        }
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
    public const string DataCenterBatchPreview = "/api/v1/calendar/data-center/batch/preview";
    public const string DataCenterBatchRequestConfirmation = "/api/v1/calendar/data-center/batch/request-confirmation";
    public const string DataCenterBatchExecute = "/api/v1/calendar/data-center/batch/execute";
    public const string DataCenterAuditExport = "/api/v1/calendar/data-center/audit/export";
    public const string DataCenterRestorePreview = "/api/v1/calendar/data-center/restore/preview";
    public const string DataCenterRestoreRequestConfirmation = "/api/v1/calendar/data-center/restore/request-confirmation";
    public const string OutlookSettings = "/api/v1/calendar/outlook/settings";
    public const string OutlookDeviceCode = "/api/v1/calendar/outlook/device-code";
    public const string OutlookDeviceCodePoll = "/api/v1/calendar/outlook/device-code/poll";
    public const string OutlookSync = "/api/v1/calendar/outlook/sync";
    public const string OutlookSyncBatches = "/api/v1/calendar/outlook/sync/batches";
    public const string OutlookCheck = "/api/v1/calendar/outlook/check";
    public const string OutlookCalendarsDiscover = "/api/v1/calendar/outlook/calendars/discover";
    public const string OutlookCalendarsSelection = "/api/v1/calendar/outlook/calendars/selection";
    public const string OutlookEventsWriteback = "/api/v1/calendar/outlook/events/writeback";
    public const string OutlookDisconnect = "/api/v1/calendar/outlook/disconnect";
    public const string OutlookLocalDataPreview = "/api/v1/calendar/outlook/local-data/preview";
    public const string OutlookLocalDataDelete = "/api/v1/calendar/outlook/local-data";
    public const string Reports = "/api/v1/calendar/reports";
    public const string GenerateReport = "/api/v1/calendar/reports/generate";
    public const string RequestReportSuggestionAction = "/api/v1/calendar/reports/suggestions/{id}/request-action";

    public static string TaskPlan(string id) => $"{Root}/tasks/{id}/plan";
    public static string EventAttachmentDownload(Guid eventId, string attachmentId) =>
        $"{Root}/events/{eventId}/attachments/{attachmentId}/download";
    public static string RecycleRestorePreview(string type, string id) => $"{RecycleBin}/{type}/{id}/restore-preview";
    public static string RecycleRestore(string type, string id) => $"{RecycleBin}/{type}/{id}/restore";
    public static string OutlookDeviceCodeCancel(Guid sessionId) => $"{OutlookDeviceCode}/{sessionId}/cancel";
    public static string OutlookSyncCancel(Guid batchId) => $"{OutlookSync}/{batchId}/cancel";
}
