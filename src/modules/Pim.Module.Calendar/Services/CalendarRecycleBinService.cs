using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class CalendarRecycleBinService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly CalendarAuditWriter _audit;

    public CalendarRecycleBinService(PimDbContext db, ICurrentUserService currentUser, CalendarAuditWriter audit)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "Not authenticated");

    public async Task<PagedResult<CalendarRecycleBinItem>> ListAsync(
        string? type,
        string? search,
        DateTimeOffset? deletedFrom,
        DateTimeOffset? deletedTo,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var normalizedType = NormalizeListType(type);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = new List<CalendarRecycleBinItem>();

        if (normalizedType is "all" or "calendar" or "task-book")
        {
            var calendars = await _db.Set<CalendarEntity>()
                .IgnoreQueryFilters()
                .Where(c => c.UserId == UserId && c.DeletedAt != null)
                .ToListAsync(ct);

            items.AddRange(calendars
                .Where(c => normalizedType == "all"
                    || (normalizedType == "calendar" && c.Kind != "task")
                    || (normalizedType == "task-book" && c.Kind == "task"))
                .Select(c => new CalendarRecycleBinItem(
                    c.Id,
                    c.Kind == "task" ? "task-book" : "calendar",
                    c.Name,
                    c.DeletedAt!.Value,
                    null,
                    null,
                    null,
                    "manual",
                    c.DeletedByOperationId,
                    c.DeletedByOperationKind)));
        }

        if (normalizedType is "all" or "event")
        {
            var events = await _db.Set<EventEntity>()
                .IgnoreQueryFilters()
                .Include(e => e.Calendar)
                .Where(e => e.DeletedAt != null && e.Calendar.UserId == UserId)
                .Select(e => new CalendarRecycleBinItem(
                    e.Id,
                    "event",
                    e.Title,
                    e.DeletedAt!.Value,
                    e.Calendar.Name,
                    e.DtStart,
                    e.DtEnd,
                    e.Source,
                    e.DeletedByOperationId,
                    e.DeletedByOperationKind))
                .ToListAsync(ct);
            items.AddRange(events);
        }

        if (normalizedType is "all" or "task")
        {
            var tasks = await _db.Set<TaskEntity>()
                .IgnoreQueryFilters()
                .Include(t => t.Calendar)
                .Where(t => t.DeletedAt != null && t.UserId == UserId)
                .Select(t => new CalendarRecycleBinItem(
                    t.Id,
                    "task",
                    t.Title,
                    t.DeletedAt!.Value,
                    t.Calendar == null ? null : t.Calendar.Name,
                    t.DtStart,
                    t.PlannedEnd ?? t.Due,
                    "manual",
                    t.DeletedByOperationId,
                    t.DeletedByOperationKind))
                .ToListAsync(ct);
            items.AddRange(tasks);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            items = items
                .Where(i => i.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (i.BookName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (deletedFrom.HasValue)
            items = items.Where(i => i.DeletedAt >= deletedFrom.Value).ToList();
        if (deletedTo.HasValue)
            items = items.Where(i => i.DeletedAt <= deletedTo.Value).ToList();

        var totalCount = items.Count;
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var pageItems = items
            .OrderByDescending(i => i.DeletedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<CalendarRecycleBinItem>(pageItems, page, pageSize, totalCount, totalPages);
    }

    public async Task<CalendarRestorePreviewResponse> PreviewRestoreAsync(string type, Guid id, CancellationToken ct = default)
        => await BuildPreviewAsync(NormalizeType(type), id, ct);

    public async Task<CalendarOperationResult> RestoreAsync(
        string type,
        Guid id,
        CalendarRestoreRequest request,
        CancellationToken ct = default)
    {
        var normalizedType = NormalizeType(type);
        if (request.RestoreAsCopy && normalizedType is "calendar" or "task-book")
            throw new DomainException(02022, "restore-as-copy is only supported for events/tasks.");

        var preview = await BuildPreviewAsync(normalizedType, id, ct);
        if (preview.Conflicts.Count > 0 && !request.RestoreAsCopy)
            throw new DomainException(02020, "Restore has conflicts");

        var operationId = Guid.NewGuid();
        var operation = request.RestoreAsCopy
            ? "calendar.recycle_bin.restore_copy"
            : "calendar.recycle_bin.restore";
        var now = DateTimeOffset.UtcNow;

        var result = normalizedType switch
        {
            "event" => await RestoreEventAsync(id, request.RestoreAsCopy, operation, operationId, now, ct),
            "task" => await RestoreTaskAsync(id, request.RestoreAsCopy, operation, operationId, now, ct),
            "calendar" or "task-book" => await RestoreCalendarAsync(normalizedType, id, operation, operationId, now, ct),
            _ => throw new DomainException(02021, "Unsupported recycle bin type")
        };

        await _db.SaveChangesAsync(ct);
        await _audit.RecordSuccessAsync(
            UserId,
            operation,
            normalizedType == "task-book" ? "task_book" : normalizedType,
            id,
            Metadata(operationId, normalizedType, result.AffectedCount),
            ct);

        return result;
    }

    private async Task<CalendarRestorePreviewResponse> BuildPreviewAsync(string type, Guid id, CancellationToken ct)
    {
        return type switch
        {
            "event" => await BuildEventPreviewAsync(id, ct),
            "task" => await BuildTaskPreviewAsync(id, ct),
            "calendar" or "task-book" => await BuildCalendarPreviewAsync(type, id, ct),
            _ => throw new DomainException(02021, "Unsupported recycle bin type")
        };
    }

    private async Task<CalendarRestorePreviewResponse> BuildEventPreviewAsync(Guid id, CancellationToken ct)
    {
        var evt = await LoadDeletedEventAsync(id, ct);
        var conflicts = await FindEventConflictsAsync(evt, ct);

        return new CalendarRestorePreviewResponse(
            "event",
            evt.Id,
            evt.Title,
            1,
            new[] { Sample(evt) },
            conflicts,
            conflicts.Count == 0);
    }

    private async Task<CalendarRestorePreviewResponse> BuildTaskPreviewAsync(Guid id, CancellationToken ct)
    {
        var task = await LoadDeletedTaskAsync(id, ct);
        var conflicts = await FindTaskConflictsAsync(task, ct);

        return new CalendarRestorePreviewResponse(
            "task",
            task.Id,
            task.Title,
            1,
            new[] { Sample(task) },
            conflicts,
            conflicts.Count == 0);
    }

    private async Task<CalendarRestorePreviewResponse> BuildCalendarPreviewAsync(string type, Guid id, CancellationToken ct)
    {
        var calendar = await LoadDeletedCalendarAsync(type, id, ct);
        var samples = await BuildBookRestoreSamplesAsync(calendar, 5, ct);
        var restoreCount = await CountBookRestoreAsync(calendar, ct) + 1;

        return new CalendarRestorePreviewResponse(
            calendar.Kind == "task" ? "task-book" : "calendar",
            calendar.Id,
            calendar.Name,
            restoreCount,
            samples,
            Array.Empty<CalendarRestoreConflict>(),
            true);
    }

    private async Task<CalendarOperationResult> RestoreEventAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var evt = await LoadDeletedEventAsync(id, ct);
        EnsureParentBookActive(evt.Calendar);

        if (restoreAsCopy)
        {
            evt.Uid = $"{Guid.NewGuid()}@pim";
            evt.SourceUid = null;
        }

        ClearDelete(evt, now);
        return new CalendarOperationResult(
            operation,
            operationId,
            1,
            new[] { evt.Id },
            new[] { Sample(evt) },
            "Restored event.");
    }

    private async Task<CalendarOperationResult> RestoreTaskAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var task = await LoadDeletedTaskAsync(id, ct);
        if (task.Calendar is not null)
            EnsureParentBookActive(task.Calendar);

        if (restoreAsCopy)
            task.Uid = $"{Guid.NewGuid()}@pim";

        ClearDelete(task, now);
        return new CalendarOperationResult(
            operation,
            operationId,
            1,
            new[] { task.Id },
            new[] { Sample(task) },
            "Restored task.");
    }

    private async Task<CalendarOperationResult> RestoreCalendarAsync(
        string type,
        Guid id,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var calendar = await LoadDeletedCalendarAsync(type, id, ct);
        var deletedOperationId = calendar.DeletedByOperationId;
        var samples = await BuildBookRestoreSamplesAsync(calendar, 5, ct);
        var affectedIds = new List<Guid> { calendar.Id };

        ClearDelete(calendar, now);

        if (deletedOperationId.HasValue)
        {
            if (calendar.Kind == "task")
            {
                var tasks = await _db.Set<TaskEntity>()
                    .IgnoreQueryFilters()
                    .Where(t => t.UserId == UserId
                        && t.CalendarId == calendar.Id
                        && t.DeletedAt != null
                        && t.DeletedByOperationId == deletedOperationId)
                    .ToListAsync(ct);

                foreach (var task in tasks)
                {
                    ClearDelete(task, now);
                    affectedIds.Add(task.Id);
                }
            }
            else
            {
                var events = await _db.Set<EventEntity>()
                    .IgnoreQueryFilters()
                    .Include(e => e.Calendar)
                    .Where(e => e.CalendarId == calendar.Id
                        && e.Calendar.UserId == UserId
                        && e.DeletedAt != null
                        && e.DeletedByOperationId == deletedOperationId)
                    .ToListAsync(ct);

                foreach (var evt in events)
                {
                    ClearDelete(evt, now);
                    affectedIds.Add(evt.Id);
                }
            }
        }

        return new CalendarOperationResult(
            operation,
            operationId,
            affectedIds.Count,
            affectedIds,
            samples,
            $"Restored {calendar.Name}.");
    }

    private async Task<EventEntity> LoadDeletedEventAsync(Guid id, CancellationToken ct)
        => await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "Event not found");

    private async Task<TaskEntity> LoadDeletedTaskAsync(Guid id, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .IgnoreQueryFilters()
            .Include(t => t.Calendar)
            .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt != null && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "Task not found");

    private async Task<CalendarEntity> LoadDeletedCalendarAsync(string type, Guid id, CancellationToken ct)
    {
        var calendar = await _db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt != null && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "Calendar not found");

        if (type == "task-book" && calendar.Kind != "task")
            throw new DomainException(02002, "Calendar not found");
        if (type == "calendar" && calendar.Kind == "task")
            throw new DomainException(02002, "Calendar not found");

        return calendar;
    }

    private async Task<IReadOnlyList<CalendarRestoreConflict>> FindEventConflictsAsync(EventEntity deleted, CancellationToken ct)
    {
        var conflicts = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .Where(e => e.Calendar.UserId == UserId
                && e.Id != deleted.Id
                && (e.Uid == deleted.Uid
                    || (deleted.SourceUid != null && e.SourceUid == deleted.SourceUid)
                    || (e.Title == deleted.Title && e.DtStart == deleted.DtStart && e.DtEnd == deleted.DtEnd)))
            .ToListAsync(ct);

        return conflicts
            .Select(e => new CalendarRestoreConflict(
                deleted.Id,
                "event",
                e.Id,
                "event",
                EventConflictReason(deleted, e),
                e.Title))
            .ToList();
    }

    private async Task<IReadOnlyList<CalendarRestoreConflict>> FindTaskConflictsAsync(TaskEntity deleted, CancellationToken ct)
    {
        var conflicts = await _db.Set<TaskEntity>()
            .Where(t => t.UserId == UserId
                && t.Id != deleted.Id
                && t.Title == deleted.Title
                && t.Due == deleted.Due
                && t.DtStart == deleted.DtStart)
            .ToListAsync(ct);

        return conflicts
            .Select(t => new CalendarRestoreConflict(
                deleted.Id,
                "task",
                t.Id,
                "task",
                "same-title-time",
                t.Title))
            .ToList();
    }

    private async Task<IReadOnlyList<CalendarOperationSample>> BuildBookRestoreSamplesAsync(
        CalendarEntity calendar,
        int take,
        CancellationToken ct)
    {
        if (!calendar.DeletedByOperationId.HasValue)
            return Array.Empty<CalendarOperationSample>();

        if (calendar.Kind == "task")
        {
            return await _db.Set<TaskEntity>()
                .IgnoreQueryFilters()
                .Include(t => t.Calendar)
                .Where(t => t.UserId == UserId
                    && t.CalendarId == calendar.Id
                    && t.DeletedAt != null
                    && t.DeletedByOperationId == calendar.DeletedByOperationId)
                .OrderBy(t => t.Title)
                .Take(take)
                .Select(t => new CalendarOperationSample(
                    t.Id,
                    "task",
                    t.Title,
                    t.DtStart,
                    t.PlannedEnd ?? t.Due,
                    calendar.Name))
                .ToListAsync(ct);
        }

        return await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Include(e => e.Calendar)
            .Where(e => e.CalendarId == calendar.Id
                && e.Calendar.UserId == UserId
                && e.DeletedAt != null
                && e.DeletedByOperationId == calendar.DeletedByOperationId)
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

    private async Task<int> CountBookRestoreAsync(CalendarEntity calendar, CancellationToken ct)
    {
        if (!calendar.DeletedByOperationId.HasValue)
            return 0;

        if (calendar.Kind == "task")
        {
            return await _db.Set<TaskEntity>()
                .IgnoreQueryFilters()
                .CountAsync(t => t.UserId == UserId
                    && t.CalendarId == calendar.Id
                    && t.DeletedAt != null
                    && t.DeletedByOperationId == calendar.DeletedByOperationId, ct);
        }

        return await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .CountAsync(e => e.CalendarId == calendar.Id
                && e.Calendar.UserId == UserId
                && e.DeletedAt != null
                && e.DeletedByOperationId == calendar.DeletedByOperationId, ct);
    }

    private static string NormalizeListType(string? type)
        => string.IsNullOrWhiteSpace(type) ? "all" : type.Trim().ToLowerInvariant();

    private static string NormalizeType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "calendar" or "calendar-book" => "calendar",
            "task-book" => "task-book",
            "event" => "event",
            "task" => "task",
            _ => throw new DomainException(02021, "Unsupported recycle bin type")
        };
    }

    private static void ClearDelete(CalendarEntity calendar, DateTimeOffset now)
    {
        calendar.DeletedAt = null;
        calendar.DeletedByOperationId = null;
        calendar.DeletedByOperationKind = null;
        calendar.UpdatedAt = now;
    }

    private static void ClearDelete(EventEntity evt, DateTimeOffset now)
    {
        evt.DeletedAt = null;
        evt.DeletedByOperationId = null;
        evt.DeletedByOperationKind = null;
        evt.UpdatedAt = now;
    }

    private static void ClearDelete(TaskEntity task, DateTimeOffset now)
    {
        task.DeletedAt = null;
        task.DeletedByOperationId = null;
        task.DeletedByOperationKind = null;
        task.UpdatedAt = now;
    }

    private static CalendarOperationSample Sample(EventEntity evt)
        => new(evt.Id, "event", evt.Title, evt.DtStart, evt.DtEnd, evt.Calendar.Name);

    private static CalendarOperationSample Sample(TaskEntity task)
        => new(task.Id, "task", task.Title, task.DtStart, task.PlannedEnd ?? task.Due, task.Calendar?.Name);

    private static string EventConflictReason(EventEntity deleted, EventEntity active)
    {
        if (active.Uid == deleted.Uid)
            return "same-uid";
        if (deleted.SourceUid != null && active.SourceUid == deleted.SourceUid)
            return "same-source-uid";
        return "same-title-time";
    }

    private static void EnsureParentBookActive(CalendarEntity calendar)
    {
        if (calendar.DeletedAt is not null)
            throw new DomainException(02023, "Restore the parent book first.");
    }

    private static IReadOnlyDictionary<string, string> Metadata(Guid operationId, string targetType, int affectedCount)
        => new Dictionary<string, string>
        {
            ["operationId"] = operationId.ToString(),
            ["targetType"] = targetType,
            ["affectedCount"] = affectedCount.ToString()
        };
}
