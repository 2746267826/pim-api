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
    private readonly RecurrenceService _recurrence;

    public CalendarService(PimDbContext db, ICurrentUserService currentUser, RecurrenceService recurrence)
    {
        _db = db;
        _currentUser = currentUser;
        _recurrence = recurrence;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Not authenticated");

    // --- Calendars ---
    public async Task<List<CalendarResponse>> GetCalendarsAsync(string? kind, CancellationToken ct)
    {
        var query = _db.Set<CalendarEntity>()
            .Where(c => c.UserId == UserId);

        if (kind is not null)
            query = query.Where(c => c.Kind == kind);

        return await query
            .Select(c => new CalendarResponse(c.Id, c.Name, c.Color, c.Kind, c.IsDefault,
                c.Events.Count))
            .ToListAsync(ct);
    }

    public async Task<CalendarResponse> CreateCalendarAsync(CreateCalendarRequest request, CancellationToken ct)
    {
        var kind = !string.IsNullOrEmpty(request.Kind) ? request.Kind : "calendar";
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = request.Name,
            Color = request.Color ?? "#3B82F6",
            Kind = kind,
            IsDefault = !await _db.Set<CalendarEntity>().AnyAsync(c => c.UserId == UserId && c.Kind == kind, ct)
        };
        _db.Set<CalendarEntity>().Add(calendar);
        await _db.SaveChangesAsync(ct);
        return new CalendarResponse(calendar.Id, calendar.Name, calendar.Color, calendar.Kind, calendar.IsDefault, 0);
    }

    public async Task<CalendarResponse> UpdateCalendarAsync(Guid id, CreateCalendarRequest request, CancellationToken ct)
    {
        var cal = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "Calendar not found");
        cal.Name = request.Name;
        if (request.Color is not null) cal.Color = request.Color;
        cal.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new CalendarResponse(cal.Id, cal.Name, cal.Color, cal.Kind, cal.IsDefault, cal.Events.Count);
    }

    public async Task DeleteCalendarAsync(Guid id, CancellationToken ct)
    {
        var cal = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "Calendar not found");
        cal.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // --- Events ---
    public async Task<List<EventResponse>> GetEventsAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var minValidDate = DateTimeOffset.MinValue.AddYears(100);
        var entities = await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId
                        && e.DtStart > minValidDate
                        && e.DtEnd > minValidDate)
            .AsNoTracking()
            .ToListAsync(ct);

        var expanded = _recurrence.ExpandEvents(entities, start, end);

        return expanded
            .OrderBy(x => x.OccurrenceStart)
            .Select(MapExpandedEvent)
            .ToList();
    }

    public async Task<PagedResult<EventResponse>> GetEventsPagedAsync(
        string? search, Guid? calendarId,
        DateTimeOffset? start, DateTimeOffset? end,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var minValidDate = DateTimeOffset.MinValue.AddYears(100);
        var query = _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId
                        && e.DtStart > minValidDate
                        && e.DtEnd > minValidDate);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(e => e.Title.Contains(search));
        if (calendarId.HasValue)
            query = query.Where(e => e.CalendarId == calendarId.Value);

        var entities = await query.AsNoTracking().ToListAsync(ct);

        var rangeStart = start ?? DateTimeOffset.MinValue;
        var rangeEnd = end ?? DateTimeOffset.MaxValue;
        var expanded = _recurrence.ExpandEvents(entities, rangeStart, rangeEnd);

        var totalCount = expanded.Count;
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var items = expanded
            .OrderByDescending(x => x.OccurrenceStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapExpandedEvent)
            .ToList();

        return new PagedResult<EventResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<EventResponse> CreateEventAsync(CreateEventRequest request, CancellationToken ct)
    {
        var calendar = request.CalendarId == Guid.Empty
            ? await GetOrCreateDefaultCalendarAsync("calendar", ct)
            : await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId && c.UserId == UserId, ct)
                ?? throw new DomainException(02003, "Calendar not found");

        var entity = new EventEntity
        {
            CalendarId = calendar.Id,
            Uid = request.Uid ?? Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            DtStart = request.DtStart,
            DtEnd = request.DtEnd,
            RRule = request.RRule,
            IsAllDay = request.IsAllDay,
            TimeZoneId = request.TimeZoneId
        };

        _db.Set<EventEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);

        return MapEvent(entity);
    }

    private async Task<CalendarEntity> GetOrCreateDefaultCalendarAsync(string kind, CancellationToken ct)
    {
        var calendar = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.UserId == UserId && c.Kind == kind && c.IsDefault, ct)
            ?? await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.UserId == UserId && c.Kind == kind, ct);

        if (calendar is not null)
            return calendar;

        calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = kind == "task" ? "默认任务" : "默认日历",
            Kind = kind,
            Color = "#3B82F6",
            IsDefault = true
        };

        _db.Set<CalendarEntity>().Add(calendar);
        await _db.SaveChangesAsync(ct);
        return calendar;
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
        entity.IsAllDay = request.IsAllDay;
        entity.TimeZoneId = request.TimeZoneId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return MapEvent(entity);
    }

    public async Task<List<EventEntity>> GetEventEntitiesAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var minValidDate = DateTimeOffset.MinValue.AddYears(100);
        return await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId
                        && e.DtStart > minValidDate
                        && e.DtEnd > minValidDate
                        && e.DtStart < end && e.DtEnd > start)
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

    public async Task<int> DeleteEventsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var entities = await _db.Set<EventEntity>()
            .Where(e => ids.Contains(e.Id) && e.Calendar.UserId == UserId)
            .ToListAsync(ct);

        foreach (var entity in entities)
            entity.DeletedAt = DateTimeOffset.UtcNow;

        if (entities.Count > 0)
            await _db.SaveChangesAsync(ct);

        return entities.Count;
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
            DtStart = request.DtStart,
            PlannedEnd = request.PlannedEnd
        };

        _db.Set<TaskEntity>().Add(task);
        await _db.SaveChangesAsync(ct);
        return MapTask(task);
    }

    public async Task<TaskResponse> UpdateTaskAsync(Guid id, CreateTaskRequest request, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "Task not found");

        task.Title = request.Title;
        task.Description = request.Description;
        task.Priority = request.Priority;
        task.Due = request.Due;
        task.EstimatedDuration = ParseDuration(request.EstimatedDuration);
        task.MinimumSegment = ParseDuration(request.MinimumSegment);
        task.DtStart = request.DtStart;
        task.PlannedEnd = request.PlannedEnd;
        task.CalendarId = request.CalendarId;
        if (request.Status is not null)
        {
            task.Status = request.Status;
            if (request.Status == "COMPLETED")
                task.CompletedAt = DateTimeOffset.UtcNow;
        }
        task.UpdatedAt = DateTimeOffset.UtcNow;

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

        if (request.PlannedEnd.HasValue)
            task.PlannedEnd = request.PlannedEnd;
        else if (request.Duration.HasValue && request.ScheduledStart.HasValue)
            task.PlannedEnd = request.ScheduledStart.Value.Add(request.Duration.Value);

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
            e.Location, e.DtStart, e.DtEnd, e.RRule, e.Status, e.Source, null,
            e.IsAllDay, e.TimeZoneId, e.SourceTimeZoneId, e.SourceUid,
            e.SourceIcsComponent, e.ExternalMetadataJson, e.RecurrenceId,
            e.ExDatesJson, e.RecurrenceMetadataJson);

    private static EventResponse MapExpandedEvent(ExpandedEvent e) =>
        new(e.OccurrenceId, e.Entity.CalendarId, e.Entity.Uid,
            e.Entity.Title, e.Entity.Description,
            e.Entity.Location, e.OccurrenceStart, e.OccurrenceEnd,
            e.Entity.RRule, e.Entity.Status, e.Entity.Source,
            e.Entity.Id, e.Entity.IsAllDay, e.Entity.TimeZoneId,
            e.Entity.SourceTimeZoneId, e.Entity.SourceUid,
            e.Entity.SourceIcsComponent, e.Entity.ExternalMetadataJson,
            e.Entity.RecurrenceId, e.Entity.ExDatesJson,
            e.Entity.RecurrenceMetadataJson);

    private static string? FormatDuration(TimeSpan? duration) =>
        duration is not null ? duration.Value.ToString("c") : null;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (value is null) return null;
        try { return System.Xml.XmlConvert.ToTimeSpan(value); }
        catch { throw new DomainException(02009, $"Invalid duration format: {value}. Use ISO 8601 format (e.g., PT1H30M)."); }
    }

    private static TaskResponse MapTask(TaskEntity t) =>
        new(t.Id, t.CalendarId, t.Uid, t.Title, t.Description,
            t.Priority,
            FormatDuration(t.EstimatedDuration),
            FormatDuration(t.MinimumSegment),
            t.DtStart, t.Due, t.Status, t.IsInbox, t.SortOrder,
            t.SubTasks.Select(MapTask).ToList(), t.PlannedEnd);
}
