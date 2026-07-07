using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class TaskExecutionSegmentServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CreateSegmentAsync_RejectsEndsAtBeforeOrEqualStartsAt()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, UserId, "Write segment tests");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateSegmentAsync(
                task.Id,
                new CreateTaskExecutionSegmentRequest(
                    new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                    "planned",
                    "manual",
                    null)));

        Assert.Equal(02024, error.ErrorCode);
        Assert.Empty(await db.Set<TaskExecutionSegmentEntity>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task CreateSegmentAsync_KeepsTaskIdentityAndReturnsSegmentMetadata()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, UserId, "Draft proposal");
        task.IsInbox = true;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateSegmentAsync(
            task.Id,
            new CreateTaskExecutionSegmentRequest(
                new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 6, 1, 10, 30, 0, TimeSpan.FromHours(8)),
                "planned",
                "model",
                "best focus window"));

        Assert.Equal(task.Id, response.TaskId);
        Assert.Equal("Draft proposal", response.TaskTitle);
        Assert.Equal("planned", response.Status);
        Assert.Equal("model", response.Source);
        Assert.Equal("best focus window", response.PlanningReason);
        Assert.Null(response.ConfirmationId);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero), response.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 2, 30, 0, TimeSpan.Zero), response.EndsAt);

        var storedTask = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        Assert.False(storedTask.IsInbox);
        Assert.Equal(response.StartsAt, storedTask.DtStart);
        Assert.Equal(response.EndsAt, storedTask.PlannedEnd);
        Assert.Empty(await db.Set<EventEntity>().ToListAsync());
    }

    [Fact]
    public async Task CreateSegmentAsync_DoesNotOverwriteExistingTaskPlanningRange()
    {
        await using var db = CreateDb();
        var originalStart = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var originalEnd = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var task = SeedTask(db, UserId, "Keep old range");
        task.DtStart = originalStart;
        task.PlannedEnd = originalEnd;
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.CreateSegmentAsync(
            task.Id,
            new CreateTaskExecutionSegmentRequest(
                new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero),
                "planned",
                "manual",
                null));

        var storedTask = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(originalStart, storedTask.DtStart);
        Assert.Equal(originalEnd, storedTask.PlannedEnd);
        Assert.False(storedTask.IsInbox);
    }

    [Fact]
    public async Task CreateSegmentAsync_RejectsAnotherUsersTask()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, OtherUserId, "Other user's task");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateSegmentAsync(
                task.Id,
                new CreateTaskExecutionSegmentRequest(
                    new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                    "planned",
                    "manual",
                    null)));

        Assert.Equal(02004, error.ErrorCode);
    }

    [Fact]
    public async Task ListSegmentsAsync_ReturnsSegmentsForTaskOrderedByStartsAt()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, UserId, "Ordered task");
        var otherTask = SeedTask(db, UserId, "Other task");
        db.Set<TaskExecutionSegmentEntity>().AddRange(
            new TaskExecutionSegmentEntity
            {
                TaskId = task.Id,
                UserId = UserId,
                StartsAt = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                Status = "planned",
                Source = "manual"
            },
            new TaskExecutionSegmentEntity
            {
                TaskId = task.Id,
                UserId = UserId,
                StartsAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                Status = "done",
                Source = "timer"
            },
            new TaskExecutionSegmentEntity
            {
                TaskId = otherTask.Id,
                UserId = UserId,
                StartsAt = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                Status = "planned",
                Source = "manual"
            });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var segments = await service.ListSegmentsAsync(task.Id);

        Assert.Equal(new[] { "done", "planned" }, segments.Select(s => s.Status).ToArray());
        Assert.All(segments, segment =>
        {
            Assert.Equal(task.Id, segment.TaskId);
            Assert.Equal("Ordered task", segment.TaskTitle);
        });
    }

    [Fact]
    public async Task DeleteSegmentAsync_SoftDeletesSegmentWithoutDeletingTask()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, UserId, "Delete segment only");
        var segment = new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
            Status = "planned",
            Source = "manual"
        };
        db.Set<TaskExecutionSegmentEntity>().Add(segment);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.DeleteSegmentAsync(task.Id, segment.Id);

        Assert.Empty(await service.ListSegmentsAsync(task.Id));
        Assert.NotNull(await db.Set<TaskEntity>().SingleOrDefaultAsync(t => t.Id == task.Id));
        var deletedSegment = await db.Set<TaskExecutionSegmentEntity>()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.Id == segment.Id);
        Assert.NotNull(deletedSegment.DeletedAt);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-task-execution-segments-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static PlanningModelService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId));

    private static TaskEntity SeedTask(PimDbContext db, Guid userId, string title)
    {
        var task = new TaskEntity
        {
            UserId = userId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title
        };
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
