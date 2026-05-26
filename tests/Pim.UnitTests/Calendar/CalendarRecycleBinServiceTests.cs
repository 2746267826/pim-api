using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarRecycleBinServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task ListAsync_ReturnsDeletedEventsAndTasksOnly()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var taskBook = SeedCalendar(db, "Tasks", "task");
        SeedEvent(db, calendar, "Active event");
        var deletedEvent = SeedEvent(db, calendar, "Deleted event", deletedAt: DateTimeOffset.UtcNow.AddHours(-1));
        SeedTask(db, "Active task", taskBook);
        var deletedTask = SeedTask(db, "Deleted task", taskBook, deletedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.ListAsync("all", null, null, null, 1, 50);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, item => item.Id == deletedEvent.Id && item.Type == "event");
        Assert.Contains(result.Items, item => item.Id == deletedTask.Id && item.Type == "task");
        Assert.DoesNotContain(result.Items, item => item.Title == "Active event");
        Assert.DoesNotContain(result.Items, item => item.Title == "Active task");
    }

    [Fact]
    public async Task RestoreEventAsync_ReturnsConflictWhenEquivalentActiveEventExists()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var deleted = SeedEvent(db, calendar, "Standup", deletedAt: DateTimeOffset.UtcNow.AddHours(-1));
        SeedEvent(db, calendar, "Standup");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRestoreAsync("event", deleted.Id);

        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("same-title-time", conflict.Reason);
        Assert.False(preview.CanRestoreWithoutConflict);
    }

    [Fact]
    public async Task RestoreEventAsCopy_ClearsDeletedAtAndCreatesNewUid()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var deleted = SeedEvent(db, calendar, "Standup", deletedAt: DateTimeOffset.UtcNow.AddHours(-1));
        deleted.SourceUid = "source-standup";
        var originalUid = deleted.Uid;
        SeedEvent(db, calendar, "Standup");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.RestoreAsync("event", deleted.Id, new CalendarRestoreRequest(RestoreAsCopy: true));

        Assert.Equal("calendar.recycle_bin.restore_copy", result.Operation);
        var restored = await db.Set<EventEntity>().SingleAsync(e => e.Id == deleted.Id);
        Assert.Null(restored.DeletedAt);
        Assert.Null(restored.DeletedByOperationId);
        Assert.Null(restored.DeletedByOperationKind);
        Assert.NotEqual(originalUid, restored.Uid);
        Assert.Null(restored.SourceUid);
    }

    [Fact]
    public async Task RestoreCalendar_RestoresOnlyChildrenFromSameOperation()
    {
        await using var db = CreateDb();
        var operationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var earlierOperationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var calendar = SeedCalendar(db, "Work", "calendar", deletedAt: DateTimeOffset.UtcNow.AddHours(-1), operationId: operationId);
        var sameOperationChild = SeedEvent(db, calendar, "Deleted with book", deletedAt: DateTimeOffset.UtcNow.AddHours(-1), operationId: operationId);
        var earlierDeletedChild = SeedEvent(db, calendar, "Earlier deleted", deletedAt: DateTimeOffset.UtcNow.AddDays(-1), operationId: earlierOperationId);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.RestoreAsync("calendar", calendar.Id, new CalendarRestoreRequest());

        Assert.Equal(2, result.AffectedCount);
        Assert.Contains(calendar.Id, result.AffectedIds);
        Assert.Contains(sameOperationChild.Id, result.AffectedIds);
        var restoredCalendar = await db.Set<CalendarEntity>().SingleAsync(c => c.Id == calendar.Id);
        var restoredChild = await db.Set<EventEntity>().SingleAsync(e => e.Id == sameOperationChild.Id);
        var stillDeletedChild = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == earlierDeletedChild.Id);
        Assert.Null(restoredCalendar.DeletedAt);
        Assert.Null(restoredChild.DeletedAt);
        Assert.NotNull(stillDeletedChild.DeletedAt);
        Assert.Equal(earlierOperationId, stillDeletedChild.DeletedByOperationId);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-recycle-bin-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarRecycleBinService CreateService(PimDbContext db)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new CalendarAuditWriter(new AuditLogService(db)));

    private static CalendarEntity SeedCalendar(
        PimDbContext db,
        string name,
        string kind,
        DateTimeOffset? deletedAt = null,
        Guid? operationId = null)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = name,
            Kind = kind,
            Color = "#2563EB",
            DeletedAt = deletedAt,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = operationId is null ? null : (kind == "task" ? "task-book" : "calendar-book")
        };
        db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    private static EventEntity SeedEvent(
        PimDbContext db,
        CalendarEntity calendar,
        string title,
        DateTimeOffset? deletedAt = null,
        Guid? operationId = null)
    {
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            DeletedAt = deletedAt,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = operationId is null ? null : "calendar-book"
        };
        db.Set<EventEntity>().Add(evt);
        return evt;
    }

    private static TaskEntity SeedTask(
        PimDbContext db,
        string title,
        CalendarEntity? calendar = null,
        DateTimeOffset? deletedAt = null,
        Guid? operationId = null)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            Calendar = calendar,
            CalendarId = calendar?.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            Due = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            DeletedAt = deletedAt,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = operationId is null ? null : "task-book"
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
