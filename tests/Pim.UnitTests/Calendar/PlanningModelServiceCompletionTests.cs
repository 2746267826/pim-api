using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class PlanningModelServiceCompletionTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task CalendarLayersReturnEventsSegmentsHabitsAvailabilityAndAiPlaceholders()
    {
        await using var db = CreateDb();
        var start = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Work",
            Color = "#3B82F6"
        };
        var calendarEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "event@pim",
            Title = "Standup",
            DtStart = start,
            DtEnd = start.AddMinutes(30),
            Source = "manual"
        };
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "task@pim",
            Title = "Write plan",
            Source = "manual"
        };
        var segment = new TaskExecutionSegmentEntity
        {
            Task = task,
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = start.AddHours(1),
            EndsAt = start.AddHours(2),
            Status = "planned",
            Source = "manual"
        };
        var habit = new HabitRoutineEntity
        {
            UserId = UserId,
            Title = "Morning review",
            Cadence = "Daily",
            Source = "manual",
            Status = "Active"
        };
        var habitOccurrence = new HabitOccurrenceEntity
        {
            HabitRoutine = habit,
            HabitRoutineId = habit.Id,
            UserId = UserId,
            StartsAt = start.AddHours(3),
            EndsAt = start.AddHours(3).AddMinutes(20),
            Status = "Planned",
            Source = "manual"
        };
        var availability = new AvailabilityWindowEntity
        {
            UserId = UserId,
            Title = "Deep work window",
            StartsAt = start.AddHours(4),
            EndsAt = start.AddHours(6),
            Kind = "available",
            Source = "manual"
        };
        var placeholder = new AiPlanningPlaceholderEntity
        {
            UserId = UserId,
            Title = "AI focus slot",
            StartsAt = start.AddHours(6),
            EndsAt = start.AddHours(7),
            Reason = "Open focus time",
            Source = "ai",
            Status = "Suggested",
            ConfirmationId = Guid.NewGuid()
        };
        db.AddRange(calendar, calendarEvent, task, segment, habit, habitOccurrence, availability, placeholder);
        await db.SaveChangesAsync();
        var service = CreatePlanningService(db);

        var result = await service.GetCalendarLayersAsync(new CalendarLayerQuery(
            start,
            start.AddHours(8),
            ["events", "task-segments", "habits", "availability", "ai-placeholders"]));

        Assert.Contains(result.Items, x => x.Layer == "events" && x.ObjectId == calendarEvent.Id);
        Assert.Contains(result.Items, x => x.Layer == "task-segments" && x.ObjectId == segment.Id);
        Assert.Contains(result.Items, x => x.Layer == "habits" && x.ObjectId == habitOccurrence.Id);
        Assert.Contains(result.Items, x => x.Layer == "availability" && x.ObjectId == availability.Id);
        Assert.Contains(result.Items, x => x.Layer == "ai-placeholders" && x.ObjectId == placeholder.Id && x.RequiresConfirmation);
    }

    [Fact]
    public async Task BasicTaskCanHaveMultipleNonOverlappingSegments()
    {
        await using var db = CreateDb();
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "task@pim",
            Title = "Write plan",
            IsInbox = true
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();
        var service = CreatePlanningService(db);

        var first = await service.CreateSegmentAsync(
            task.Id,
            new CreateTaskExecutionSegmentRequest(
                new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
                "planned",
                "manual",
                null));
        var second = await service.CreateSegmentAsync(
            task.Id,
            new CreateTaskExecutionSegmentRequest(
                new DateTimeOffset(2026, 7, 8, 14, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 8, 15, 0, 0, TimeSpan.Zero),
                "planned",
                "manual",
                null));

        Assert.NotEqual(first.Id, second.Id);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new PimDbContext(options);
    }

    private static PlanningModelService CreatePlanningService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
