using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class DataCenterQueryService
{
    private const string GenericConfirmationSummary = "待确认操作";

    private static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        OperationConfirmationStatus.Pending.ToString(),
        "pending"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public DataCenterQueryService(PimDbContext db, ICurrentUserService currentUser, TimeProvider? timeProvider = null)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<DataCenterQueryResponse> QueryAsync(
        DataCenterQueryRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var isAdmin = await IsCurrentActiveAdminAsync(ct);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var items = new List<DataCenterItem>();
        var filterType = request.ObjectType?.Trim().ToLowerInvariant();
        var searchTerm = request.Search?.Trim();
        var filterSource = request.Source?.Trim();
        bool ShouldInclude(string type) => string.IsNullOrWhiteSpace(filterType) || string.Equals(filterType, type, StringComparison.OrdinalIgnoreCase);

        // Fast path: single ObjectType → DB-level pagination to avoid full load
        if (!string.IsNullOrWhiteSpace(filterType))
        {
            var single = await QuerySingleTypeAsync(userId, isAdmin, filterType!, searchTerm, filterSource,
                request, page, pageSize, ct);
            if (single != null) return single;
        }

        if (ShouldInclude("event"))
        {
            var q = _db.Set<EventEntity>().AsNoTracking().Include(e => e.Calendar).Where(e => e.Calendar.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(e => e.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(e =>
                e.Title.ToLower().Contains(searchTerm.ToLower())
                || e.Description != null && e.Description.ToLower().Contains(searchTerm.ToLower())
                || e.Location != null && e.Location.ToLower().Contains(searchTerm.ToLower())
                || e.Source.ToLower().Contains(searchTerm.ToLower())
                || e.Status.ToLower().Contains(searchTerm.ToLower())
                || e.Calendar.Name.ToLower().Contains(searchTerm.ToLower()));
            var events = await q.Take(2000).ToListAsync(ct);
            items.AddRange(events.Select(e => new DataCenterItem("event", e.Id, e.Title, e.Source, e.Status, e.DtStart, e.DtEnd, BuildEventSummary(e))));
        }
        if (ShouldInclude("task"))
        {
            var q = _db.Set<TaskEntity>().AsNoTracking().Include(t => t.Calendar).Where(t => t.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(t =>
                t.Title.ToLower().Contains(searchTerm.ToLower())
                || t.Description != null && t.Description.ToLower().Contains(searchTerm.ToLower())
                || t.Status.ToLower().Contains(searchTerm.ToLower())
                || t.Calendar != null && t.Calendar.Name.ToLower().Contains(searchTerm.ToLower()));
            var tasks = await q.Take(2000).ToListAsync(ct);
            items.AddRange(tasks.Select(t => new DataCenterItem("task", t.Id, t.Title, "manual", t.Status, t.DtStart, t.PlannedEnd ?? t.Due, FirstText(t.Description, t.Calendar?.Name))));
        }
        if (ShouldInclude("task-segment"))
        {
            var q = _db.Set<TaskExecutionSegmentEntity>().AsNoTracking().Include(s => s.Task).Where(s => s.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(s => s.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.ToLower();
                q = isAdmin
                    ? q.Where(s => s.Task.Title.ToLower().Contains(term)
                        || s.PlanningReason != null && s.PlanningReason.ToLower().Contains(term)
                        || s.Source.ToLower().Contains(term)
                        || s.Status.ToLower().Contains(term))
                    : q.Where(s => s.Task.Title.ToLower().Contains(term)
                        || s.Source.ToLower().Contains(term)
                        || s.Status.ToLower().Contains(term));
            }
            var segments = await q.Take(2000).ToListAsync(ct);
            items.AddRange(segments.Select(s => new DataCenterItem("task-segment", s.Id, s.Task.Title, s.Source,
                s.Status, s.StartsAt, s.EndsAt,
                isAdmin ? FirstText(s.PlanningReason, s.Task.Description) : FirstText(s.Task.Description))));
        }
        if (ShouldInclude("habit"))
        {
            var q = _db.Set<HabitRoutineEntity>().AsNoTracking().Where(h => h.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(h => h.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(h =>
                h.Title.ToLower().Contains(searchTerm.ToLower())
                || h.Description != null && h.Description.ToLower().Contains(searchTerm.ToLower())
                || h.Cadence.ToLower().Contains(searchTerm.ToLower())
                || h.Source.ToLower().Contains(searchTerm.ToLower())
                || h.Status.ToLower().Contains(searchTerm.ToLower()));
            var habits = await q.Take(2000).ToListAsync(ct);
            items.AddRange(habits.Select(h => new DataCenterItem("habit", h.Id, h.Title, h.Source, h.Status,
                h.CreatedAt, h.UpdatedAt,
                isAdmin ? FirstText(h.Description, h.Cadence, h.RuleJson) : FirstText(h.Description, h.Cadence))));
        }
        if (ShouldInclude("habit-occurrence"))
        {
            var q = _db.Set<HabitOccurrenceEntity>().AsNoTracking().Include(o => o.HabitRoutine).Where(o => o.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(o => o.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(o =>
                o.HabitRoutine.Title.ToLower().Contains(searchTerm.ToLower())
                || o.Source.ToLower().Contains(searchTerm.ToLower())
                || o.Status.ToLower().Contains(searchTerm.ToLower()));
            var habitOccurrences = await q.Take(2000).ToListAsync(ct);
            items.AddRange(habitOccurrences.Select(o => new DataCenterItem("habit-occurrence", o.Id, o.HabitRoutine.Title, o.Source, o.Status, o.StartsAt, o.EndsAt, $"Habit occurrence for {o.HabitRoutine.Cadence} routine")));
        }
        if (ShouldInclude("availability"))
        {
            var q = _db.Set<AvailabilityWindowEntity>().AsNoTracking().Where(a => a.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(a => a.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(a =>
                a.Title.ToLower().Contains(searchTerm.ToLower())
                || a.Source.ToLower().Contains(searchTerm.ToLower())
                || a.Kind.ToLower().Contains(searchTerm.ToLower()));
            var availability = await q.Take(2000).ToListAsync(ct);
            items.AddRange(availability.Select(a => new DataCenterItem("availability", a.Id, a.Title, a.Source, a.Kind, a.StartsAt, a.EndsAt, "Availability window")));
        }
        if (ShouldInclude("ai-placeholder"))
        {
            var q = _db.Set<AiPlanningPlaceholderEntity>().AsNoTracking().Where(p => p.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(p => p.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(p =>
                p.Title.ToLower().Contains(searchTerm.ToLower())
                || p.Source.ToLower().Contains(searchTerm.ToLower())
                || p.Status.ToLower().Contains(searchTerm.ToLower()));
            var placeholders = await q.Take(2000).ToListAsync(ct);
            items.AddRange(placeholders.Select(p => new DataCenterItem("ai-placeholder", p.Id, p.Title, p.Source,
                p.Status, p.StartsAt, p.EndsAt, isAdmin ? p.Reason : "计划占位")));
        }
        if (ShouldInclude("reminder"))
        {
            var q = _db.Set<ReminderEntity>().AsNoTracking().Where(r => r.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.ToLower();
                q = isAdmin
                    ? q.Where(r => r.Title.ToLower().Contains(term)
                        || r.TriggerReason != null && r.TriggerReason.ToLower().Contains(term)
                        || r.Body != null && r.Body.ToLower().Contains(term)
                        || r.Status.ToLower().Contains(term))
                    : q.Where(r => r.Title.ToLower().Contains(term)
                        || r.Body != null && r.Body.ToLower().Contains(term)
                        || r.Status.ToLower().Contains(term));
            }
            var reminders = await q.Take(2000).ToListAsync(ct);
            items.AddRange(reminders.Select(r => new DataCenterItem("reminder", r.Id, r.Title, "reminder",
                r.Status, r.ScheduledAt, null,
                isAdmin ? FirstText(r.TriggerReason, r.Body, r.RiskLevel) : FirstText(r.Body))));
        }
        if (ShouldInclude("reminder-delivery"))
        {
            var q = _db.Set<ReminderDeliveryEntity>().AsNoTracking().Include(d => d.Reminder).Where(d => d.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(d =>
                d.Reminder.Title.ToLower().Contains(searchTerm.ToLower())
                || d.Channel.ToLower().Contains(searchTerm.ToLower())
                || d.Status.ToLower().Contains(searchTerm.ToLower()));
            var reminderDeliveries = await q.Take(2000).ToListAsync(ct);
            items.AddRange(reminderDeliveries.Select(d => new DataCenterItem("reminder-delivery", d.Id,
                d.Reminder.Title, d.Channel, d.Status, d.CreatedAt, d.RespondedAt,
                isAdmin ? FirstText(d.Action, d.PayloadJson) : FirstText(d.Action))));
        }
        if (ShouldInclude("report"))
        {
            var q = _db.Set<ReportArtifactEntity>().AsNoTracking().Where(r => r.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.ToLower();
                q = isAdmin
                    ? q.Where(r => r.Kind.ToLower().Contains(term)
                        || r.Status.ToLower().Contains(term)
                        || r.ContentMarkdown != null && r.ContentMarkdown.ToLower().Contains(term)
                        || r.MetricsJson != null && r.MetricsJson.ToLower().Contains(term))
                    : q.Where(r => r.Kind.ToLower().Contains(term)
                        || r.Status.ToLower().Contains(term)
                        || r.ContentMarkdown != null && r.ContentMarkdown.ToLower().Contains(term));
            }
            var reports = await q.Take(2000).ToListAsync(ct);
            items.AddRange(reports.Select(r => new DataCenterItem("report", r.Id, $"{r.Kind} report", "report",
                r.Status, r.GeneratedAt, null,
                isAdmin ? FirstText(r.ContentMarkdown, r.MetricsJson) : FirstText(r.ContentMarkdown, "报告"))));
        }
        if (ShouldInclude("report-suggestion"))
        {
            var q = _db.Set<ReportSuggestionEntity>().AsNoTracking().Include(s => s.Report).Where(s => s.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.ToLower();
                q = isAdmin
                    ? q.Where(s => s.Action.ToLower().Contains(term)
                        || s.Status.ToLower().Contains(term)
                        || s.Summary != null && s.Summary.ToLower().Contains(term)
                        || s.PayloadJson != null && s.PayloadJson.ToLower().Contains(term))
                    : q.Where(s => s.Action.ToLower().Contains(term)
                        || s.Status.ToLower().Contains(term)
                        || s.Summary != null && s.Summary.ToLower().Contains(term));
            }
            var reportSuggestions = await q.Take(2000).ToListAsync(ct);
            items.AddRange(reportSuggestions.Select(s => new DataCenterItem("report-suggestion", s.Id, s.Action,
                "report", s.Status, s.CreatedAt, s.UpdatedAt,
                isAdmin ? FirstText(s.Summary, s.PayloadJson) : FirstText(s.Summary))));
        }
        if (ShouldInclude("sync-connection"))
        {
            var q = _db.Set<OutlookConnectionEntity>().AsNoTracking().Where(c => c.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(c => c.Provider == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(c =>
                c.Provider.ToLower().Contains(searchTerm.ToLower())
                || c.Status.ToLower().Contains(searchTerm.ToLower()));
            var outlookConnections = await q.Take(2000).ToListAsync(ct);
            items.AddRange(outlookConnections.Select(c => new DataCenterItem("sync-connection", c.Id, c.Provider,
                c.Provider, c.Status, c.LastSyncedAt ?? c.CreatedAt, c.AccessTokenExpiresAt,
                isAdmin ? FirstText(c.TokenHealth, c.LastError) : "同步连接")));
        }
        if (ShouldInclude("sync-batch"))
        {
            var q = _db.Set<OutlookSyncBatchEntity>().AsNoTracking().Where(b => b.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(b => b.Provider == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(b =>
                b.Provider.ToLower().Contains(searchTerm.ToLower())
                || b.Status.ToLower().Contains(searchTerm.ToLower()));
            var syncBatches = await q.Take(2000).ToListAsync(ct);
            items.AddRange(syncBatches.Select(b => new DataCenterItem("sync-batch", b.Id,
                $"{b.Provider} sync batch", b.Provider, b.Status, b.StartedAt, b.FinishedAt,
                isAdmin
                    ? $"read={b.ReadCount}; created={b.CreatedCount}; updated={b.UpdatedCount}; conflicts={b.ConflictCount}; failures={b.FailureCount}"
                    : "同步批次")));
        }
        if (ShouldInclude("sync-conflict"))
        {
            var q = _db.Set<SyncConflictEntity>().AsNoTracking().Where(c => c.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(c => c.Provider == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(c =>
                c.ConflictKind.ToLower().Contains(searchTerm.ToLower())
                || c.Provider.ToLower().Contains(searchTerm.ToLower())
                || c.Status.ToLower().Contains(searchTerm.ToLower()));
            var syncConflicts = await q.Take(2000).ToListAsync(ct);
            items.AddRange(syncConflicts.Select(c => new DataCenterItem("sync-conflict", c.Id, c.ConflictKind,
                c.Provider, c.Status, c.CreatedAt, c.UpdatedAt,
                isAdmin
                    ? FirstText(AuditSnapshotSanitizer.SanitizeJson(c.PimSnapshotJson),
                        AuditSnapshotSanitizer.SanitizeJson(c.ExternalSnapshotJson))
                    : "同步冲突需核对")));
        }
        if (isAdmin && ShouldInclude("audit-version"))
        {
            var q = _db.Set<AuditVersionEntity>().AsNoTracking().Where(v => v.UserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(v => v.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(v =>
                v.ObjectType.ToLower().Contains(searchTerm.ToLower())
                || v.Source.ToLower().Contains(searchTerm.ToLower())
                || v.ChangedFieldsJson != null && v.ChangedFieldsJson.ToLower().Contains(searchTerm.ToLower())
                || v.AfterJson != null && v.AfterJson.ToLower().Contains(searchTerm.ToLower())
                || v.BeforeJson != null && v.BeforeJson.ToLower().Contains(searchTerm.ToLower()));
            var auditVersions = await q.Take(2000).ToListAsync(ct);
            items.AddRange(auditVersions.Select(v => new DataCenterItem("audit-version", v.Id, $"{v.ObjectType} audit version", v.Source, "recorded", v.CreatedAt, null, FirstText(AuditSnapshotSanitizer.SanitizeJson(v.ChangedFieldsJson), AuditSnapshotSanitizer.SanitizeJson(v.AfterJson), AuditSnapshotSanitizer.SanitizeJson(v.BeforeJson)))));
        }
        if (ShouldInclude("confirmation"))
        {
            var q = _db.OperationConfirmations.AsNoTracking().AsQueryable();
            q = isAdmin
                ? q.Where(c => c.RequestedByUserId == null || c.RequestedByUserId == userId)
                : q.Where(c => c.RequestedByUserId == userId);
            if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(c => c.Source == filterSource);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.ToLower();
                q = isAdmin
                    ? q.Where(c => c.OperationType.ToLower().Contains(term)
                        || c.Source.ToLower().Contains(term)
                        || c.Status.ToLower().Contains(term)
                        || c.Summary != null && c.Summary.ToLower().Contains(term))
                    : q.Where(c => c.OperationType.ToLower().Contains(term)
                        || c.Source.ToLower().Contains(term)
                        || c.Status.ToLower().Contains(term));
            }
            if (request.PendingOnly)
            {
                var now = _timeProvider.GetUtcNow();
                q = q.Where(c => PendingStatuses.Contains(c.Status) && c.ExpiresAt > now);
            }
            var confirmations = await q.Take(2000).ToListAsync(ct);
            items.AddRange(confirmations.Select(c => new DataCenterItem("confirmation", c.Id, c.OperationType,
                c.Source, c.Status, c.CreatedAt, c.ExpiresAt,
                isAdmin ? c.Summary : GenericConfirmationSummary)));
        }
        if (ShouldInclude("recycle-bin"))
        {
            var calQuery = _db.Set<CalendarEntity>().IgnoreQueryFilters().AsNoTracking().Where(c => c.UserId == userId && c.DeletedAt != null);
            if (!string.IsNullOrWhiteSpace(searchTerm)) calQuery = calQuery.Where(c =>
                c.Name.ToLower().Contains(searchTerm.ToLower()) || c.Kind.ToLower().Contains(searchTerm.ToLower()));
            var deletedCalendars = await calQuery.Take(2000).ToListAsync(ct);
            items.AddRange(deletedCalendars.Select(c => new DataCenterItem("recycle-bin", c.Id, c.Name, "manual", "deleted", c.DeletedAt, null, c.Kind == "task" ? "Deleted task book" : "Deleted calendar")));
            var evQuery = _db.Set<EventEntity>().IgnoreQueryFilters().AsNoTracking().Include(e => e.Calendar).Where(e => e.DeletedAt != null && e.Calendar.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm)) evQuery = evQuery.Where(e =>
                e.Title.ToLower().Contains(searchTerm.ToLower())
                || e.Source.ToLower().Contains(searchTerm.ToLower())
                || e.Description != null && e.Description.ToLower().Contains(searchTerm.ToLower()));
            var deletedEvents = await evQuery.Take(2000).ToListAsync(ct);
            items.AddRange(deletedEvents.Select(e => new DataCenterItem("recycle-bin", e.Id, e.Title, e.Source, "deleted", e.DtStart, e.DtEnd, FirstText(BuildEventSummary(e), $"Deleted at {e.DeletedAt:O}"))));
            var taskQuery = _db.Set<TaskEntity>().IgnoreQueryFilters().AsNoTracking().Include(t => t.Calendar).Where(t => t.DeletedAt != null && t.UserId == userId);
            if (!string.IsNullOrWhiteSpace(searchTerm)) taskQuery = taskQuery.Where(t =>
                t.Title.ToLower().Contains(searchTerm.ToLower())
                || t.Description != null && t.Description.ToLower().Contains(searchTerm.ToLower()));
            var deletedTasks = await taskQuery.Take(2000).ToListAsync(ct);
            items.AddRange(deletedTasks.Select(t => new DataCenterItem("recycle-bin", t.Id, t.Title, "manual", "deleted", t.DtStart, t.PlannedEnd ?? t.Due, FirstText(t.Description, t.Calendar?.Name, $"Deleted at {t.DeletedAt:O}"))));
        }

        var filtered = ApplyStructuralFilters(items, request)
            .OrderBy(i => i.StartsAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.EndsAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.ObjectType)
            .ThenBy(i => i.Title)
            .ThenBy(i => i.ObjectId)
            .ToList();

        var totalCount = filtered.Count;
        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new DataCenterQueryResponse(pageItems, page, pageSize, totalCount);
    }

    private async Task<DataCenterQueryResponse?> QuerySingleTypeAsync(Guid userId, bool isAdmin,
        string filterType, string? searchTerm, string? filterSource, DataCenterQueryRequest request, int page,
        int pageSize, CancellationToken ct)
    {
        // 为最常见的单类型查询提供真正的 DB pagination，避免全量
        switch (filterType)
        {
            case "event":
            {
                var q = _db.Set<EventEntity>().AsNoTracking().Include(e => e.Calendar).Where(e => e.Calendar.UserId == userId);
                if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(e => e.Source == filterSource);
                if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(e => e.Title.ToLower().Contains(searchTerm!.ToLower()) || e.Source.ToLower().Contains(searchTerm!.ToLower()));
                var total = await q.CountAsync(ct);
                var rows = await q.OrderBy(e => e.DtStart).ThenBy(e => e.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
                var items = rows.Select(e => new DataCenterItem("event", e.Id, e.Title, e.Source, e.Status, e.DtStart, e.DtEnd, BuildEventSummary(e))).ToList();
                return new DataCenterQueryResponse(items, page, pageSize, total);
            }
            case "task":
            {
                var q = _db.Set<TaskEntity>().AsNoTracking().Include(t => t.Calendar).Where(t => t.UserId == userId);
                if (!string.IsNullOrWhiteSpace(searchTerm)) q = q.Where(t => t.Title.ToLower().Contains(searchTerm!.ToLower()));
                var total = await q.CountAsync(ct);
                var rows = await q.OrderBy(t => t.DtStart).ThenBy(t => t.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
                var items = rows.Select(t => new DataCenterItem("task", t.Id, t.Title, "manual", t.Status, t.DtStart, t.PlannedEnd ?? t.Due, FirstText(t.Description, t.Calendar?.Name))).ToList();
                return new DataCenterQueryResponse(items, page, pageSize, total);
            }
            case "confirmation":
            {
                var q = _db.OperationConfirmations.AsNoTracking().AsQueryable();
                q = isAdmin
                    ? q.Where(c => c.RequestedByUserId == null || c.RequestedByUserId == userId)
                    : q.Where(c => c.RequestedByUserId == userId);
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    var term = searchTerm.ToLower();
                    q = isAdmin
                        ? q.Where(c => c.OperationType.ToLower().Contains(term)
                            || c.Summary != null && c.Summary.ToLower().Contains(term))
                        : q.Where(c => c.OperationType.ToLower().Contains(term));
                }
                if (!string.IsNullOrWhiteSpace(filterSource)) q = q.Where(c => c.Source == filterSource);
                if (request.PendingOnly)
                {
                    var now = _timeProvider.GetUtcNow();
                    q = q.Where(c => PendingStatuses.Contains(c.Status) && c.ExpiresAt > now);
                }
                var total = await q.CountAsync(ct);
                var rows = await q.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
                var items = rows.Select(c => new DataCenterItem("confirmation", c.Id, c.OperationType,
                    c.Source, c.Status, c.CreatedAt, c.ExpiresAt,
                    isAdmin ? c.Summary : GenericConfirmationSummary)).ToList();
                return new DataCenterQueryResponse(items, page, pageSize, total);
            }
            default: return null;
        }
    }

    private static IEnumerable<DataCenterItem> ApplyStructuralFilters(
        IEnumerable<DataCenterItem> items,
        DataCenterQueryRequest request)
    {
        var filtered = items;

        if (!string.IsNullOrWhiteSpace(request.ObjectType))
        {
            var objectType = request.ObjectType.Trim();
            filtered = filtered.Where(i => string.Equals(i.ObjectType, objectType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            var source = request.Source.Trim();
            filtered = filtered.Where(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase));
        }

        if (request.PendingOnly)
        {
            var now = _timeProvider.GetUtcNow();
            filtered = filtered.Where(i =>
                string.Equals(i.ObjectType, "confirmation", StringComparison.OrdinalIgnoreCase)
                && PendingStatuses.Contains(i.Status)
                && (!i.EndsAt.HasValue || i.EndsAt.Value > now));
        }

        return filtered;
    }

    private async Task<bool> IsCurrentActiveAdminAsync(CancellationToken ct)
    {
        if (_currentUser.UserId is not Guid userId ||
            !string.Equals(_currentUser.Role, AdminBootstrap.AdminRole, StringComparison.OrdinalIgnoreCase))
            return false;
        return await _db.Users.AsNoTracking().AnyAsync(x =>
            x.Id == userId && x.IsActive && x.Role == AdminBootstrap.AdminRole, ct);
    }

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string BuildEventSummary(EventEntity e)
    {
        var parts = new List<string>();
        Add(e.Description);
        Add(e.Location);
        Add(e.Calendar.Name);
        if (e.Source.StartsWith("outlook", StringComparison.OrdinalIgnoreCase))
        {
            Add("Outlook event (external identifiers hidden)");
        }

        return string.Join(" | ", parts);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add(value);
        }
    }
}
