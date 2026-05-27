using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarTaskPlanningTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task PlanTaskAsync_SetsPlannedRangeWithoutCreatingEvent()
    {
        await using var db = CreateDb();
        var task = new TaskEntity { UserId = UserId, Uid = "task@pim", Title = "Write plan", IsInbox = true };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var planned = await service.PlanTaskAsync(task.Id, new PlanTaskRequest(
            new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 26, 10, 30, 0, TimeSpan.Zero),
            "PT1H30M"));

        Assert.Equal(task.Id, planned.Id);
        Assert.False(planned.IsInbox);
        Assert.Equal(new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero), planned.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 5, 26, 10, 30, 0, TimeSpan.Zero), planned.PlannedEnd);
        Assert.Equal("01:30:00", planned.EstimatedDuration);
        Assert.Empty(await db.Set<EventEntity>().ToListAsync());
    }

    [Fact]
    public async Task PlanTaskAsync_PreservesExistingEstimatedDurationWhenOmitted()
    {
        await using var db = CreateDb();
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "task@pim",
            Title = "Write plan",
            IsInbox = true,
            EstimatedDuration = TimeSpan.FromHours(2)
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var planned = await service.PlanTaskAsync(task.Id, new PlanTaskRequest(
            new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            null,
            null));

        Assert.Equal("02:00:00", planned.EstimatedDuration);
        Assert.Equal(TimeSpan.FromHours(2), (await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id)).EstimatedDuration);
    }

    [Fact]
    public async Task GetTasksPagedAsync_FiltersSearchStatusAndPriority()
    {
        await using var db = CreateDb();
        db.Set<TaskEntity>().AddRange(
            new TaskEntity { UserId = UserId, Uid = "a@pim", Title = "Alpha deep work", Priority = 1, Status = "NEEDS-ACTION" },
            new TaskEntity { UserId = UserId, Uid = "b@pim", Title = "Beta admin", Priority = 3, Status = "COMPLETED" });
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var result = await service.GetTasksPagedAsync(
            inbox: null,
            search: "Alpha",
            calendarId: null,
            status: "NEEDS-ACTION",
            priority: 1,
            plannedFrom: null,
            plannedTo: null,
            dueFrom: null,
            dueTo: null,
            page: 1,
            pageSize: 20);

        var item = Assert.Single(result.Items);
        Assert.Equal("Alpha deep work", item.Title);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_UpdatesStatusForRequestedTasksOnly()
    {
        await using var db = CreateDb();
        var a = new TaskEntity { UserId = UserId, Uid = "a@pim", Title = "A", Status = "NEEDS-ACTION" };
        var b = new TaskEntity { UserId = UserId, Uid = "b@pim", Title = "B", Status = "NEEDS-ACTION" };
        db.Set<TaskEntity>().AddRange(a, b);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var result = await service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(new[] { a.Id }, "COMPLETED", null, null));

        Assert.Equal(1, result.AffectedCount);
        Assert.Equal("COMPLETED", (await db.Set<TaskEntity>().SingleAsync(t => t.Id == a.Id)).Status);
        Assert.Equal("NEEDS-ACTION", (await db.Set<TaskEntity>().SingleAsync(t => t.Id == b.Id)).Status);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_RejectsAnotherUsersCalendar()
    {
        await using var db = CreateDb();
        var task = new TaskEntity { UserId = UserId, Uid = "a@pim", Title = "A", IsInbox = true };
        var otherUsersCalendar = new CalendarEntity
        {
            UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "Other user's tasks",
            Kind = "task"
        };
        db.Set<TaskEntity>().Add(task);
        db.Set<CalendarEntity>().Add(otherUsersCalendar);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(new[] { task.Id }, null, null, otherUsersCalendar.Id)));

        Assert.Equal(02003, exception.ErrorCode);
        Assert.Equal("Calendar not found", exception.Message);
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        Assert.Null(stored.CalendarId);
        Assert.True(stored.IsInbox);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_AssignsCurrentUsersCalendarAndClearsInbox()
    {
        await using var db = CreateDb();
        var task = new TaskEntity { UserId = UserId, Uid = "a@pim", Title = "A", IsInbox = true };
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Planning",
            Kind = "task"
        };
        db.Set<TaskEntity>().Add(task);
        db.Set<CalendarEntity>().Add(calendar);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var result = await service.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(new[] { task.Id }, null, null, calendar.Id));

        Assert.Equal(1, result.AffectedCount);
        var stored = await db.Set<TaskEntity>().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(calendar.Id, stored.CalendarId);
        Assert.False(stored.IsInbox);
        Assert.Equal("Planning", Assert.Single(result.Samples).BookName);
    }

    [Fact]
    public async Task BatchUpdateTasksAsync_ReturnsNoTasksUpdatedWhenNoTasksMatch()
    {
        await using var db = CreateDb();
        var service = CreateCalendarService(db);

        var result = await service.BatchUpdateTasksAsync(
            new BatchTaskUpdateRequest(new[] { Guid.NewGuid() }, "COMPLETED", null, null));

        Assert.Equal(0, result.AffectedCount);
        Assert.Equal("No tasks updated", result.Message);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-task-planning-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateCalendarService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
