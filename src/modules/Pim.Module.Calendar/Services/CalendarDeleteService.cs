using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class CalendarDeleteService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly CalendarAuditWriter _audit;

    public CalendarDeleteService(PimDbContext db, ICurrentUserService currentUser, CalendarAuditWriter audit)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "Not authenticated");

    public async Task<CalendarDeletePreviewResponse> PreviewCalendarDeleteAsync(Guid calendarId, CancellationToken ct = default)
    {
        var calendar = await LoadCalendarAsync(calendarId, ct);
        var operationKind = CalendarOperationKind(calendar);
        var samples = await LoadCalendarChildSamplesAsync(calendar, 5, ct);
        var affectedCount = await CountCalendarChildrenAsync(calendar, ct);

        return new CalendarDeletePreviewResponse(
            calendar.Kind == "task" ? "task-book" : "calendar-book",
            calendar.Id,
            calendar.Name,
            operationKind,
            affectedCount,
            samples,
            $"Delete {calendar.Name} and {affectedCount} active {(calendar.Kind == "task" ? "task" : "event")}{(affectedCount == 1 ? string.Empty : "s")}.",
            true);
    }

    public async Task<CalendarOperationResult> DeleteCalendarAsync(Guid calendarId, CancellationToken ct = default)
    {
        var calendar = await LoadCalendarAsync(calendarId, ct);
        var operationId = Guid.NewGuid();
        var operationKind = CalendarOperationKind(calendar);
        var deletedAt = DateTimeOffset.UtcNow;
        var childSamples = await LoadCalendarChildSamplesAsync(calendar, 5, ct);
        var affectedIds = new List<Guid> { calendar.Id };

        calendar.DeletedAt = deletedAt;
        calendar.DeletedByOperationId = operationId;
        calendar.DeletedByOperationKind = operationKind;
        calendar.UpdatedAt = deletedAt;

        if (calendar.Kind == "task")
        {
            var tasks = await _db.Set<TaskEntity>()
                .Where(t => t.UserId == UserId && t.CalendarId == calendar.Id)
                .ToListAsync(ct);

            foreach (var task in tasks)
            {
                MarkDeleted(task, deletedAt, operationId, operationKind);
                affectedIds.Add(task.Id);
            }
        }
        else
        {
            var events = await _db.Set<EventEntity>()
                .Include(e => e.Calendar)
                .Where(e => e.CalendarId == calendar.Id && e.Calendar.UserId == UserId)
                .ToListAsync(ct);

            foreach (var evt in events)
            {
                MarkDeleted(evt, deletedAt, operationId, operationKind);
                affectedIds.Add(evt.Id);
            }
        }

        await _db.SaveChangesAsync(ct);
        await _audit.RecordSuccessAsync(
            UserId,
            "calendar.books.delete",
            calendar.Kind == "task" ? "task_book" : "calendar_book",
            calendar.Id,
            Metadata(operationId, operationKind, calendar.Name, affectedIds.Count),
            ct);

        return new CalendarOperationResult(
            "calendar.books.delete",
            operationId,
            affectedIds.Count,
            affectedIds,
            childSamples,
            $"Deleted {calendar.Name}.");
    }

    public async Task<CalendarOperationResult> DeleteEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var evt = await LoadEventAsync(eventId, ct);
        var operationId = Guid.NewGuid();
        var deletedAt = DateTimeOffset.UtcNow;
        var operationKind = "single-event";

        MarkDeleted(evt, deletedAt, operationId, operationKind);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordSuccessAsync(
            UserId,
            "calendar.events.delete",
            "calendar_event",
            evt.Id,
            Metadata(operationId, operationKind, evt.Title, 1),
            ct);

        return Result("calendar.events.delete", operationId, new[] { evt }, "Deleted event.");
    }

    public async Task<CalendarOperationResult> BatchDeleteEventsAsync(IEnumerable<Guid>? ids, CancellationToken ct = default)
    {
        var idSet = NormalizeIds(ids);
        var operationId = Guid.NewGuid();
        var operation = "calendar.events.batch_delete";
        if (idSet.Count == 0)
            return EmptyResult(operation, operationId, "No events deleted.");

        var events = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .Where(e => idSet.Contains(e.Id) && e.Calendar.UserId == UserId)
            .OrderBy(e => e.DtStart)
            .ToListAsync(ct);
        if (events.Count == 0)
            return EmptyResult(operation, operationId, "No events deleted.");

        var deletedAt = DateTimeOffset.UtcNow;
        var operationKind = "batch-event";

        foreach (var evt in events)
            MarkDeleted(evt, deletedAt, operationId, operationKind);

        if (events.Count > 0)
            await _db.SaveChangesAsync(ct);

        await _audit.RecordSuccessAsync(
            UserId,
            operation,
            "calendar_event",
            operationId,
            Metadata(operationId, operationKind, null, events.Count),
            ct);

        return Result(operation, operationId, events, "Deleted events.");
    }

    public async Task<CalendarOperationResult> DeleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await LoadTaskAsync(taskId, ct);
        var operationId = Guid.NewGuid();
        var deletedAt = DateTimeOffset.UtcNow;
        var operationKind = "single-task";

        MarkDeleted(task, deletedAt, operationId, operationKind);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordSuccessAsync(
            UserId,
            "calendar.tasks.delete",
            "calendar_task",
            task.Id,
            Metadata(operationId, operationKind, task.Title, 1),
            ct);

        return Result("calendar.tasks.delete", operationId, new[] { task }, "Deleted task.");
    }

    public async Task<CalendarOperationResult> BatchDeleteTasksAsync(IEnumerable<Guid>? ids, CancellationToken ct = default)
    {
        var idSet = NormalizeIds(ids);
        var operationId = Guid.NewGuid();
        var operation = "calendar.tasks.batch_delete";
        if (idSet.Count == 0)
            return EmptyResult(operation, operationId, "No tasks deleted.");

        var tasks = await _db.Set<TaskEntity>()
            .Include(t => t.Calendar)
            .Where(t => idSet.Contains(t.Id) && t.UserId == UserId)
            .OrderBy(t => t.Title)
            .ToListAsync(ct);
        if (tasks.Count == 0)
            return EmptyResult(operation, operationId, "No tasks deleted.");

        var deletedAt = DateTimeOffset.UtcNow;
        var operationKind = "batch-task";

        foreach (var task in tasks)
            MarkDeleted(task, deletedAt, operationId, operationKind);

        if (tasks.Count > 0)
            await _db.SaveChangesAsync(ct);

        await _audit.RecordSuccessAsync(
            UserId,
            operation,
            "calendar_task",
            operationId,
            Metadata(operationId, operationKind, null, tasks.Count),
            ct);

        return Result(operation, operationId, tasks, "Deleted tasks.");
    }

    private async Task<CalendarEntity> LoadCalendarAsync(Guid calendarId, CancellationToken ct)
        => await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "Calendar not found");

    private async Task<EventEntity> LoadEventAsync(Guid eventId, CancellationToken ct)
        => await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "Event not found");

    private async Task<TaskEntity> LoadTaskAsync(Guid taskId, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .Include(t => t.Calendar)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "Task not found");

    private async Task<int> CountCalendarChildrenAsync(CalendarEntity calendar, CancellationToken ct)
    {
        if (calendar.Kind == "task")
        {
            return await _db.Set<TaskEntity>()
                .CountAsync(t => t.UserId == UserId && t.CalendarId == calendar.Id, ct);
        }

        return await _db.Set<EventEntity>()
            .CountAsync(e => e.CalendarId == calendar.Id && e.Calendar.UserId == UserId, ct);
    }

    private async Task<IReadOnlyList<CalendarOperationSample>> LoadCalendarChildSamplesAsync(
        CalendarEntity calendar,
        int take,
        CancellationToken ct)
    {
        if (calendar.Kind == "task")
        {
            return await _db.Set<TaskEntity>()
                .Where(t => t.UserId == UserId && t.CalendarId == calendar.Id)
                .OrderBy(t => t.Title)
                .Take(take)
                .Select(t => new CalendarOperationSample(
                    t.Id,
                    "task",
                    t.Title,
                    t.DtStart,
                    t.PlannedEnd,
                    calendar.Name))
                .ToListAsync(ct);
        }

        return await _db.Set<EventEntity>()
            .Where(e => e.CalendarId == calendar.Id && e.Calendar.UserId == UserId)
            .OrderBy(e => e.DtStart)
            .Take(take)
            .Select(e => new CalendarOperationSample(
                e.Id,
                "event",
                e.Title,
                e.DtStart,
                e.DtEnd,
                calendar.Name))
            .ToListAsync(ct);
    }

    private static string CalendarOperationKind(CalendarEntity calendar)
        => calendar.Kind == "task" ? "task-book" : "calendar-book";

    private static void MarkDeleted(EventEntity evt, DateTimeOffset deletedAt, Guid operationId, string operationKind)
    {
        evt.DeletedAt = deletedAt;
        evt.DeletedByOperationId = operationId;
        evt.DeletedByOperationKind = operationKind;
        evt.UpdatedAt = deletedAt;
    }

    private static void MarkDeleted(TaskEntity task, DateTimeOffset deletedAt, Guid operationId, string operationKind)
    {
        task.DeletedAt = deletedAt;
        task.DeletedByOperationId = operationId;
        task.DeletedByOperationKind = operationKind;
        task.UpdatedAt = deletedAt;
    }

    private static CalendarOperationResult Result(
        string operation,
        Guid operationId,
        IReadOnlyList<EventEntity> events,
        string message)
        => new(
            operation,
            operationId,
            events.Count,
            events.Select(e => e.Id).ToList(),
            events.Take(5).Select(e => new CalendarOperationSample(
                e.Id,
                "event",
                e.Title,
                e.DtStart,
                e.DtEnd,
                e.Calendar.Name)).ToList(),
            message);

    private static CalendarOperationResult Result(
        string operation,
        Guid operationId,
        IReadOnlyList<TaskEntity> tasks,
        string message)
        => new(
            operation,
            operationId,
            tasks.Count,
            tasks.Select(t => t.Id).ToList(),
            tasks.Take(5).Select(t => new CalendarOperationSample(
                t.Id,
                "task",
                t.Title,
                t.DtStart,
                t.PlannedEnd,
                t.Calendar?.Name)).ToList(),
            message);

    private static CalendarOperationResult EmptyResult(string operation, Guid operationId, string message)
        => new(operation, operationId, 0, Array.Empty<Guid>(), Array.Empty<CalendarOperationSample>(), message);

    private static IReadOnlyList<Guid> NormalizeIds(IEnumerable<Guid>? ids)
        => ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new List<Guid>();

    private static IReadOnlyDictionary<string, string> Metadata(
        Guid operationId,
        string operationKind,
        string? title,
        int affectedCount)
    {
        var metadata = new Dictionary<string, string>
        {
            ["operationId"] = operationId.ToString(),
            ["operationKind"] = operationKind,
            ["affectedCount"] = affectedCount.ToString()
        };

        if (!string.IsNullOrWhiteSpace(title))
            metadata["title"] = title;

        return metadata;
    }
}
