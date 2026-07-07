using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class PlanningModelService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public PlanningModelService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

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
