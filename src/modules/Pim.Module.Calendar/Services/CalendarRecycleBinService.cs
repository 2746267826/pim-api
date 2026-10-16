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
    // GC 策略：回收站仅软删，不提供永久删除接口；过期数据由运维定期清理。
    // 默认保留期 30 天，可通过 PurgeExpiredSoftDeletedAsync 定时调用（Hangfire/运维脚本）。
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly CalendarAuditWriter _audit;

    public CalendarRecycleBinService(PimDbContext db, ICurrentUserService currentUser, CalendarAuditWriter audit)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "未登录");

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
            throw new DomainException(02022, "仅日程和任务支持恢复为副本。");

        var preview = await BuildPreviewAsync(normalizedType, id, ct);
        if (preview.Conflicts.Count > 0 && !request.RestoreAsCopy)
            throw new DomainException(02020, "恢复存在冲突");

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
            _ => throw new DomainException(02021, "不支持的回收站类型")
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
            _ => throw new DomainException(02021, "不支持的回收站类型")
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

    private Task<CalendarOperationResult> RestoreEventAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 与任务恢复同一个道理（工单 #17 / F1）：恢复日程会把它重新加进所属**日历**的活跃成员集合，
        // 必须与「整本删除该日历」互斥（删除方会先取该日历的簿锁）。日程所属日历不会变，故锁集合可先无锁读出。
        return RestoreEventLockedAsync(id, restoreAsCopy, operation, operationId, now, ct);
    }

    private async Task<CalendarOperationResult> RestoreEventLockedAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var calendarId = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.Id == id && e.Calendar.UserId == UserId)
            .Select(e => (Guid?)e.CalendarId)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(02004, "日程不存在");

        return await TaskRowLock.WithCalendarLockAsync(
            _db,
            calendarId,
            async () =>
            {
                var evt = await LoadDeletedEventAsync(id, ct);
                EnsureParentBookActive(evt.Calendar);

                if (restoreAsCopy)
                {
                    evt.Uid = $"{Guid.NewGuid()}@pim";
                    evt.SourceUid = null;
                }

                ClearDelete(evt, now);
                // 同任务恢复：锁内提交，保护「校验父本 → 写库」整段。
                await _db.SaveChangesAsync(ct);

                return new CalendarOperationResult(
                    operation,
                    operationId,
                    1,
                    new[] { evt.Id },
                    new[] { Sample(evt) },
                    "Restored event.");
            },
            ct);
    }

    private Task<CalendarOperationResult> RestoreTaskAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 工单 #17 / F1：恢复任务会把它重新加进所属任务簿的**活跃成员集合**，
        // 必须与「整本删除该簿」互斥，否则「先校验父簿有效 → 删除方提交 → 再清删除标记」
        // 会把任务恢复进一本已被删除的簿。
        //
        // 实现：先无锁读出候选所属簿 id（**只用于决定第一轮锁集合**），再按全局锁序取「簿锁 ∪ 任务锁」，
        // **在锁内**重新加载任务并复核「任务此刻的所属簿 == 已锁住的簿」；不一致说明这段间隙里任务被
        // 恢复→迁走→再删除（软删任务在「先被恢复」之后是可以被迁走的），此时释放事务、按新值重算锁集合后重试。
        // 锁内再校验父簿有效性，并在锁内落库。
        // 锁内校验失败时抛的仍是既有的 02023「请先恢复所属本」（与用户晚一步操作时的表现一致，
        // 不新增产品语义）。
        return RestoreTaskLockedAsync(id, restoreAsCopy, operation, operationId, now, ct);
    }

    private async Task<CalendarOperationResult> RestoreTaskLockedAsync(
        Guid id,
        bool restoreAsCopy,
        string operation,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 锁集合必须覆盖「任务实际所属的那本簿」，而这个 id 只能在锁内确定，因此这里做**有界重试**：
        // 先无锁读出候选簿 → 按锁序取「任务 ∪ 该簿」→ 锁内复核任务此刻的所属簿是否正是锁住的那本；
        // 不一致（例如这段间隙里任务被恢复→迁到别的簿→又被删）说明锁集合已过时，释放事务后按新值重来。
        // 不能在持有任务锁之后再补取簿锁：那会打破「先簿后任务」的全局锁序，可能与整本删除形成等待环。
        for (var attempt = 0; attempt < RestoreLockAttempts; attempt++)
        {
            var bookId = await _db.Set<TaskEntity>()
                .IgnoreQueryFilters()
                .Where(t => t.Id == id && t.UserId == UserId)
                .Select(t => t.CalendarId)
                .FirstOrDefaultAsync(ct);

            // ⚠️ stale 必须**和执行结果一起**从委托里返回，不能写成外层可变标志：
            // 执行策略可能在瞬时故障后重放整个委托，外层标志会把上一轮尝试的结论带进下一轮，
            // 造成「这一轮其实恢复成功、却因陈旧标志丢弃成功结果，最后报一个不相干的错」。
            var outcome = await TaskRowLock.WithTaskAndCalendarLocksAsync(
                _db,
                new[] { id },
                bookId,
                async () =>
                {
                    var task = await LoadDeletedTaskAsync(id, ct);
                    if (task.CalendarId != bookId)
                        return new RestoreOutcome(null, Stale: true);

                    if (task.Calendar is not null)
                        EnsureParentBookActive(task.Calendar);

                    if (restoreAsCopy)
                        task.Uid = $"{Guid.NewGuid()}@pim";

                    ClearDelete(task, now);
                    // 在锁内提交：锁保护的是「校验父簿 → 写库」这一整段，不能只在锁内改内存。
                    await _db.SaveChangesAsync(ct);

                    return new RestoreOutcome(
                        new CalendarOperationResult(
                            operation,
                            operationId,
                            1,
                            new[] { task.Id },
                            new[] { Sample(task) },
                            "Restored task."),
                        Stale: false);
                },
                ct);

            if (!outcome.Stale)
                return outcome.Result!;
        }

        // 连续 N 次都撞上「锁集合过时」：说明该任务正在被其它并发写反复搬动，按既有语义报冲突（02020）。
        throw new DomainException(02020, "恢复存在冲突");
    }

    /// <summary>恢复任务时「锁定所属簿 → 复核」的重试上限。</summary>
    private const int RestoreLockAttempts = 3;

    /// <summary>
    /// 单次恢复尝试的结果：<c>Stale</c> 表示「锁内发现任务实际所属簿与已锁住的簿不一致」（锁集合已过时），
    /// 需要释放事务后按新值重算锁集合；它与结果一起返回，避免用外层可变标志跨执行策略重放。
    /// </summary>
    private sealed record RestoreOutcome(CalendarOperationResult? Result, bool Stale);

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

        // #309 需求 6：随 Outlook 删除而进回收站的日历，恢复后转为 PIM 本地日历——
        // 它与 Outlook 已脱钩（绑定行已删除），恢复出来就是可编辑/可删除的本地数据。
        // 这里把 Source 改回 manual 以免后续逻辑继续把它当作外部来源。
        var outlivedBinding = await _db.Set<OutlookCalendarBindingEntity>()
            .AnyAsync(b => b.PimCalendarId == calendar.Id, ct);
        if (!outlivedBinding && calendar.Source.StartsWith("outlook", StringComparison.OrdinalIgnoreCase))
        {
            calendar.Source = "manual";
        }

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
                    // #309 需求 6：随 Outlook 删除的日程恢复后是本地数据，必须同时脱离
                    // Outlook 来源语义——否则「Outlook 来源」仍会触发外部来源的确认等级、
                    // 附件走 Graph 下载分支、数据体检把它计为外部数据等行为。
                    if (string.Equals(evt.DeletedByOperationKind, OutlookCalendarSyncService.MirrorDeleteOperationKind, StringComparison.Ordinal))
                    {
                        evt.Source = "manual";
                    }

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
            ?? throw new DomainException(02001, "日程不存在");

    private async Task<TaskEntity> LoadDeletedTaskAsync(Guid id, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .IgnoreQueryFilters()
            .Include(t => t.Calendar)
            .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt != null && t.UserId == UserId, ct)
            ?? throw new DomainException(02004, "任务不存在");

    private async Task<CalendarEntity> LoadDeletedCalendarAsync(string type, Guid id, CancellationToken ct)
    {
        var calendar = await _db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt != null && c.UserId == UserId, ct)
            ?? throw new DomainException(02002, "日历不存在");

        if (type == "task-book" && calendar.Kind != "task")
            throw new DomainException(02002, "日历不存在");
        if (type == "calendar" && calendar.Kind == "task")
            throw new DomainException(02002, "日历不存在");

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
            _ => throw new DomainException(02021, "不支持的回收站类型")
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
            throw new DomainException(02023, "请先恢复所属本。");
    }

    private static IReadOnlyDictionary<string, string> Metadata(Guid operationId, string targetType, int affectedCount)
        => new Dictionary<string, string>
        {
            ["operationId"] = operationId.ToString(),
            ["targetType"] = targetType,
            ["affectedCount"] = affectedCount.ToString()
        };

    /// <summary>
    /// GC：清理超过保留期的软删数据。默认仅统计；当 <paramref name="executeDelete"/> 为 true 时执行物理删除。
    /// 建议通过 Hangfire 定时任务调用 executeDelete:true，或由运维脚本确认后启用。
    /// </summary>
    public async Task<int> PurgeExpiredSoftDeletedAsync(TimeSpan? retention = null, bool executeDelete = false, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - (retention ?? DefaultRetention);
        var expiredCalendars = await _db.Set<CalendarEntity>().IgnoreQueryFilters()
            .Where(c => c.UserId == UserId && c.DeletedAt != null && c.DeletedAt < cutoff)
            .ToListAsync(ct);
        var expiredEvents = await _db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.Calendar.UserId == UserId && e.DeletedAt != null && e.DeletedAt < cutoff)
            .ToListAsync(ct);
        var expiredTasks = await _db.Set<TaskEntity>().IgnoreQueryFilters()
            .Where(t => t.UserId == UserId && t.DeletedAt != null && t.DeletedAt < cutoff)
            .ToListAsync(ct);
        var count = expiredCalendars.Count + expiredEvents.Count + expiredTasks.Count;
        if (executeDelete && count > 0)
        {
            _db.RemoveRange(expiredEvents);
            _db.RemoveRange(expiredTasks);
            _db.RemoveRange(expiredCalendars);
            await _db.SaveChangesAsync(ct);
        }
        return count;
    }

    /// <summary>仅统计过期条数，不删除（兼容旧调用）。</summary>
    public Task<int> CountExpiredSoftDeletedAsync(TimeSpan? retention = null, CancellationToken ct = default)
        => PurgeExpiredSoftDeletedAsync(retention, executeDelete: false, ct);
}
