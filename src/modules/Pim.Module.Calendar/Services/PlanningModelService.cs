using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class PlanningModelService
{
    private static readonly HashSet<string> DefaultLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "events",
        "task-segments"
    };

    private static readonly HashSet<string> OutlookSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook",
        "outlook-graph",
        "outlook-ics"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public PlanningModelService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<CalendarLayerResponse> GetCalendarLayersAsync(
        CalendarLayerQuery query,
        CancellationToken ct = default)
    {
        var userId = UserId;
        if (query.End <= query.Start)
            throw new DomainException(02027, "Layer end must be after start");

        var requestedLayers = NormalizeLayers(query.Layers);
        var items = new List<CalendarLayerItem>();

        if (requestedLayers.Contains("events"))
        {
            var events = await _db.Set<EventEntity>()
                .AsNoTracking()
                .Include(e => e.Calendar)
                .Where(e => e.Calendar.UserId == userId
                    && e.DtStart < query.End
                    && e.DtEnd > query.Start)
                .ToListAsync(ct);

            items.AddRange(events
                .Where(e => !query.OutlookOnly || IsOutlookSource(e.Source))
                .Select(e => new CalendarLayerItem(
                    $"event:{e.Id}",
                    "events",
                    "event",
                    e.Id,
                    e.Title,
                    e.DtStart,
                    e.DtEnd,
                    e.Source,
                    e.Status,
                    e.Calendar.Color,
                    false)));
        }

        if (requestedLayers.Contains("task-segments"))
        {
            var segments = await _db.Set<TaskExecutionSegmentEntity>()
                .AsNoTracking()
                .Include(s => s.Task)
                .Where(s => s.UserId == userId
                    && s.StartsAt < query.End
                    && s.EndsAt > query.Start)
                .ToListAsync(ct);

            items.AddRange(segments
                .Where(s => !query.OutlookOnly || IsOutlookSource(s.Source))
                .Select(s => new CalendarLayerItem(
                    $"task-segment:{s.Id}",
                    "task-segments",
                    "task-segment",
                    s.Id,
                    s.Task.Title,
                    s.StartsAt,
                    s.EndsAt,
                    s.Source,
                    s.Status,
                    "#22C55E",
                    s.ConfirmationId.HasValue)));
        }

        return new CalendarLayerResponse(
            query.Start,
            query.End,
            items
                .OrderBy(i => i.StartsAt)
                .ThenBy(i => i.Layer)
                .ThenBy(i => i.Title)
                .ThenBy(i => i.ObjectId)
                .ToList());
    }

    public async Task<TaskExecutionSegmentResponse> CreateSegmentAsync(
        Guid taskId,
        CreateTaskExecutionSegmentRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (endsAt <= startsAt)
            throw new DomainException(02024, "Segment end must be after start");

        ValidateShortRequired(request.Status, "Segment status");
        ValidateShortRequired(request.Source, "Segment source");

        var task = await GetTaskAsync(taskId, userId, ct);
        var now = DateTimeOffset.UtcNow;
        var segment = new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = userId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = request.Status,
            Source = request.Source,
            PlanningReason = request.PlanningReason,
            CreatedAt = now,
            UpdatedAt = now
        };

        task.IsInbox = false;
        task.DtStart ??= startsAt;
        task.PlannedEnd ??= endsAt;
        task.UpdatedAt = now;

        _db.Set<TaskExecutionSegmentEntity>().Add(segment);
        await _db.SaveChangesAsync(ct);

        return MapSegment(segment, task.Title);
    }

    public async Task<IReadOnlyList<TaskExecutionSegmentResponse>> ListSegmentsAsync(
        Guid taskId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var task = await GetTaskAsync(taskId, userId, ct);
        return await _db.Set<TaskExecutionSegmentEntity>()
            .AsNoTracking()
            .Where(s => s.TaskId == task.Id && s.UserId == userId)
            .OrderBy(s => s.StartsAt)
            .Select(s => new TaskExecutionSegmentResponse(
                s.Id,
                s.TaskId,
                task.Title,
                s.StartsAt,
                s.EndsAt,
                s.Status,
                s.Source,
                s.PlanningReason,
                s.ConfirmationId))
            .ToListAsync(ct);
    }

    public async Task DeleteSegmentAsync(
        Guid taskId,
        Guid segmentId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        _ = await GetTaskAsync(taskId, userId, ct);
        var segment = await _db.Set<TaskExecutionSegmentEntity>()
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.TaskId == taskId && s.UserId == userId, ct)
            ?? throw new DomainException(02025, "Task execution segment does not exist");

        var now = DateTimeOffset.UtcNow;
        segment.DeletedAt = now;
        segment.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TaskEntity> GetTaskAsync(Guid taskId, Guid userId, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId, ct)
            ?? throw new DomainException(02004, "Task does not exist");

    private static void ValidateShortRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
            throw new DomainException(02026, $"{fieldName} must be 1-40 characters");
    }

    private static HashSet<string> NormalizeLayers(IReadOnlyList<string>? layers)
    {
        var normalized = layers?
            .SelectMany(layer => layer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Select(layer => layer.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return normalized is { Count: > 0 }
            ? normalized
            : new HashSet<string>(DefaultLayers, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOutlookSource(string source) => OutlookSources.Contains(source);

    private static TaskExecutionSegmentResponse MapSegment(TaskExecutionSegmentEntity segment, string taskTitle)
        => new(
            segment.Id,
            segment.TaskId,
            taskTitle,
            segment.StartsAt,
            segment.EndsAt,
            segment.Status,
            segment.Source,
            segment.PlanningReason,
            segment.ConfirmationId);
}
