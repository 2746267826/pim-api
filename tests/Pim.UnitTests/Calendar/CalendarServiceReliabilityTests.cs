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

public class CalendarServiceReliabilityTests
{
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CreateEventAsync_NormalizesPlus08ToUtc()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id,
                "Test event",
                null,
                null,
                new DateTimeOffset(2026, 7, 20, 14, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.FromHours(8)),
                null),
            default);

        Assert.Equal(new DateTimeOffset(2026, 7, 20, 6, 0, 0, TimeSpan.Zero), response.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 7, 0, 0, TimeSpan.Zero), response.DtEnd);
        Assert.Equal(TimeSpan.Zero, response.DtStart.Offset);
        Assert.Equal(TimeSpan.Zero, response.DtEnd.Offset);

        var entity = await db.Set<EventEntity>().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 6, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 7, 0, 0, TimeSpan.Zero), entity.DtEnd);
    }

    [Fact]
    public async Task CreateEventAsync_EndEqualsStart_Returns02010()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                new CreateEventRequest(
                    calendar.Id, "Test", null, null,
                    start, start, null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateEventAsync_EndBeforeStart_Returns02010()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                new CreateEventRequest(
                    calendar.Id, "Test", null, null,
                    new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                    null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateEventAsync_NormalizesAndValidatesRange()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = SeedEvent(db, calendar, "Original event");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.UpdateEventAsync(
            evt.Id,
            new UpdateEventRequest(
                calendar.Id, "Updated", null, null,
                new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.FromHours(8)),
                null),
            default);

        Assert.Equal(new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero), response.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 7, 0, 0, TimeSpan.Zero), response.DtEnd);
        Assert.Equal(TimeSpan.Zero, response.DtStart.Offset);
        Assert.Equal(TimeSpan.Zero, response.DtEnd.Offset);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 7, 0, 0, TimeSpan.Zero), entity.DtEnd);
    }

    [Fact]
    public async Task UpdateEventAsync_EndEqualsStart_Returns02010()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = SeedEvent(db, calendar, "Original event");
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(
                evt.Id,
                new UpdateEventRequest(
                    calendar.Id, "Updated", null, null,
                    start, start, null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateEventAsync_EndBeforeStart_Returns02010()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = SeedEvent(db, calendar, "Original event");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(
                evt.Id,
                new UpdateEventRequest(
                    calendar.Id, "Updated", null, null,
                    new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                    null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateEventAsync_InvalidRange_DoesNotMutateTrackedEntity()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = SeedEvent(db, calendar, "Original title");
        evt.Description = "Original description";
        evt.Location = "Original location";
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(
                evt.Id,
                new UpdateEventRequest(
                    calendar.Id, "Hacked title", "Hacked description", "Hacked location",
                    start, start, null),
                default));

        Assert.Equal(02010, ex.ErrorCode);

        Assert.Equal("Original title", evt.Title);
        Assert.Equal("Original description", evt.Description);
        Assert.Equal("Original location", evt.Location);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero), evt.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), evt.DtEnd);
    }

    // ========== CreateTaskAsync ==========

    [Fact]
    public async Task CreateTaskAsync_NormalizesPlus08ToUtc()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateTaskAsync(
            new CreateTaskRequest(
                cal.Id, "Task title", null, 0,
                null, null,
                new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.FromHours(8)),
                null,
                new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.FromHours(8))),
            default);

        Assert.Equal(new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero), response.Due);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), response.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero), response.PlannedEnd);
        Assert.Equal(TimeSpan.Zero, response.Due!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.DtStart!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.PlannedEnd!.Value.Offset);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero), entity.Due);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero), entity.PlannedEnd);
    }

    [Fact]
    public async Task CreateTaskAsync_PlannedEndEqualsDtStart_Returns02010()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var dt = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateTaskAsync(
                new CreateTaskRequest(
                    cal.Id, "Task", null, 0, null, null, null, dt, null, dt),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateTaskAsync_PlannedEndBeforeDtStart_Returns02010()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateTaskAsync(
                new CreateTaskRequest(
                    cal.Id, "Task", null, 0, null, null, null,
                    new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
                    null,
                    new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero)),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Theory]
    [InlineData("PT0M")]
    [InlineData("PT30S")]
    public async Task CreateTaskAsync_EstimatedDurationTooShort_Returns02011(string duration)
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateTaskAsync(
                new CreateTaskRequest(
                    cal.Id, "Task", null, 0, duration, null, null, null, null, null),
                default));

        Assert.Equal(02011, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateTaskAsync_NullEstimatedDuration_ReturnsNull()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateTaskAsync(
            new CreateTaskRequest(
                cal.Id, "Task", null, 0, null, null, null, null, null, null),
            default);

        Assert.Null(response.EstimatedDuration);
        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.EstimatedDuration);
    }

    // ========== UpdateTaskAsync ==========

    [Fact]
    public async Task UpdateTaskAsync_NormalizesPlus08ToUtc()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Original");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.UpdateTaskAsync(
            task.Id,
            new UpdateTaskRequest(
                cal.Id, "Updated", "Desc", 1,
                null, null,
                new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.FromHours(8)),
                null,
                new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.FromHours(8))),
            default);

        Assert.Equal(new DateTimeOffset(2026, 7, 22, 6, 0, 0, TimeSpan.Zero), response.Due);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), response.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero), response.PlannedEnd);
        Assert.Equal(TimeSpan.Zero, response.Due!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.DtStart!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.PlannedEnd!.Value.Offset);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 6, 0, 0, TimeSpan.Zero), entity.Due);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero), entity.PlannedEnd);
    }

    [Fact]
    public async Task UpdateTaskAsync_EndBeforeStart_Returns02010_DoesNotMutate()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Original", t =>
        {
            t.Title = "Original";
            t.Description = "Orig desc";
            t.Priority = 5;
            t.DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateTaskAsync(
                task.Id,
                new UpdateTaskRequest(
                    cal.Id, "Hacked", "Hacked desc", 0,
                    null, null, null,
                    new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                    null,
                    new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero)),
                default));

        Assert.Equal(02010, ex.ErrorCode);

        Assert.Equal("Original", task.Title);
        Assert.Equal("Orig desc", task.Description);
        Assert.Equal(5, task.Priority);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero), task.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), task.PlannedEnd);
    }

    [Fact]
    public async Task UpdateTaskAsync_NullPlannedEndPreservesExisting()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Original", t =>
        {
            t.DtStart = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // new DtStart > existing PlannedEnd => must reject (8:30 start > 10:00 end is fine, so make it 10:30 start > 10:00 end)
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateTaskAsync(
                task.Id,
                new UpdateTaskRequest(
                    cal.Id, "Updated", null, 0,
                    null, null, null,
                    new DateTimeOffset(2026, 7, 20, 10, 30, 0, 0, TimeSpan.Zero),
                    null,
                    null), // null PlannedEnd preserves existing = 10:00
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateTaskAsync_NullDtStartClearsStart()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Original", t =>
        {
            t.DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.UpdateTaskAsync(
            task.Id,
            new UpdateTaskRequest(
                cal.Id, "Updated", null, 0,
                null, null,
                null,
                null, // null DtStart clears start
                null,
                new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero) // explicit end
                ),
            default);

        // DtStart should be null, so end <= start check should not trigger
        Assert.Null(response.DtStart);
        Assert.NotNull(response.PlannedEnd);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.DtStart);
        Assert.NotNull(entity.PlannedEnd);
    }

    [Theory]
    [InlineData("PT0M")]
    [InlineData("PT30S")]
    public async Task UpdateTaskAsync_EstimatedDurationTooShort_Returns02011(string duration)
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Original");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateTaskAsync(
                task.Id,
                new UpdateTaskRequest(
                    cal.Id, "Updated", null, 0, duration, null, null, null, null, null),
                default));

        Assert.Equal(02011, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateTaskAsync_NullEstimatedDurationClears()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "With duration", t =>
        {
            t.EstimatedDuration = TimeSpan.FromHours(2);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.UpdateTaskAsync(
            task.Id,
            new UpdateTaskRequest(
                cal.Id, "Updated", null, 0,
                null, null, null, null, null, null),
            default);

        Assert.Null(response.EstimatedDuration);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.EstimatedDuration);
    }

    // ========== PlanTaskAsync ==========

    [Fact]
    public async Task PlanTaskAsync_NormalizesPlus08ToUtc()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Plan me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.PlanTaskAsync(
            task.Id,
            new PlanTaskRequest(
                new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 23, 15, 0, 0, TimeSpan.FromHours(8)),
                null),
            default);

        Assert.Equal(new DateTimeOffset(2026, 7, 23, 6, 0, 0, TimeSpan.Zero), response.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 7, 0, 0, TimeSpan.Zero), response.PlannedEnd);
        Assert.Equal(TimeSpan.Zero, response.DtStart!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.PlannedEnd!.Value.Offset);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 6, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 7, 0, 0, TimeSpan.Zero), entity.PlannedEnd);
    }

    [Fact]
    public async Task PlanTaskAsync_EndBeforeStart_Returns02010()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Plan me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.PlanTaskAsync(
                task.Id,
                new PlanTaskRequest(
                    new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero),
                    null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task PlanTaskAsync_EstimatedDurationTooShort_Returns02011()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Plan me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.PlanTaskAsync(
                task.Id,
                new PlanTaskRequest(
                    new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero),
                    "PT30S"),
                default));

        Assert.Equal(02011, ex.ErrorCode);
    }

    [Fact]
    public async Task PlanTaskAsync_NullEstimatedDurationPreservesExisting()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Plan me", t =>
        {
            t.EstimatedDuration = TimeSpan.FromHours(2);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.PlanTaskAsync(
            task.Id,
            new PlanTaskRequest(
                new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero),
                null), // null -> preserve existing
            default);

        Assert.Equal("02:00:00", response.EstimatedDuration);
    }

    // ========== MoveTaskAsync ==========

    [Fact]
    public async Task MoveTaskAsync_NormalizesPlus08ToUtc()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MoveTaskAsync(
            task.Id,
            new MoveTaskRequest(
                new DateTimeOffset(2026, 7, 24, 14, 0, 0, TimeSpan.FromHours(8)),
                TimeSpan.FromHours(1),
                null,
                new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.FromHours(8))),
            default);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 6, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero), entity.PlannedEnd);
        Assert.Equal(TimeSpan.Zero, entity.DtStart!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, entity.PlannedEnd!.Value.Offset);
    }

    [Fact]
    public async Task MoveTaskAsync_ExplicitEndBeforeStart_Returns02010()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me", t =>
        {
            t.DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.MoveTaskAsync(
                task.Id,
                new MoveTaskRequest(
                    new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero)),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task MoveTaskAsync_OnlyScheduledStart_ValidatesAgainstExistingEnd()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me", t =>
        {
            t.DtStart = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // new start 11:00 > existing end 10:00 => reject
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.MoveTaskAsync(
                task.Id,
                new MoveTaskRequest(
                    new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                    null,
                    null,
                    null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task MoveTaskAsync_OnlyPlannedEnd_ValidatesAgainstExistingStart()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me", t =>
        {
            t.DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
            t.PlannedEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // explicit end 8:00 < existing start 9:00 => reject
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.MoveTaskAsync(
                task.Id,
                new MoveTaskRequest(
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero)),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    [Fact]
    public async Task MoveTaskAsync_DurationCalculatesEnd()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.MoveTaskAsync(
            task.Id,
            new MoveTaskRequest(
                new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
                TimeSpan.FromHours(2),
                null,
                null),
            default);

        var entity = await db.Set<TaskEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero), entity.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), entity.PlannedEnd);
    }

    [Fact]
    public async Task MoveTaskAsync_DurationCalculatedEndBeforeStart_Returns02010()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db, "Tasks", "task");
        var task = SeedTask(db, "Move me");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.MoveTaskAsync(
                task.Id,
                new MoveTaskRequest(
                    new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromHours(-2),
                    null,
                    null),
                default));

        Assert.Equal(02010, ex.ErrorCode);
    }

    // --- Helpers ---

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-reliability-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private static EventEntity SeedEvent(PimDbContext db, CalendarEntity calendar, string title)
    {
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
        };
        db.Set<EventEntity>().Add(evt);
        return evt;
    }

    private static CalendarEntity SeedCalendar(PimDbContext db, string name, string kind)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = name,
            Kind = kind,
        };
        db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    private static TaskEntity SeedTask(PimDbContext db, string title, Action<TaskEntity>? configure = null)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
        };
        configure?.Invoke(task);
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
