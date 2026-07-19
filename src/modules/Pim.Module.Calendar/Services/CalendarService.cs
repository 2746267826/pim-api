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

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "未登录");

    // --- Calendars ---
    public async Task<List<CalendarResponse>> GetCalendarsAsync(string? kind, CancellationToken ct)
    {
        var calendarsQuery = _db.Set<CalendarEntity>()
            .Where(c => c.UserId == UserId);

        if (kind is not null)
            calendarsQuery = calendarsQuery.Where(c => c.Kind == kind);

        var query =
            from calendar in calendarsQuery
            join binding in _db.Set<OutlookCalendarBindingEntity>()
                on calendar.Id equals binding.PimCalendarId into bindingGroup
            from binding in bindingGroup.DefaultIfEmpty()
            select new CalendarResponse(
                calendar.Id, calendar.Name, calendar.Color, calendar.Kind,
                calendar.IsDefault, calendar.Events.Count, calendar.Source,
                binding == null ? null : binding.Id,
                binding == null || binding.CanEdit);

        return await query.ToListAsync(ct);
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
            ?? throw new DomainException(02002, "日历不存在");
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
            ?? throw new DomainException(02002, "日历不存在");
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
                ?? throw new DomainException(02003, "日历不存在");

        var hasOutlookBinding = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => b.PimCalendarId == calendar.Id, ct);
        if (hasOutlookBinding)
            throw new DomainException(02009, "Microsoft 日历的日程必须通过确认写回流程创建。");

        var (normalizedStart, normalizedEnd) = NormalizeAndValidateEventRange(request.DtStart, request.DtEnd);

        var entity = new EventEntity
        {
            CalendarId = calendar.Id,
            Uid = request.Uid ?? Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            DtStart = normalizedStart,
            DtEnd = normalizedEnd,
            RRule = request.RRule,
            IsAllDay = request.IsAllDay,
            TimeZoneId = request.TimeZoneId
        };

        _db.Set<EventEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);

        return MapEvent(entity);
    }

    public async Task<ImportReport> ImportOutlookIcsAsync(
        string icsContent,
        Guid? targetCalendarId,
        OutlookIcsService outlookIcs,
        CancellationToken ct = default)
    {
        var parsed = outlookIcs.Parse(icsContent);
        if (parsed.ErrorReason is not null)
        {
            return new ImportReport(
                0,
                1,
                new Dictionary<string, int> { [parsed.ErrorReason] = 1 },
                new List<ImportSkippedItem>
                {
                    new(parsed.ErrorReason, "Outlook ICS import", null, null)
                });
        }

        CalendarEntity? calendar = null;
        if (targetCalendarId.HasValue)
        {
            calendar = await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == targetCalendarId.Value && c.UserId == UserId, ct);
        }

        calendar ??= await GetOrCreateDefaultCalendarAsync("calendar", ct);

        var imported = 0;
        var skipped = 0;
        var reasonCounts = new Dictionary<string, int>();
        var samples = new List<ImportSkippedItem>();
        var acceptedEvents = new List<OutlookIcsParsedEvent>();

        void AddSkipped(string reason, OutlookIcsParsedEvent item)
        {
            skipped++;
            reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
            if (reason.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase))
                reasonCounts["duplicate"] = reasonCounts.GetValueOrDefault("duplicate") + 1;
            if (samples.Count < 10)
                samples.Add(new ImportSkippedItem(reason, item.Title, item.Start, item.Uid));
        }

        foreach (var item in parsed.Events)
        {
            if (item.InvalidReason is not null)
            {
                AddSkipped(item.InvalidReason, item);
                continue;
            }

            if (item.Start == DateTimeOffset.MinValue || item.End == DateTimeOffset.MinValue)
            {
                AddSkipped("invalid_date", item);
                continue;
            }

            var duplicateReason = await FindActiveDuplicateReasonAsync(item, ct);
            duplicateReason ??= FindAcceptedDuplicateReason(item, acceptedEvents);
            if (duplicateReason is not null)
            {
                AddSkipped(duplicateReason, item);
                continue;
            }

            _db.Set<EventEntity>().Add(new EventEntity
            {
                CalendarId = calendar.Id,
                Uid = Truncate(item.Uid, 255) ?? string.Empty,
                SourceUid = Truncate(item.Uid, 255),
                Title = Truncate(item.Title, 255) ?? string.Empty,
                Description = item.Description,
                Location = Truncate(item.Location, 500),
                DtStart = item.Start,
                DtEnd = item.End,
                RRule = item.RRule,
                IsAllDay = item.IsAllDay,
                TimeZoneId = Truncate(item.SourceTimeZoneId, 100),
                SourceTimeZoneId = Truncate(item.SourceTimeZoneId, 100),
                Source = "outlook-ics",
                SourceIcsComponent = item.SourceIcsComponent,
                ExternalMetadataJson = item.ExternalMetadataJson,
                RecurrenceId = Truncate(item.RecurrenceId, 255),
                ExDatesJson = item.ExDatesJson,
                RecurrenceMetadataJson = item.RecurrenceMetadataJson
            });
            acceptedEvents.Add(item);
            imported++;
        }

        if (imported > 0)
            await _db.SaveChangesAsync(ct);

        return new ImportReport(imported, skipped, reasonCounts, samples);
    }

    private async Task<string?> FindActiveDuplicateReasonAsync(OutlookIcsParsedEvent item, CancellationToken ct)
    {
        if (await _db.Set<EventEntity>().AnyAsync(e => e.Calendar.UserId == UserId && e.Uid == item.Uid, ct))
            return "duplicate_uid";

        if (await _db.Set<EventEntity>().AnyAsync(e => e.Calendar.UserId == UserId && e.SourceUid == item.Uid, ct))
            return "duplicate_source_uid";

        if (await _db.Set<EventEntity>().AnyAsync(e =>
                e.Calendar.UserId == UserId &&
                e.Title == item.Title &&
                e.DtStart == item.Start &&
                e.DtEnd == item.End, ct))
            return "duplicate_title_time";

        return null;
    }

    private static string? FindAcceptedDuplicateReason(OutlookIcsParsedEvent item, IReadOnlyList<OutlookIcsParsedEvent> acceptedEvents)
    {
        if (acceptedEvents.Any(e => e.Uid == item.Uid))
            return "duplicate_uid";

        if (acceptedEvents.Any(e => e.Title == item.Title && e.Start == item.Start && e.End == item.End))
            return "duplicate_title_time";

        return null;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

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

    public async Task<EventResponse> UpdateEventAsync(Guid id, UpdateEventRequest request, CancellationToken ct)
    {
        var entity = await _db.Set<EventEntity>()
            .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "日程不存在");

        if (entity.OutlookCalendarBindingId != null)
            throw new DomainException(02009, "Microsoft 日程必须通过确认写回流程修改。");

        var sourceCalendarHasBinding = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => b.PimCalendarId == entity.CalendarId, ct);
        if (sourceCalendarHasBinding)
            throw new DomainException(02009, "Microsoft 日历的日程必须通过确认写回流程修改。");

        if (request.CalendarId != entity.CalendarId)
        {
            var targetCalendarHasBinding = await _db.Set<OutlookCalendarBindingEntity>()
                .AnyAsync(b => b.PimCalendarId == request.CalendarId, ct);
            if (targetCalendarHasBinding)
                throw new DomainException(02009, "目标日历为 Microsoft 日历，移动操作必须通过确认写回流程。");
        }

        var (normalizedStart, normalizedEnd) = NormalizeAndValidateEventRange(request.DtStart, request.DtEnd);

        entity.Title = request.Title;
        entity.Description = request.Description;
        entity.Location = request.Location;
        entity.DtStart = normalizedStart;
        entity.DtEnd = normalizedEnd;

        entity.RRule = request.RRule;
        if (request.IsAllDay.HasValue)
            entity.IsAllDay = request.IsAllDay.Value;
        if (request.TimeZoneId is not null)
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
            ?? throw new DomainException(02001, "日程不存在");

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

    public async Task<PagedResult<TaskResponse>> GetTasksPagedAsync(
        bool? inbox,
        string? search,
        Guid? calendarId,
        string? status,
        int? priority,
        DateTimeOffset? plannedFrom,
        DateTimeOffset? plannedTo,
        DateTimeOffset? dueFrom,
        DateTimeOffset? dueTo,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Set<TaskEntity>()
            .Where(t => t.UserId == UserId);

        if (inbox.HasValue)
            query = query.Where(t => t.IsInbox == inbox.Value);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Title.Contains(search));
        if (calendarId.HasValue)
            query = query.Where(t => t.CalendarId == calendarId.Value);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);
        if (priority.HasValue)
            query = query.Where(t => t.Priority == priority.Value);
        if (plannedFrom.HasValue)
            query = query.Where(t => t.DtStart >= plannedFrom.Value);
        if (plannedTo.HasValue)
            query = query.Where(t => t.DtStart <= plannedTo.Value);
        if (dueFrom.HasValue)
            query = query.Where(t => t.Due >= dueFrom.Value);
        if (dueTo.HasValue)
            query = query.Where(t => t.Due <= dueTo.Value);

        var totalCount = await query.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var tasks = await query
            .OrderBy(t => t.Status == "COMPLETED")
            .ThenBy(t => t.Due == null)
            .ThenBy(t => t.Due)
            .ThenBy(t => t.SortOrder)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<TaskResponse>(
            tasks.Select(MapTask).ToList(),
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    public async Task<TaskResponse> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct)
    {
        var due = NormalizeToUtc(request.Due);
        var dtStart = NormalizeToUtc(request.DtStart);
        var plannedEnd = NormalizeToUtc(request.PlannedEnd);
        var estimatedDuration = ParseEstimatedDuration(request.EstimatedDuration);
        var minimumSegment = ParseDuration(request.MinimumSegment);

        ValidateTaskRange(dtStart, plannedEnd);

        var task = new TaskEntity
        {
            UserId = UserId,
            CalendarId = request.CalendarId,
            Uid = Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            Due = due,
            EstimatedDuration = estimatedDuration,
            MinimumSegment = minimumSegment,
            IsInbox = request.CalendarId is null && !dtStart.HasValue,
            DtStart = dtStart,
            PlannedEnd = plannedEnd
        };

        _db.Set<TaskEntity>().Add(task);
        await _db.SaveChangesAsync(ct);
        return MapTask(task);
    }

    public async Task<TaskResponse> UpdateTaskAsync(Guid id, UpdateTaskRequest request, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

        var due = NormalizeToUtc(request.Due);
        var estimatedDuration = ParseEstimatedDuration(request.EstimatedDuration);
        var minimumSegment = ParseDuration(request.MinimumSegment);

        var finalStart = NormalizeToUtc(request.DtStart);
        var finalEnd = request.PlannedEnd.HasValue
            ? request.PlannedEnd.Value.ToUniversalTime()
            : task.PlannedEnd;

        ValidateTaskRange(finalStart, finalEnd);

        task.Title = request.Title;
        task.Description = request.Description;
        task.Priority = request.Priority;
        task.Due = due;
        task.EstimatedDuration = estimatedDuration;
        task.MinimumSegment = minimumSegment;
        task.DtStart = finalStart;
        if (request.PlannedEnd.HasValue)
            task.PlannedEnd = finalEnd;
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

    public async Task<TaskResponse> PlanTaskAsync(Guid id, PlanTaskRequest request, CancellationToken ct = default)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

        var start = request.PlannedStart.ToUniversalTime();
        var end = request.PlannedEnd?.ToUniversalTime();

        ValidateTaskRange(start, end);

        var estimatedDuration = request.EstimatedDuration is not null
            ? ParseEstimatedDuration(request.EstimatedDuration)
            : task.EstimatedDuration;

        task.DtStart = start;
        task.PlannedEnd = end;
        if (request.EstimatedDuration is not null)
            task.EstimatedDuration = estimatedDuration;
        task.IsInbox = false;
        task.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return MapTask(task);
    }

    public async Task<CalendarOperationResult> BatchUpdateTasksAsync(
        BatchTaskUpdateRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? new List<Guid>();
        var operationId = Guid.NewGuid();

        if (ids.Count == 0)
        {
            return new CalendarOperationResult(
                "calendar.tasks.batch_update",
                operationId,
                0,
                Array.Empty<Guid>(),
                Array.Empty<CalendarOperationSample>(),
                "没有更新任务");
        }

        if (request.Status is null && !request.Priority.HasValue && !request.CalendarId.HasValue)
        {
            return new CalendarOperationResult(
                "calendar.tasks.batch_update",
                operationId,
                0,
                Array.Empty<Guid>(),
                Array.Empty<CalendarOperationSample>(),
                "没有更新任务");
        }

        CalendarEntity? targetCalendar = null;
        if (request.CalendarId.HasValue)
        {
            targetCalendar = await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId.Value && c.UserId == UserId, ct)
                ?? throw new DomainException(02003, "日历不存在");
        }

        var tasks = await _db.Set<TaskEntity>()
            .Include(t => t.Calendar)
            .Where(t => t.UserId == UserId && ids.Contains(t.Id))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var task in tasks)
        {
            if (request.Status is not null)
            {
                task.Status = request.Status;
                task.CompletedAt = request.Status == "COMPLETED" ? now : null;
            }

            if (request.Priority.HasValue)
                task.Priority = request.Priority.Value;

            if (request.CalendarId.HasValue)
            {
                task.CalendarId = targetCalendar!.Id;
                task.Calendar = targetCalendar;
                task.IsInbox = false;
            }

            task.UpdatedAt = now;
        }

        if (tasks.Count > 0)
            await _db.SaveChangesAsync(ct);

        if (tasks.Count == 0)
        {
            return new CalendarOperationResult(
                "calendar.tasks.batch_update",
                operationId,
                0,
                Array.Empty<Guid>(),
                Array.Empty<CalendarOperationSample>(),
                "没有更新任务");
        }

        return new CalendarOperationResult(
            "calendar.tasks.batch_update",
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
            "已更新任务");
    }

    public async Task MoveTaskAsync(Guid id, MoveTaskRequest request, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>().FindAsync(new object[] { id }, ct)
            ?? throw new DomainException(02004, "任务不存在");

        var newStart = request.ScheduledStart?.ToUniversalTime() ?? task.DtStart;
        DateTimeOffset? newEnd;
        if (request.PlannedEnd.HasValue)
            newEnd = request.PlannedEnd.Value.ToUniversalTime();
        else if (request.Duration.HasValue && request.ScheduledStart.HasValue)
            newEnd = request.ScheduledStart.Value.ToUniversalTime().Add(request.Duration.Value);
        else
            newEnd = task.PlannedEnd;

        if (request.ScheduledStart is not null || request.PlannedEnd is not null)
            ValidateTaskRange(newStart, newEnd);

        if (request.ScheduledStart.HasValue)
        {
            task.DtStart = newStart;
            task.IsInbox = false;
        }

        if (request.NewSortOrder.HasValue)
            task.SortOrder = request.NewSortOrder.Value;

        if (request.PlannedEnd.HasValue)
            task.PlannedEnd = newEnd;
        else if (request.Duration.HasValue && request.ScheduledStart.HasValue)
            task.PlannedEnd = newEnd;

        task.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteTaskAsync(Guid id, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

        task.DeletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) NormalizeAndValidateEventRange(
        DateTimeOffset start, DateTimeOffset end)
    {
        var normalizedStart = start.ToUniversalTime();
        var normalizedEnd = end.ToUniversalTime();
        if (normalizedEnd <= normalizedStart)
            throw new DomainException(02010, "结束时间必须晚于开始时间");
        return (normalizedStart, normalizedEnd);
    }

    private static EventResponse MapEvent(EventEntity e) =>
        new(e.Id, e.CalendarId, e.Uid, e.Title, e.Description,
            e.Location, e.DtStart, e.DtEnd, e.RRule, e.Status, e.Source, null,
            e.IsAllDay, e.TimeZoneId, e.SourceTimeZoneId, e.SourceUid,
            e.ExternalMetadataJson, e.RecurrenceId, e.ExDatesJson,
            e.RecurrenceMetadataJson,
            e.OutlookCalendarBindingId, e.OutlookEventId, e.OutlookEtag, e.OutlookEventType);

    private static EventResponse MapExpandedEvent(ExpandedEvent e) =>
        new(e.OccurrenceId, e.Entity.CalendarId, e.Entity.Uid,
            e.Entity.Title, e.Entity.Description,
            e.Entity.Location, e.OccurrenceStart, e.OccurrenceEnd,
            e.Entity.RRule, e.Entity.Status, e.Entity.Source,
            e.Entity.Id, e.Entity.IsAllDay, e.Entity.TimeZoneId,
            e.Entity.SourceTimeZoneId, e.Entity.SourceUid,
            e.Entity.ExternalMetadataJson, e.Entity.RecurrenceId, e.Entity.ExDatesJson,
            e.Entity.RecurrenceMetadataJson,
            e.Entity.OutlookCalendarBindingId, e.Entity.OutlookEventId, e.Entity.OutlookEtag, e.Entity.OutlookEventType);

    private static string? FormatDuration(TimeSpan? duration) =>
        duration is not null ? duration.Value.ToString("c") : null;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (value is null) return null;
        try { return System.Xml.XmlConvert.ToTimeSpan(value); }
        catch { throw new DomainException(02009, $"时长格式无效：{value}。请使用 ISO 8601 格式，例如 PT1H30M。"); }
    }

    private static DateTimeOffset? NormalizeToUtc(DateTimeOffset? dt) =>
        dt?.ToUniversalTime();

    private static void ValidateTaskRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue && end.HasValue && end.Value <= start.Value)
            throw new DomainException(02010, "结束时间必须晚于开始时间");
    }

    private static TimeSpan? ParseEstimatedDuration(string? value)
    {
        var parsed = ParseDuration(value);
        if (parsed.HasValue && parsed.Value < TimeSpan.FromMinutes(1))
            throw new DomainException(02011, "预计时长至少为 1 分钟");
        return parsed;
    }

    private static TaskResponse MapTask(TaskEntity t) =>
        new(t.Id, t.CalendarId, t.Uid, t.Title, t.Description,
            t.Priority,
            FormatDuration(t.EstimatedDuration),
            FormatDuration(t.MinimumSegment),
            t.DtStart, t.Due, t.Status, t.IsInbox, t.SortOrder,
            t.SubTasks.Select(MapTask).ToList(), t.PlannedEnd);
}
