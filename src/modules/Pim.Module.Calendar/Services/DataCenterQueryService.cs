using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class DataCenterQueryService
{
    private static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        OperationConfirmationStatus.Pending.ToString(),
        "pending"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public DataCenterQueryService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<DataCenterQueryResponse> QueryAsync(
        DataCenterQueryRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var items = new List<DataCenterItem>();

        var events = await _db.Set<EventEntity>()
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Where(e => e.Calendar.UserId == userId)
            .ToListAsync(ct);
        items.AddRange(events.Select(e => new DataCenterItem(
            "event",
            e.Id,
            e.Title,
            e.Source,
            e.Status,
            e.DtStart,
            e.DtEnd,
            FirstText(e.Description, e.Location, e.Calendar.Name))));

        var tasks = await _db.Set<TaskEntity>()
            .AsNoTracking()
            .Include(t => t.Calendar)
            .Where(t => t.UserId == userId)
            .ToListAsync(ct);
        items.AddRange(tasks.Select(t => new DataCenterItem(
            "task",
            t.Id,
            t.Title,
            "manual",
            t.Status,
            t.DtStart,
            t.PlannedEnd ?? t.Due,
            FirstText(t.Description, t.Calendar?.Name))));

        var segments = await _db.Set<TaskExecutionSegmentEntity>()
            .AsNoTracking()
            .Include(s => s.Task)
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
        items.AddRange(segments.Select(s => new DataCenterItem(
            "task-segment",
            s.Id,
            s.Task.Title,
            s.Source,
            s.Status,
            s.StartsAt,
            s.EndsAt,
            FirstText(s.PlanningReason, s.Task.Description))));

        var confirmations = await _db.OperationConfirmations
            .AsNoTracking()
            .Where(c => c.RequestedByUserId == null || c.RequestedByUserId == userId)
            .ToListAsync(ct);
        items.AddRange(confirmations.Select(c => new DataCenterItem(
            "confirmation",
            c.Id,
            c.OperationType,
            c.Source,
            c.Status,
            c.CreatedAt,
            c.ExpiresAt,
            c.Summary)));

        var deletedCalendars = await _db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.DeletedAt != null)
            .ToListAsync(ct);
        items.AddRange(deletedCalendars.Select(c => new DataCenterItem(
            "recycle-bin",
            c.Id,
            c.Name,
            "manual",
            "deleted",
            c.DeletedAt,
            null,
            c.Kind == "task" ? "Deleted task book" : "Deleted calendar")));

        var deletedEvents = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Where(e => e.DeletedAt != null && e.Calendar.UserId == userId)
            .ToListAsync(ct);
        items.AddRange(deletedEvents.Select(e => new DataCenterItem(
            "recycle-bin",
            e.Id,
            e.Title,
            e.Source,
            "deleted",
            e.DtStart,
            e.DtEnd,
            FirstText(e.Description, e.Location, $"Deleted at {e.DeletedAt:O}"))));

        var deletedTasks = await _db.Set<TaskEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(t => t.Calendar)
            .Where(t => t.DeletedAt != null && t.UserId == userId)
            .ToListAsync(ct);
        items.AddRange(deletedTasks.Select(t => new DataCenterItem(
            "recycle-bin",
            t.Id,
            t.Title,
            "manual",
            "deleted",
            t.DtStart,
            t.PlannedEnd ?? t.Due,
            FirstText(t.Description, t.Calendar?.Name, $"Deleted at {t.DeletedAt:O}"))));

        var filtered = ApplyFilters(items, request)
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

    private static IEnumerable<DataCenterItem> ApplyFilters(
        IEnumerable<DataCenterItem> items,
        DataCenterQueryRequest request)
    {
        var filtered = items;

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            filtered = filtered.Where(i =>
                ContainsIgnoreCase(i.Title, term)
                || ContainsIgnoreCase(i.Summary, term)
                || ContainsIgnoreCase(i.Source, term)
                || ContainsIgnoreCase(i.Status, term));
        }

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
            filtered = filtered.Where(i =>
                string.Equals(i.ObjectType, "confirmation", StringComparison.OrdinalIgnoreCase)
                && PendingStatuses.Contains(i.Status));
        }

        return filtered;
    }

    private static bool ContainsIgnoreCase(string? value, string term)
        => value?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
