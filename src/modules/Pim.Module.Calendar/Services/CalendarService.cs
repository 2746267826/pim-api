using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class CalendarService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public CalendarService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Not authenticated");

    // --- Calendars ---
    public async Task<List<CalendarResponse>> GetCalendarsAsync(CancellationToken ct)
    {
        return await _db.Set<CalendarEntity>()
            .Where(c => c.UserId == UserId)
            .Select(c => new CalendarResponse(c.Id, c.Name, c.Color, c.IsDefault,
                c.Events.Count))
            .ToListAsync(ct);
    }

    public async Task<CalendarResponse> CreateCalendarAsync(CreateCalendarRequest request, CancellationToken ct)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = request.Name,
            Color = request.Color ?? "#3B82F6",
            IsDefault = !await _db.Set<CalendarEntity>().AnyAsync(c => c.UserId == UserId, ct)
        };
        _db.Set<CalendarEntity>().Add(calendar);
        await _db.SaveChangesAsync(ct);
        return new CalendarResponse(calendar.Id, calendar.Name, calendar.Color, calendar.IsDefault, 0);
    }

    // --- Events ---
    public async Task<List<EventResponse>> GetEventsAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        return await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId &&
                        e.DtStart < end && e.DtEnd > start)
            .OrderBy(e => e.DtStart)
            .Select(e => new EventResponse(
                e.Id, e.CalendarId, e.Uid, e.Title, e.Description,
                e.Location, e.DtStart, e.DtEnd, e.RRule, e.Status, e.Source))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<EventResponse>> GetEventsPagedAsync(
        string? search, Guid? calendarId,
        DateTimeOffset? start, DateTimeOffset? end,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
            throw new DomainException(01002, "Not authenticated");

        var query = _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == _currentUser.UserId.Value);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(e => e.Title.Contains(search));
        if (calendarId.HasValue)
            query = query.Where(e => e.CalendarId == calendarId.Value);
        if (start.HasValue)
            query = query.Where(e => e.DtEnd >= start.Value);
        if (end.HasValue)
            query = query.Where(e => e.DtStart <= end.Value);

        var totalCount = await query.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var items = await query
            .OrderByDescending(e => e.DtStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => MapEvent(e))
            .ToListAsync(ct);

        return new PagedResult<EventResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<EventResponse> CreateEventAsync(CreateEventRequest request, CancellationToken ct)
    {
        var calendar = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == request.CalendarId && c.UserId == UserId, ct)
            ?? throw new DomainException(02003, "Calendar not found");

        var entity = new EventEntity
        {
            CalendarId = request.CalendarId,
            Uid = Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            DtStart = request.DtStart,
            DtEnd = request.DtEnd,
            RRule = request.RRule
        };

        _db.Set<EventEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);

        return MapEvent(entity);
    }

    public async Task<EventResponse> UpdateEventAsync(Guid id, CreateEventRequest request, CancellationToken ct)
    {
        var entity = await _db.Set<EventEntity>()
            .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "Event not found");

        entity.Title = request.Title;
        entity.Description = request.Description;
        entity.Location = request.Location;
        entity.DtStart = request.DtStart;
        entity.DtEnd = request.DtEnd;
        entity.RRule = request.RRule;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return MapEvent(entity);
    }

    public async Task<List<EventEntity>> GetEventEntitiesAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        return await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId &&
                        e.DtStart < end && e.DtEnd > start)
            .OrderBy(e => e.DtStart)
            .ToListAsync(ct);
    }

    public async Task DeleteEventAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<EventEntity>()
            .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "Event not found");

        entity.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // --- Tasks ---
    public async Task<List<TaskResponse>> GetTasksAsync(bool? inbox, CancellationToken ct)
    {
        var query = _db.Set<TaskEntity>()
            .Where(t => t.UserId == UserId);

        if (inbox.HasValue)
            query = query.Where(t => t.IsInbox == inbox.Value);

        var tasks = await query.OrderBy(t => t.SortOrder).ToListAsync(ct);
        return tasks.Select(MapTask).ToList();
    }

    public async Task<TaskResponse> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            CalendarId = request.CalendarId,
            Uid = Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            Due = request.Due,
            EstimatedDuration = ParseDuration(request.EstimatedDuration),
            MinimumSegment = ParseDuration(request.MinimumSegment),
            IsInbox = request.CalendarId is null && !request.DtStart.HasValue,
            DtStart = request.DtStart
        };

        _db.Set<TaskEntity>().Add(task);
        await _db.SaveChangesAsync(ct);
        return MapTask(task);
    }

    public async Task MoveTaskAsync(Guid id, MoveTaskRequest request, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>().FindAsync(new object[] { id }, ct)
            ?? throw new DomainException(02004, "Task not found");

        if (request.ScheduledStart.HasValue)
        {
            task.DtStart = request.ScheduledStart;
            task.IsInbox = false;
        }

        if (request.NewSortOrder.HasValue)
            task.SortOrder = request.NewSortOrder.Value;

        task.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteTaskAsync(Guid id, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "Task not found");

        task.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static EventResponse MapEvent(EventEntity e) =>
        new(e.Id, e.CalendarId, e.Uid, e.Title, e.Description,
            e.Location, e.DtStart, e.DtEnd, e.RRule, e.Status, e.Source);

    private static string? FormatDuration(TimeSpan? duration) =>
        duration is not null ? duration.Value.ToString("c") : null;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (value is null) return null;
        if (TimeSpan.TryParse(value, out var result)) return result;
        throw new DomainException(02009, $"Invalid duration format: {value}. Use ISO 8601 format (e.g., PT1H30M).");
    }

    private static TaskResponse MapTask(TaskEntity t) =>
        new(t.Id, t.CalendarId, t.Uid, t.Title, t.Description,
            t.Priority,
            FormatDuration(t.EstimatedDuration),
            FormatDuration(t.MinimumSegment),
            t.DtStart, t.Due, t.Status, t.IsInbox, t.SortOrder,
            t.SubTasks.Select(MapTask).ToList());
}
