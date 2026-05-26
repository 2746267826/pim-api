using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarDeleteServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task DeleteEventAsync_SoftDeletesEventAndWritesAudit()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var evt = SeedEvent(db, calendar, "Focus block");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.DeleteEventAsync(evt.Id);

        Assert.Equal("calendar.events.delete", result.Operation);
        Assert.Equal(1, result.AffectedCount);
        var deleted = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal(result.OperationId, deleted.DeletedByOperationId);
        Assert.Equal("single-event", deleted.DeletedByOperationKind);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("calendar.events.delete", audit.Action);
    }

    [Fact]
    public async Task DeleteCalendarBookAsync_DeletesOnlyActiveChildrenWithSameOperationId()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var active = SeedEvent(db, calendar, "Active child");
        var alreadyDeleted = SeedEvent(db, calendar, "Earlier child");
        alreadyDeleted.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        alreadyDeleted.DeletedByOperationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        alreadyDeleted.DeletedByOperationKind = "single-event";
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewCalendarDeleteAsync(calendar.Id);
        Assert.Equal(1, preview.AffectedCount);
        Assert.Contains(preview.Samples, sample => sample.Title == "Active child");

        var result = await service.DeleteCalendarAsync(calendar.Id);

        var deletedCalendar = await db.Set<CalendarEntity>().IgnoreQueryFilters().SingleAsync(c => c.Id == calendar.Id);
        var deletedActive = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == active.Id);
        var untouchedEarlier = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == alreadyDeleted.Id);
        Assert.Equal(result.OperationId, deletedCalendar.DeletedByOperationId);
        Assert.Equal(result.OperationId, deletedActive.DeletedByOperationId);
        Assert.NotEqual(result.OperationId, untouchedEarlier.DeletedByOperationId);
        Assert.Equal("calendar-book", deletedCalendar.DeletedByOperationKind);
        Assert.Equal("calendar-book", deletedActive.DeletedByOperationKind);
    }

    [Fact]
    public async Task BatchDeleteTasksAsync_UsesOneOperationIdForAllTasks()
    {
        await using var db = CreateDb();
        var taskA = SeedTask(db, "A");
        var taskB = SeedTask(db, "B");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.BatchDeleteTasksAsync(new[] { taskA.Id, taskB.Id });

        Assert.Equal(2, result.AffectedCount);
        var deleted = await db.Set<TaskEntity>().IgnoreQueryFilters().OrderBy(t => t.Title).ToListAsync();
        Assert.All(deleted, task => Assert.Equal(result.OperationId, task.DeletedByOperationId));
        Assert.All(deleted, task => Assert.Equal("batch-task", task.DeletedByOperationKind));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-delete-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarDeleteService CreateService(PimDbContext db)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new CalendarAuditWriter(new AuditLogService(db)));

    private static CalendarEntity SeedCalendar(PimDbContext db, string name, string kind)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = name,
            Kind = kind,
            Color = "#2563EB"
        };
        db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    private static EventEntity SeedEvent(PimDbContext db, CalendarEntity calendar, string title)
    {
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero)
        };
        db.Set<EventEntity>().Add(evt);
        return evt;
    }

    private static TaskEntity SeedTask(PimDbContext db, string title)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
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
