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
    private readonly EventAttachmentService _attachments;
    private readonly TimeProvider _timeProvider;
    private readonly CalendarAuditWriter? _audit;

    public CalendarService(PimDbContext db, ICurrentUserService currentUser, RecurrenceService recurrence)
        : this(db, currentUser, recurrence, new EventAttachmentService(db), TimeProvider.System, null)
    {
    }

    public CalendarService(
        PimDbContext db,
        ICurrentUserService currentUser,
        RecurrenceService recurrence,
        EventAttachmentService attachments,
        TimeProvider? timeProvider = null,
        CalendarAuditWriter? audit = null)
    {
        _db = db;
        _currentUser = currentUser;
        _recurrence = recurrence;
        _attachments = attachments;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _audit = audit;
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
        cal.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return new CalendarResponse(cal.Id, cal.Name, cal.Color, cal.Kind, cal.IsDefault, cal.Events.Count);
    }

    public async Task DeleteCalendarAsync(Guid id, CancellationToken ct)
    {
        var cal = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "日历不存在");
        cal.DeletedAt = _timeProvider.GetUtcNow();
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
                        && e.DtEnd > minValidDate
                        && ((e.DtStart < end && e.DtEnd > start) || !string.IsNullOrEmpty(e.RRule) || e.IsException))
            .AsNoTracking()
            .ToListAsync(ct);

        var expanded = _recurrence.ExpandEventsV2(entities, start, end);

        return expanded
            .OrderBy(x => x.OccurrenceStart)
            .Select(EventResponseMapper.MapExpanded)
            .ToList();
    }

    public async Task<PagedResult<EventResponse>> GetEventsPagedAsync(
        string? search, Guid? calendarId,
        DateTimeOffset? start, DateTimeOffset? end,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var minValidDate = DateTimeOffset.MinValue.AddYears(100);
        var hasWindow = start.HasValue && end.HasValue;
        var query = _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == UserId
                        && e.DtStart > minValidDate
                        && e.DtEnd > minValidDate);

        if (hasWindow)
        {
            var s = start!.Value;
            var e2 = end!.Value;
            query = query.Where(e => ((e.DtStart < e2 && e.DtEnd > s) || !string.IsNullOrEmpty(e.RRule) || e.IsException));
        }

        if (!string.IsNullOrEmpty(search))
            query = query.Where(e => e.Title.Contains(search));
        if (calendarId.HasValue)
            query = query.Where(e => e.CalendarId == calendarId.Value);

        var entities = await query.AsNoTracking().ToListAsync(ct);

        var rangeStart = start ?? DateTimeOffset.MinValue;
        var rangeEnd = end ?? DateTimeOffset.MaxValue;
        var expanded = _recurrence.ExpandEventsV2(entities, rangeStart, rangeEnd);

        var totalCount = expanded.Count;
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var items = expanded
            .OrderByDescending(x => x.OccurrenceStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(EventResponseMapper.MapExpanded)
            .ToList();

        return new PagedResult<EventResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<EventResponse> CreateEventAsync(CreateEventRequest request, CancellationToken ct)
    {
        request = EventFieldValidator.ValidateAndNormalize(request);

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

        var isHtml = string.Equals(request.DescriptionFormat, "html", StringComparison.OrdinalIgnoreCase);
        if (isHtml)
        {
            var normalized = EventDescriptionSanitizer.Normalize(request.Description, "html");
            request = request with
            {
                Description = normalized,
                DescriptionFormat = normalized is null ? null : request.DescriptionFormat
            };
        }
        else
        {
            ManualDescriptionValidator.EnsureSafe(request.Description);
            if (string.IsNullOrWhiteSpace(request.Description))
                request = request with { Description = null, DescriptionFormat = null };
        }

        await ValidatePimFileReferencesAsync(request.AttachmentReferences, ct);

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

        ApplyUnifiedFields(entity, request);

        // PR3: Series / exception handling (bool? : null defaults to false)
        if (request.IsException == true)
        {
            if (!request.SeriesMasterId.HasValue || string.IsNullOrEmpty(request.RecurrenceId))
                throw new DomainException(02009, "例外必须指定系列主事件和原始发生时间");
            var master = await _db.Set<EventEntity>()
                .FirstOrDefaultAsync(m => m.Id == request.SeriesMasterId.Value && m.Calendar.UserId == UserId, ct)
                ?? throw new DomainException(02001, "系列主事件不存在");
            entity.IsException = true;
            entity.SeriesMasterId = request.SeriesMasterId;
            entity.RecurrenceId = request.RecurrenceId;
            entity.IsSeriesMaster = false;
            // Exceptions do not carry RRule
            entity.RRule = null;
        }
        else if (!string.IsNullOrEmpty(request.RRule))
        {
            entity.IsSeriesMaster = true;
            entity.IsException = false;
            entity.SeriesMasterId = null;
            // Keep RRule as provided
        }
        else
        {
            entity.IsSeriesMaster = request.IsSeriesMaster == true;
            entity.IsException = false;
            entity.SeriesMasterId = null;
        }

        _db.Set<EventEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);

        return EventResponseMapper.Map(entity);
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
                RecurrenceMetadataJson = item.RecurrenceMetadataJson,
                IsSeriesMaster = !string.IsNullOrEmpty(item.RRule),
                IsException = false,
                SeriesMasterId = null
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
        => await UpdateEventAsync(id, request, null, null, ct);

    public async Task<EventResponse> UpdateEventAsync(Guid id, UpdateEventRequest request, string? scope, CancellationToken ct)
        => await UpdateEventAsync(id, request, scope, null, ct);

    public async Task<EventResponse> UpdateEventAsync(Guid id, UpdateEventRequest request, string? scope, Guid? originalEventId, CancellationToken ct)
    {
        request = EventFieldValidator.ValidateAndNormalize(request);

        // If originalEventId provided (synthetic occurrence case), prefer master via originalEventId for scope=this/series
        EventEntity? entity = null;
        var hasOriginal = originalEventId.HasValue && originalEventId.Value != Guid.Empty && !string.Equals(scope, "instance", StringComparison.OrdinalIgnoreCase);
        if (hasOriginal && (string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase)))
        {
            entity = await _db.Set<EventEntity>()
                .FirstOrDefaultAsync(e => e.Id == originalEventId.Value && e.Calendar.UserId == UserId, ct);
        }
        entity ??= await _db.Set<EventEntity>()
            .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "日程不存在");
        // Fallback: if requested id is synthetic (not found) but originalEventId is master, entity already resolved
        // Normalize recurrenceId from request / query if needed will be handled by caller

        if (entity.OutlookCalendarBindingId != null)
            throw new DomainException(02009, "Microsoft 日程必须通过确认写回流程修改。");

        var sourceCalendarHasBinding = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => b.PimCalendarId == entity.CalendarId, ct);
        if (sourceCalendarHasBinding)
            throw new DomainException(02009, "Microsoft 日历的日程必须通过确认写回流程修改。");

        if (request.CalendarId != entity.CalendarId)
        {
            var targetCalendar = await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId && c.UserId == UserId, ct)
                ?? throw new DomainException(02003, "日历不存在");
            var targetCalendarHasBinding = await _db.Set<OutlookCalendarBindingEntity>()
                .AnyAsync(b => b.PimCalendarId == targetCalendar.Id, ct);
            if (targetCalendarHasBinding)
                throw new DomainException(02009, "目标日历为 Microsoft 日历，移动操作必须通过确认写回流程。");
        }

        var (normalizedStart, normalizedEnd) = NormalizeAndValidateEventRange(request.DtStart, request.DtEnd);

        var isHtml = string.Equals(request.DescriptionFormat, "html", StringComparison.OrdinalIgnoreCase);
        if (isHtml)
        {
            var normalized = EventDescriptionSanitizer.Normalize(request.Description, "html");
            request = request with
            {
                Description = normalized,
                DescriptionFormat = normalized is null ? null : request.DescriptionFormat
            };
        }
        else
        {
            ManualDescriptionValidator.EnsureSafe(request.Description);
            if (string.IsNullOrWhiteSpace(request.Description))
                request = request with { Description = null, DescriptionFormat = null };
        }

        await ValidatePimFileReferencesAsync(request.AttachmentReferences, ct);

        var isScopeThis = string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase);

        // Scope=this : edit single occurrence -> create/update exception
        if (isScopeThis)
        {
            // If target itself is an exception, update it directly with validation and status preservation
            if (entity.IsException)
            {
                // Validate SeriesMasterId / RecurrenceId belongs to same series if provided
                if (request.SeriesMasterId.HasValue && request.SeriesMasterId.Value != entity.SeriesMasterId)
                    throw new DomainException(02009, "例外与系列主事件不匹配");
                if (!string.IsNullOrEmpty(request.RecurrenceId) && !string.Equals(request.RecurrenceId, entity.RecurrenceId, StringComparison.Ordinal))
                    throw new DomainException(02009, "例外 RecurrenceId 与目标不一致");
                var prevStatus = entity.Status;
                entity.Title = request.Title;
                entity.Description = request.Description;
                entity.Location = request.Location;
                entity.DtStart = normalizedStart;
                entity.DtEnd = normalizedEnd;
                if (request.IsAllDay.HasValue) entity.IsAllDay = request.IsAllDay.Value;
                if (request.TimeZoneId is not null) entity.TimeZoneId = request.TimeZoneId;
                entity.UpdatedAt = _timeProvider.GetUtcNow();
                ApplyUnifiedFields(entity, request);
                if (!string.IsNullOrEmpty(request.RecurrenceId))
                    entity.RecurrenceId = request.RecurrenceId;
                entity.RRule = null;
                // Preserve CANCELLED status — do not auto-revert to CONFIRMED
                if (string.Equals(prevStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                    entity.Status = prevStatus;
                await _db.SaveChangesAsync(ct);
                return EventResponseMapper.Map(entity);
            }

            // Target is master (or regular) -> create or update exception for given RecurrenceId
            if (!entity.IsSeriesMaster && string.IsNullOrEmpty(entity.RRule))
            {
                // Not a series master but scope=this requested -> treat as normal update
                // fall through to series path below
            }
            else
            {
                if (string.IsNullOrEmpty(request.RecurrenceId))
                    throw new DomainException(02009, "修改单次需指定 RecurrenceId");
                // If request provides SeriesMasterId, it must match the target master
                if (request.SeriesMasterId.HasValue && request.SeriesMasterId.Value != entity.Id)
                    throw new DomainException(02009, "例外与系列主事件不匹配");

                var existingException = await _db.Set<EventEntity>()
                    .FirstOrDefaultAsync(e => e.SeriesMasterId == entity.Id && e.RecurrenceId == request.RecurrenceId && e.IsException && e.DeletedAt == null, ct);

                if (existingException != null)
                {
                    var prevStatus = existingException.Status;
                    existingException.Title = request.Title;
                    existingException.Description = request.Description;
                    existingException.Location = request.Location;
                    existingException.DtStart = normalizedStart;
                    existingException.DtEnd = normalizedEnd;
                    if (request.IsAllDay.HasValue) existingException.IsAllDay = request.IsAllDay.Value;
                    if (request.TimeZoneId is not null) existingException.TimeZoneId = request.TimeZoneId;
                    existingException.UpdatedAt = _timeProvider.GetUtcNow();
                    ApplyUnifiedFields(existingException, request);
                    existingException.RecurrenceId = request.RecurrenceId;
                    existingException.RRule = null;
                    // Preserve CANCELLED — do not auto-revert
                    if (string.Equals(prevStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                        existingException.Status = prevStatus;
                    await _db.SaveChangesAsync(ct);
                    return EventResponseMapper.Map(existingException);
                }

                var exceptionEntity = new EventEntity
                {
                    CalendarId = entity.CalendarId,
                    Uid = entity.Uid,
                    Title = request.Title,
                    Description = request.Description,
                    Location = request.Location,
                    DtStart = normalizedStart,
                    DtEnd = normalizedEnd,
                    IsAllDay = request.IsAllDay ?? entity.IsAllDay,
                    TimeZoneId = request.TimeZoneId ?? entity.TimeZoneId,
                    IsException = true,
                    IsSeriesMaster = false,
                    SeriesMasterId = entity.Id,
                    RecurrenceId = request.RecurrenceId,
                    RRule = null,
                    Status = "CONFIRMED",
                    Source = entity.Source,
                };
                ApplyUnifiedFields(exceptionEntity, request);
                // Ensure exception flag persists regardless of ApplyUnifiedFields
                exceptionEntity.IsException = true;
                exceptionEntity.IsSeriesMaster = false;
                exceptionEntity.SeriesMasterId = entity.Id;
                exceptionEntity.RecurrenceId = request.RecurrenceId;
                exceptionEntity.RRule = null;
                _db.Set<EventEntity>().Add(exceptionEntity);
                await _db.SaveChangesAsync(ct);
                return EventResponseMapper.Map(exceptionEntity);
            }
        }

        var isScopeSeries = string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase);

        // scope=series from exception: resolve master and update master, not exception
        // Fix: do not overwrite master DtStart/DtEnd with occurrence's time unless explicitly changed vs master
        if (isScopeSeries && entity.IsException)
        {
            if (!entity.SeriesMasterId.HasValue)
                throw new DomainException(02009, "例外缺少系列主事件");
            var masterEntity = await _db.Set<EventEntity>()
                .FirstOrDefaultAsync(e => e.Id == entity.SeriesMasterId.Value && e.Calendar.UserId == UserId, ct)
                ?? throw new DomainException(02001, "系列主事件不存在");

            if (masterEntity.OutlookCalendarBindingId != null)
                throw new DomainException(02009, "Microsoft 日程必须通过确认写回流程修改。");

            masterEntity.Title = request.Title;
            masterEntity.Description = request.Description;
            masterEntity.Location = request.Location;
            // Only overwrite master time if request time differs from master time (explicit series time change)
            // Otherwise keep master's original DtStart/DtEnd to avoid copying occurrence date to master
            if (normalizedStart != masterEntity.DtStart)
                masterEntity.DtStart = normalizedStart;
            if (normalizedEnd != masterEntity.DtEnd)
            {
                // also ensure only update if duration or time explicitly changed; if only date part differs due to occurrence, skip
                // Compare time-of-day and duration: keep master if only occurrence date shifted
                var reqDuration = normalizedEnd - normalizedStart;
                var masterDuration = masterEntity.DtEnd - masterEntity.DtStart;
                // If durations differ or time-of-day differs, treat as explicit change
                if (reqDuration != masterDuration || normalizedStart.TimeOfDay != masterEntity.DtStart.TimeOfDay)
                    masterEntity.DtEnd = normalizedEnd;
            }
            masterEntity.RRule = request.RRule;
            if (request.IsAllDay.HasValue)
                masterEntity.IsAllDay = request.IsAllDay.Value;
            if (request.TimeZoneId is not null)
                masterEntity.TimeZoneId = request.TimeZoneId;
            masterEntity.UpdatedAt = _timeProvider.GetUtcNow();

            ApplyUnifiedFields(masterEntity, request);

            // Update master fields; ensure RRule/master flags correct and exception not created
            if (!string.IsNullOrEmpty(request.RRule))
            {
                masterEntity.IsSeriesMaster = true;
                masterEntity.IsException = false;
                masterEntity.SeriesMasterId = null;
                masterEntity.RecurrenceId = null;
            }
            else
            {
                if (masterEntity.IsSeriesMaster && string.IsNullOrEmpty(request.RRule))
                    masterEntity.IsSeriesMaster = false;
                if (request.IsSeriesMaster == true)
                    masterEntity.IsSeriesMaster = true;
                masterEntity.IsException = false;
                masterEntity.SeriesMasterId = null;
            }

            await _db.SaveChangesAsync(ct);
            return EventResponseMapper.Map(masterEntity);
        }

        // Default: series/master update path
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
        entity.UpdatedAt = _timeProvider.GetUtcNow();

        ApplyUnifiedFields(entity, request);

        // PR3: Series / exception handling for updates (bool? handling)
        if (request.IsException == true)
        {
            if (!request.SeriesMasterId.HasValue || string.IsNullOrEmpty(request.RecurrenceId))
                throw new DomainException(02009, "例外必须指定系列主事件和原始发生时间");
            var master = await _db.Set<EventEntity>()
                .FirstOrDefaultAsync(m => m.Id == request.SeriesMasterId.Value && m.Calendar.UserId == UserId, ct)
                ?? throw new DomainException(02001, "系列主事件不存在");
            entity.IsException = true;
            entity.SeriesMasterId = request.SeriesMasterId;
            entity.RecurrenceId = request.RecurrenceId;
            entity.IsSeriesMaster = false;
            entity.RRule = null;
        }
        else if (!string.IsNullOrEmpty(request.RRule))
        {
            entity.IsSeriesMaster = true;
            entity.IsException = false;
            entity.SeriesMasterId = null;
        }
        else
        {
            // If RRule cleared, clear master flag
            if (entity.IsSeriesMaster && string.IsNullOrEmpty(request.RRule))
                entity.IsSeriesMaster = false;
            // Explicit master flag from request (bool? : null => false)
            if (request.IsSeriesMaster == true)
                entity.IsSeriesMaster = true;
        }

        // Update RecurrenceId for exception if provided
        if (request.IsException == true && !string.IsNullOrEmpty(request.RecurrenceId))
            entity.RecurrenceId = request.RecurrenceId;

        await _db.SaveChangesAsync(ct);
        return EventResponseMapper.Map(entity);
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
        => await DeleteEventAsync(id, null, null, null, ct);

    public async Task DeleteEventAsync(Guid id, string? scope, CancellationToken ct)
        => await DeleteEventAsync(id, scope, null, null, ct);

    public async Task DeleteEventAsync(Guid id, string? scope, string? recurrenceId, CancellationToken ct)
        => await DeleteEventAsync(id, scope, recurrenceId, null, ct);

    public async Task DeleteEventAsync(Guid id, string? scope, string? recurrenceId, Guid? originalEventId, CancellationToken ct)
    {
        EventEntity? entity = null;
        var hasOriginal = originalEventId.HasValue && originalEventId.Value != Guid.Empty;
        if (hasOriginal && (string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase)))
        {
            entity = await _db.Set<EventEntity>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == originalEventId.Value && e.Calendar.UserId == UserId, ct);
        }
        entity ??= await _db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "日程不存在");
        if (entity.DeletedAt != null)
            return;

        // Normalize recurrenceId to O format when needed
        string? normalizedRecurrenceId = recurrenceId;
        if (!string.IsNullOrEmpty(recurrenceId))
        {
            if (DateTimeOffset.TryParse(recurrenceId, out var parsed))
                normalizedRecurrenceId = parsed.ToString("O");
            else
                throw new DomainException(02009, "RecurrenceId 格式无效");
        }

        var isScopeThis = string.Equals(scope, "this", StringComparison.OrdinalIgnoreCase);
        var isScopeSeries = string.Equals(scope, "series", StringComparison.OrdinalIgnoreCase);
        var operationId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();

        void EnsureNotOutlook(EventEntity e, Guid calendarId, Guid? bindingId)
        {
            if (bindingId != null)
                throw new DomainException(02009, "Microsoft 日程必须通过确认写回流程删除。");
        }

        async Task EnsureCalendarNotOutlookBound(Guid calendarId)
        {
            var hasBinding = await _db.Set<OutlookCalendarBindingEntity>()
                .AnyAsync(b => b.PimCalendarId == calendarId, ct);
            if (hasBinding)
                throw new DomainException(02009, "Microsoft 日历的日程必须通过确认写回流程删除。");
        }

        // Pre-check for direct entity
        EnsureNotOutlook(entity, entity.CalendarId, entity.OutlookCalendarBindingId);
        await EnsureCalendarNotOutlookBound(entity.CalendarId);

        // If scope=series from exception, also check master binding
        EventEntity? resolvedMaster = null;
        if (entity.IsException && isScopeSeries && entity.SeriesMasterId.HasValue)
        {
            resolvedMaster = await _db.Set<EventEntity>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == entity.SeriesMasterId.Value && e.Calendar.UserId == UserId, ct);
            if (resolvedMaster != null && resolvedMaster.DeletedAt == null)
            {
                EnsureNotOutlook(resolvedMaster, resolvedMaster.CalendarId, resolvedMaster.OutlookCalendarBindingId);
                await EnsureCalendarNotOutlookBound(resolvedMaster.CalendarId);
            }
        }
        if (entity.IsSeriesMaster && !string.IsNullOrEmpty(normalizedRecurrenceId))
        {
            // scope=this path will check master already, but also ensure normalized
        }

        if (isScopeThis)
        {
            var recId = normalizedRecurrenceId;
            if (string.IsNullOrEmpty(recId) && entity.IsException)
                recId = entity.RecurrenceId;

            if (entity.IsSeriesMaster)
            {
                if (string.IsNullOrEmpty(recId))
                    throw new DomainException(02009, "删除单次需指定 RecurrenceId");
                var existing = await _db.Set<EventEntity>()
                    .FirstOrDefaultAsync(e => e.SeriesMasterId == entity.Id && e.RecurrenceId == recId && e.IsException && e.DeletedAt == null, ct);
                if (existing != null)
                {
                    existing.Status = "CANCELLED";
                    existing.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct);
                    if (_audit != null)
                        await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", existing.Id,
                            new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "cancel-occurrence", ["affectedCount"] = "1" }, ct);
                    return;
                }
                DateTimeOffset occurrenceStart;
                if (!DateTimeOffset.TryParse(recId, out occurrenceStart))
                    occurrenceStart = entity.DtStart;
                var duration = entity.DtEnd - entity.DtStart;
                var cancelled = new EventEntity
                {
                    CalendarId = entity.CalendarId,
                    Uid = entity.Uid,
                    Title = entity.Title,
                    Description = entity.Description,
                    Location = entity.Location,
                    DtStart = occurrenceStart,
                    DtEnd = occurrenceStart.Add(duration),
                    IsException = true,
                    IsSeriesMaster = false,
                    SeriesMasterId = entity.Id,
                    RecurrenceId = recId,
                    RRule = null,
                    Status = "CANCELLED",
                    Source = entity.Source,
                };
                _db.Set<EventEntity>().Add(cancelled);
                await _db.SaveChangesAsync(ct);
                if (_audit != null)
                    await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", cancelled.Id,
                        new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "cancel-occurrence", ["affectedCount"] = "1" }, ct);
                return;
            }

            if (entity.IsException)
            {
                entity.Status = "CANCELLED";
                entity.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                if (_audit != null)
                    await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", entity.Id,
                        new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "cancel-occurrence", ["affectedCount"] = "1" }, ct);
                return;
            }

            // Non-series entity with scope=this -> soft delete as fallback
            entity.DeletedAt = now;
            entity.DeletedByOperationId = operationId;
            entity.DeletedByOperationKind = "single-event";
            entity.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            if (_audit != null)
                await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", entity.Id,
                    new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "single-event", ["affectedCount"] = "1" }, ct);
            return;
        }

        // scope=series or null: soft-delete cascade
        void MarkDeleted(EventEntity e)
        {
            e.DeletedAt = now;
            e.DeletedByOperationId = operationId;
            e.DeletedByOperationKind = isScopeSeries ? "series" : (e.IsSeriesMaster ? "series" : "single-event");
            e.UpdatedAt = now;
        }

        if (entity.IsException && isScopeSeries)
        {
            if (entity.SeriesMasterId.HasValue)
            {
                var master = resolvedMaster ?? await _db.Set<EventEntity>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(e => e.Id == entity.SeriesMasterId.Value && e.Calendar.UserId == UserId, ct);
                if (master != null && master.DeletedAt == null)
                {
                    MarkDeleted(master);
                    var exceptions = await _db.Set<EventEntity>()
                        .Where(e => e.SeriesMasterId == master.Id && e.IsException && e.DeletedAt == null)
                        .ToListAsync(ct);
                    foreach (var ex in exceptions) MarkDeleted(ex);
                    if (entity.DeletedAt == null) MarkDeleted(entity);
                    await _db.SaveChangesAsync(ct);
                    if (_audit != null)
                        await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", master.Id,
                            new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "series", ["affectedCount"] = (1 + exceptions.Count).ToString() }, ct);
                    return;
                }
            }
            MarkDeleted(entity);
            await _db.SaveChangesAsync(ct);
            if (_audit != null)
                await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", entity.Id,
                    new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "single-event", ["affectedCount"] = "1" }, ct);
            return;
        }

        MarkDeleted(entity);
        if (entity.IsSeriesMaster)
        {
            var exceptions = await _db.Set<EventEntity>()
                .Where(e => e.SeriesMasterId == entity.Id && e.IsException && e.DeletedAt == null)
                .ToListAsync(ct);
            foreach (var ex in exceptions) MarkDeleted(ex);
            await _db.SaveChangesAsync(ct);
            if (_audit != null)
                await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", entity.Id,
                    new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "series", ["affectedCount"] = (1 + exceptions.Count).ToString() }, ct);
            return;
        }

        await _db.SaveChangesAsync(ct);
        if (_audit != null)
            await _audit.RecordSuccessAsync(UserId, "calendar.events.delete", "calendar_event", entity.Id,
                new Dictionary<string, string> { ["operationId"] = operationId.ToString(), ["operationKind"] = "single-event", ["affectedCount"] = "1" }, ct);
    }

    public async Task<int> DeleteEventsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var entities = await _db.Set<EventEntity>()
            .Where(e => ids.Contains(e.Id) && e.Calendar.UserId == UserId)
            .ToListAsync(ct);

        foreach (var entity in entities)
            entity.DeletedAt = _timeProvider.GetUtcNow();

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
        if (request.CalendarId.HasValue)
        {
            var calendar = await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId.Value && c.UserId == UserId, ct)
                ?? throw new DomainException(02003, "日历不存在");
        }

        var due = NormalizeToUtc(request.Due);
        var dtStart = NormalizeToUtc(request.DtStart);
        var plannedEnd = NormalizeToUtc(request.PlannedEnd);
        var estimatedDuration = ParseEstimatedDuration(request.EstimatedDuration);
        var minimumSegment = ParseDuration(request.MinimumSegment);

        ValidateTaskRange(dtStart, plannedEnd);

        ManualDescriptionValidator.EnsureSafe(request.Description);

        var task = new TaskEntity
        {
            UserId = UserId,
            CalendarId = request.CalendarId,
            TaskBookId = request.TaskBookId,
            Uid = Guid.NewGuid().ToString() + "@pim",
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            PercentComplete = request.PercentComplete ?? 0,
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

        if (request.CalendarId.HasValue)
        {
            var calendar = await _db.Set<CalendarEntity>()
                .FirstOrDefaultAsync(c => c.Id == request.CalendarId.Value && c.UserId == UserId, ct)
                ?? throw new DomainException(02003, "日历不存在");
        }

        var due = NormalizeToUtc(request.Due);
        var estimatedDuration = ParseEstimatedDuration(request.EstimatedDuration);
        var minimumSegment = ParseDuration(request.MinimumSegment);

        var finalStart = NormalizeToUtc(request.DtStart);
        var finalEnd = request.PlannedEnd.HasValue
            ? request.PlannedEnd.Value.ToUniversalTime()
            : task.PlannedEnd;

        ValidateTaskRange(finalStart, finalEnd);

        ManualDescriptionValidator.EnsureSafe(request.Description);

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
        if (request.TaskBookId.HasValue)
            task.TaskBookId = request.TaskBookId;
        task.PercentComplete = request.PercentComplete ?? task.PercentComplete;
        if (finalStart.HasValue || request.CalendarId.HasValue)
            task.IsInbox = false;
        if (request.Status is not null)
        {
            task.Status = request.Status;
            if (request.Status == "COMPLETED")
                task.CompletedAt = _timeProvider.GetUtcNow();
        }
        task.UpdatedAt = _timeProvider.GetUtcNow();

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
        task.UpdatedAt = _timeProvider.GetUtcNow();

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
        var now = _timeProvider.GetUtcNow();

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
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
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

        task.UpdatedAt = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteTaskAsync(Guid id, CancellationToken ct)
    {
        var task = await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

        task.DeletedAt = _timeProvider.GetUtcNow();
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

    private async Task ValidatePimFileReferencesAsync(
        IReadOnlyList<EventAttachmentReferenceDto>? references,
        CancellationToken ct)
    {
        if (references is null)
            return;

        foreach (var reference in references)
        {
            // Native calendar CRUD is the only writer of pimFile references.
            // Outlook references are server-authoritative (sync hydration) and
            // must never be accepted from client requests through this path.
            if (!string.Equals(reference.Kind, "pimFile", StringComparison.OrdinalIgnoreCase))
                throw new DomainException(02009, "Outlook 附件引用只能由服务器同步写入。");

            await _attachments.ValidatePimFileReferenceAsync(UserId, reference, ct);
        }
    }

    private static void ApplyUnifiedFields(EventEntity entity, CreateEventRequest request)
    {
        entity.DescriptionFormat = request.DescriptionFormat;
        entity.ShowAs = request.ShowAs;
        entity.Importance = request.Importance;
        entity.Sensitivity = request.Sensitivity;
        entity.CategoriesJson = EventFieldCodec.SerializeCategories(request.Categories);
        entity.IsReminderOn = request.IsReminderOn ?? false;
        entity.ReminderMinutesBeforeStart = request.IsReminderOn == true ? request.ReminderMinutesBeforeStart : null;
        entity.OrganizerJson = EventFieldCodec.SerializePerson(request.Organizer);
        entity.AttendeesJson = EventFieldCodec.SerializeAttendees(request.Attendees);
        entity.IsOnlineMeeting = request.IsOnlineMeeting ?? false;
        entity.OnlineMeetingProvider = request.OnlineMeetingProvider;
        entity.OnlineMeetingUrl = request.OnlineMeetingUrl;
        entity.ExternalLink = request.ExternalLink;
        entity.AttachmentReferencesJson = EventFieldCodec.SerializeAttachments(request.AttachmentReferences);
    }

    private static void ApplyUnifiedFields(EventEntity entity, UpdateEventRequest request)
    {
        entity.DescriptionFormat = request.DescriptionFormat;
        entity.ShowAs = request.ShowAs;
        entity.Importance = request.Importance;
        entity.Sensitivity = request.Sensitivity;

        if (request.Categories is not null)
            entity.CategoriesJson = EventFieldCodec.SerializeCategories(request.Categories);
        if (request.Attendees is not null)
            entity.AttendeesJson = EventFieldCodec.SerializeAttendees(request.Attendees);
        if (request.AttachmentReferences is not null)
            entity.AttachmentReferencesJson = EventFieldCodec.SerializeAttachments(request.AttachmentReferences);

        if (request.IsReminderOn.HasValue)
        {
            entity.IsReminderOn = request.IsReminderOn.Value;
            entity.ReminderMinutesBeforeStart = request.IsReminderOn.Value
                ? request.ReminderMinutesBeforeStart
                : null;
        }
        else if (request.ReminderMinutesBeforeStart.HasValue)
        {
            entity.ReminderMinutesBeforeStart = request.ReminderMinutesBeforeStart;
        }

        entity.OrganizerJson = EventFieldCodec.SerializePerson(request.Organizer);
        if (request.IsOnlineMeeting.HasValue)
            entity.IsOnlineMeeting = request.IsOnlineMeeting.Value;
        entity.OnlineMeetingProvider = request.OnlineMeetingProvider;
        entity.OnlineMeetingUrl = request.OnlineMeetingUrl;
        entity.ExternalLink = request.ExternalLink;
    }

    private static string? FormatDuration(TimeSpan? duration) =>
        duration is not null ? duration.Value.ToString("c") : null;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (value is null) return null;
        try { return System.Xml.XmlConvert.ToTimeSpan(value); }
        catch (FormatException) { }
        catch (OverflowException) { }

        if (System.TimeSpan.TryParseExact(value, "c", System.Globalization.CultureInfo.InvariantCulture, out var fallback))
            return fallback;

        throw new DomainException(02009, $"时长格式无效：{value}。请使用 ISO 8601 格式，例如 PT1H30M。");
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
            t.SubTasks.Select(MapTask).ToList(), t.PlannedEnd, t.TaskBookId, t.PercentComplete);

    // 真库回放修复：确保周期展开按 Asia/Shanghai 本地日期去重，避免跨时区重复
    private static DateTimeOffset NormalizeRecurrenceStart(DateTimeOffset start, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        try
        {
            var local = TimeZoneInfo.ConvertTime(start, tz);
            if (tz.IsInvalidTime(local.DateTime))
                local = local.AddHours(1);
            return TimeZoneInfo.ConvertTimeToUtc(local.DateTime, tz);
        }
        catch
        {
            return start;
        }
    }
}
