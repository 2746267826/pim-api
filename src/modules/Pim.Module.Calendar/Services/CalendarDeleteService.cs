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
    private readonly TimeProvider _timeProvider;

    public CalendarDeleteService(PimDbContext db, ICurrentUserService currentUser, CalendarAuditWriter audit, TimeProvider? timeProvider = null)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "未登录");

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
            $"删除 {calendar.Name} 及 {affectedCount} 个活跃{(calendar.Kind == "task" ? "任务" : "日程")}。",
            true);
    }

    public Task<CalendarOperationResult> DeleteCalendarAsync(Guid calendarId, CancellationToken ct = default)
        // 复审 R1（第 3 轮）：整本删除**不**用锁外的投影 id 加锁 —— 投影与锁内重读之间新加入该簿的
        // 任务会落在锁集合之外。改成在事务内加载**真实**要删的任务集合，拿到集合后再逐把补任务锁。
        // 复审 I1（第 4 轮）：只补任务锁仍挡不住「并发把新任务建/迁进这本簿」——
        // 所以这里同时取**簿级咨询锁**（与 CreateTaskAsync / UpdateTaskAsync 指定 calendarId 时同一把），
        // 让「改簿成员」与「删整本」互斥。
        => TaskRowLock.WithCalendarLockAsync(_db, calendarId, () => DeleteCalendarCoreAsync(calendarId, ct), ct);

    private async Task<CalendarOperationResult> DeleteCalendarCoreAsync(Guid calendarId, CancellationToken ct)
    {
        // 锁内重读（软删过滤器在模型上，已删除的簿会直接抛 02003）。
        var calendar = await LoadCalendarAsync(calendarId, ct);
        var operationId = Guid.NewGuid();
        var operationKind = CalendarOperationKind(calendar);
        var deletedAt = _timeProvider.GetUtcNow();
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

            // 复审 R1：锁集合 = 即将被改状态的真实集合（在事务内、改状态之前取锁）。
            await TaskRowLock.AcquireLocksAsync(_db, tasks.Select(t => t.Id).ToList(), ct);

            foreach (var task in tasks)
            {
                MarkDeleted(task, deletedAt, operationId, operationKind);
                affectedIds.Add(task.Id);
            }

            // 复审 I6：整本删除同样要收口正在跑的计时（见 DeleteTaskAsync 的说明）。
            await RunningSegments.CloseAllAsync(
                _db,
                tasks.Select(t => t.Id).ToList(),
                TaskSegmentStatuses.Cancelled,
                deletedAt,
                ct);
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
            $"已删除 {calendar.Name}。");
    }

    public async Task<CalendarOperationResult> DeleteEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var evt = await LoadEventAsync(eventId, ct);

        if (evt.OutlookCalendarBindingId != null)
            throw new DomainException(02009, "Microsoft 日程必须通过确认写回流程删除。");

        var calendarHasBinding = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => b.PimCalendarId == evt.CalendarId, ct);
        if (calendarHasBinding)
            throw new DomainException(02009, "Microsoft 日历的日程必须通过确认写回流程删除。");

        var operationId = Guid.NewGuid();
        var deletedAt = _timeProvider.GetUtcNow();
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

        return Result("calendar.events.delete", operationId, new[] { evt }, "已删除日程。");
    }

    public async Task<CalendarOperationResult> BatchDeleteEventsAsync(IEnumerable<Guid>? ids, CancellationToken ct = default)
    {
        var idSet = NormalizeIds(ids);
        var operationId = Guid.NewGuid();
        var operation = "calendar.events.batch_delete";
        if (idSet.Count == 0)
            return EmptyResult(operation, operationId, "没有删除日程。");

        var events = await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .Where(e => idSet.Contains(e.Id) && e.Calendar.UserId == UserId)
            .OrderBy(e => e.DtStart)
            .ToListAsync(ct);
        if (events.Count == 0)
            return EmptyResult(operation, operationId, "没有删除日程。");

        if (events.Any(e => e.OutlookCalendarBindingId != null))
            throw new DomainException(02009, "批量删除中包含 Microsoft 日程，必须通过确认写回流程。");

        var calendarIds = events.Select(e => e.CalendarId).Distinct().ToList();
        var hasBoundCalendar = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => calendarIds.Contains(b.PimCalendarId), ct);
        if (hasBoundCalendar)
            throw new DomainException(02009, "批量删除中包含 Microsoft 日历的日程，必须通过确认写回流程。");

        var deletedAt = _timeProvider.GetUtcNow();
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

        return Result(operation, operationId, events, "已删除日程。");
    }

    public Task<CalendarOperationResult> DeleteTaskAsync(Guid taskId, CancellationToken ct = default)
        // 复审 N2：删除与计时动作（timer/start）必须共用同一把任务级锁，否则
        // 「删除方查到没有开放段 → start 插入开放段 → 删除方写软删」会留下
        // 「已删除任务 + 在跑的段」，恢复后继续累计删除期间的时间。
        => TaskRowLock.WithTaskLocksAsync(_db, new[] { taskId }, () => DeleteTaskCoreAsync(taskId, ct), ct);

    private async Task<CalendarOperationResult> DeleteTaskCoreAsync(Guid taskId, CancellationToken ct)
    {
        var task = await LoadTaskAsync(taskId, ct);
        var operationId = Guid.NewGuid();
        var deletedAt = _timeProvider.GetUtcNow();
        var operationKind = "single-task";

        MarkDeleted(task, deletedAt, operationId, operationKind);
        // 复审 I6：任务被软删时把「正在计时」的段就地闭合。否则开放段会一直挂着：
        // pause/stop 因任务被软删过滤而不可达，恢复任务后还会把删除期间的墙钟时间算进实际时长。
        await RunningSegments.CloseAllAsync(
            _db,
            new[] { task.Id },
            TaskSegmentStatuses.Cancelled,
            deletedAt,
            ct);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordSuccessAsync(
            UserId,
            "calendar.tasks.delete",
            "calendar_task",
            task.Id,
            Metadata(operationId, operationKind, task.Title, 1),
            ct);

        return Result("calendar.tasks.delete", operationId, new[] { task }, "已删除任务。");
    }

    public async Task<CalendarOperationResult> BatchDeleteTasksAsync(IEnumerable<Guid>? ids, CancellationToken ct = default)
    {
        var idSet = NormalizeIds(ids);
        if (idSet.Count == 0)
            return EmptyResult("calendar.tasks.batch_delete", Guid.NewGuid(), "没有删除任务。");

        // 复审 N2：与计时动作共用任务级锁（见 DeleteTaskAsync 的说明）。
        return await TaskRowLock.WithTaskLocksAsync(
            _db, idSet, () => BatchDeleteTasksCoreAsync(idSet, ct), ct);
    }

    private async Task<CalendarOperationResult> BatchDeleteTasksCoreAsync(IReadOnlyList<Guid> idSet, CancellationToken ct)
    {
        var operationId = Guid.NewGuid();
        var operation = "calendar.tasks.batch_delete";

        var tasks = await _db.Set<TaskEntity>()
            .Include(t => t.Calendar)
            .Where(t => idSet.Contains(t.Id) && t.UserId == UserId)
            .OrderBy(t => t.Title)
            .ToListAsync(ct);
        if (tasks.Count == 0)
            return EmptyResult(operation, operationId, "没有删除任务。");

        var deletedAt = _timeProvider.GetUtcNow();
        var operationKind = "batch-task";

        foreach (var task in tasks)
            MarkDeleted(task, deletedAt, operationId, operationKind);

        // 复审 I6：批量删除同样要收口正在跑的计时。
        await RunningSegments.CloseAllAsync(
            _db,
            tasks.Select(t => t.Id).ToList(),
            TaskSegmentStatuses.Cancelled,
            deletedAt,
            ct);

        if (tasks.Count > 0)
            await _db.SaveChangesAsync(ct);

        await _audit.RecordSuccessAsync(
            UserId,
            operation,
            "calendar_task",
            operationId,
            Metadata(operationId, operationKind, null, tasks.Count),
            ct);

        return Result(operation, operationId, tasks, "已删除任务。");
    }

    private async Task<CalendarEntity> LoadCalendarAsync(Guid calendarId, CancellationToken ct)
        => await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "日历不存在");

    private async Task<EventEntity> LoadEventAsync(Guid eventId, CancellationToken ct)
        => await _db.Set<EventEntity>()
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar.UserId == UserId, ct)
            ?? throw new DomainException(02001, "日程不存在");

    private async Task<TaskEntity> LoadTaskAsync(Guid taskId, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .Include(t => t.Calendar)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

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
