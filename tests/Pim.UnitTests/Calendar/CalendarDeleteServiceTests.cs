using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarDeleteServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // --- Existing tests ---

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

    [Fact]
    public async Task BatchDeleteEventsAsync_EmptyIdsReturnsZeroWithoutAudit()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.BatchDeleteEventsAsync(Array.Empty<Guid>());

        Assert.Equal(0, result.AffectedCount);
        Assert.Empty(result.AffectedIds);
        Assert.Empty(result.Samples);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task BatchDeleteEventsAsync_NullIdsReturnsZeroWithoutAudit()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.BatchDeleteEventsAsync(null!);

        Assert.Equal(0, result.AffectedCount);
        Assert.Empty(result.AffectedIds);
        Assert.Empty(result.Samples);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task BatchDeleteTasksAsync_UnknownIdsReturnsZeroWithoutAudit()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.BatchDeleteTasksAsync(new[] { Guid.NewGuid() });

        Assert.Equal(0, result.AffectedCount);
        Assert.Empty(result.AffectedIds);
        Assert.Empty(result.Samples);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task BatchDeleteTasksAsync_AlreadyDeletedTaskReturnsZeroWithoutRetaggingOrAudit()
    {
        await using var db = CreateDb();
        var task = SeedTask(db, "Earlier task");
        var originalOperationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        task.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        task.DeletedByOperationId = originalOperationId;
        task.DeletedByOperationKind = "single-task";
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.BatchDeleteTasksAsync(new[] { task.Id });

        Assert.Equal(0, result.AffectedCount);
        Assert.Empty(result.AffectedIds);
        Assert.Empty(result.Samples);
        var untouched = await db.Set<TaskEntity>().IgnoreQueryFilters().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(originalOperationId, untouched.DeletedByOperationId);
        Assert.Equal("single-task", untouched.DeletedByOperationKind);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    // --- New TDD tests for Task 6B ---

    [Fact]
    public async Task CreateEventAsync_RejectsOutlookBoundCalendarWithoutMutation()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        SeedOutlookBinding(db, calendar.Id);
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(new CreateEventRequest(
                calendar.Id, "Test event", null, null,
                new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                null), default));

        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(db.Set<EventEntity>().IgnoreQueryFilters().ToList());
        Assert.Empty(db.AuditLogs.ToList());
    }

    [Fact]
    public async Task UpdateEventAsync_RejectsOutlookBoundEventWithoutMutation()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var evt = SeedEventWithOutlookBinding(db, calendar, "Bound event");
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(evt.Id, new UpdateEventRequest(
                calendar.Id, "Updated title", null, null,
                new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                null), default));

        Assert.Equal(02009, ex.ErrorCode);
        var unchanged = await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        Assert.Equal("Bound event", unchanged.Title);
    }

    [Fact]
    public async Task UpdateEventAsync_RejectsMoveIntoOutlookBoundCalendarWithoutMutation()
    {
        await using var db = CreateDb();
        var manualCalendar = SeedCalendar(db, "Manual", "calendar");
        var boundCalendar = SeedCalendar(db, "Bound", "calendar");
        SeedOutlookBinding(db, boundCalendar.Id);
        var evt = SeedEvent(db, manualCalendar, "Manual event");
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(evt.Id, new UpdateEventRequest(
                boundCalendar.Id, "Moved title", null, null,
                new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
                null), default));

        Assert.Equal(02009, ex.ErrorCode);
        var unchanged = await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        Assert.Equal("Manual event", unchanged.Title);
        Assert.Equal(manualCalendar.Id, unchanged.CalendarId);
    }

    [Fact]
    public async Task DeleteEventAsync_RejectsOutlookBoundEventWithoutAudit()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "Work", "calendar");
        var evt = SeedEventWithOutlookBinding(db, calendar, "Bound event");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.DeleteEventAsync(evt.Id));

        Assert.Equal(02009, ex.ErrorCode);
        var notDeleted = await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        Assert.Null(notDeleted.DeletedAt);
        Assert.Empty(db.AuditLogs.ToList());
    }

    [Fact]
    public async Task DeleteEventAsync_RejectsEventOnOutlookBoundCalendarEvenWithoutDirectBinding()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "BoundCal", "calendar");
        SeedOutlookBinding(db, calendar.Id);
        var evt = SeedEvent(db, calendar, "Orphan on bound calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.DeleteEventAsync(evt.Id));

        Assert.Equal(02009, ex.ErrorCode);
        var notDeleted = await db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        Assert.Null(notDeleted.DeletedAt);
        Assert.Empty(db.AuditLogs.ToList());
    }

    [Fact]
    public async Task BatchDeleteEventsAsync_RejectsMixedOutlookBatchAtomicallyWithoutAudit()
    {
        await using var db = CreateDb();
        var manualCalendar = SeedCalendar(db, "Manual", "calendar");
        var boundCalendar = SeedCalendar(db, "Bound", "calendar");
        SeedOutlookBinding(db, boundCalendar.Id);
        var manual = SeedEvent(db, manualCalendar, "Manual event");
        var bound = SeedEventWithOutlookBinding(db, boundCalendar, "Bound event");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.BatchDeleteEventsAsync(new[] { manual.Id, bound.Id }));

        Assert.Equal(02009, ex.ErrorCode);
        var both = await db.Set<EventEntity>().AsNoTracking().ToListAsync();
        Assert.All(both, e => Assert.Null(e.DeletedAt));
        Assert.Empty(db.AuditLogs.ToList());
    }

    // --- Helpers ---

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-delete-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    // ── 工单 #17 / F3：整本删除只删「当前真的还在本簿里」的任务 ──────────────

    [Fact]
    public async Task DeleteCalendarAsync_DoesNotDeleteTasksThatWereMovedOutBeforeTheDelete()
    {
        // 语义回归：先迁出、后删整本 —— 已迁出的任务必须留在原处。
        // （并发交错下的同一性质由真实 Postgres 脚本验证：删除方在取到任务锁后会**重读成员集合**。）
        await using var db = CreateDb();
        var source = SeedCalendar(db, "Source book", "task");
        var target = SeedCalendar(db, "Target book", "task");
        var stays = SeedTask(db, source, "留在本簿");
        var moved = SeedTask(db, source, "已迁出");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        moved.CalendarId = target.Id;
        await db.SaveChangesAsync();

        var result = await service.DeleteCalendarAsync(source.Id);

        // affectedIds 里含被删的任务簿自身，所以按「除本簿以外的 id」断言
        Assert.Equal(new[] { stays.Id }, result.AffectedIds.Where(id => id != source.Id).ToArray());
        db.ChangeTracker.Clear();
        var movedAfter = await db.Set<TaskEntity>().IgnoreQueryFilters().SingleAsync(t => t.Id == moved.Id);
        Assert.Null(movedAfter.DeletedAt);
        Assert.Equal(target.Id, movedAfter.CalendarId);
    }

    [Fact]
    public async Task DeleteCalendarAsync_DeletesEveryCurrentMember()
    {
        // 反面对照：没有迁出的成员必须被删干净（防止「重读成员」把集合读没了）。
        await using var db = CreateDb();
        var book = SeedCalendar(db, "Book", "task");
        var first = SeedTask(db, book, "A");
        var second = SeedTask(db, book, "B");
        var other = SeedTask(db, SeedCalendar(db, "Other", "task"), "C");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.DeleteCalendarAsync(book.Id);

        Assert.Equal(new[] { first.Id, second.Id }, result.AffectedIds.Where(id => id != book.Id).OrderBy(id => id).ToArray());
        db.ChangeTracker.Clear();
        Assert.All(
            await db.Set<TaskEntity>().IgnoreQueryFilters().Where(t => t.Id == first.Id || t.Id == second.Id).ToListAsync(),
            t => Assert.NotNull(t.DeletedAt));
        Assert.Null((await db.Set<TaskEntity>().IgnoreQueryFilters().SingleAsync(t => t.Id == other.Id)).DeletedAt);
    }

    private static TaskEntity SeedTask(PimDbContext db, CalendarEntity book, string title)
    {
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            CalendarId = book.Id,
            IsInbox = false
        };
        db.Set<TaskEntity>().Add(task);
        return task;
    }

    private static CalendarDeleteService CreateService(PimDbContext db)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new CalendarAuditWriter(new AuditLogService(db)));

    private static CalendarService CreateCalendarService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

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

    private static EventEntity SeedEventWithOutlookBinding(PimDbContext db, CalendarEntity calendar, string title)
    {
        var bindingId = Guid.NewGuid();
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = title,
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            OutlookCalendarBindingId = bindingId,
            OutlookEventId = "remote-id-1"
        };
        db.Set<EventEntity>().Add(evt);
        return evt;
    }

    private static OutlookCalendarBindingEntity SeedOutlookBinding(PimDbContext db, Guid pimCalendarId)
    {
        var binding = new OutlookCalendarBindingEntity
        {
            PimCalendarId = pimCalendarId,
            ConnectionId = Guid.NewGuid(),
            GraphCalendarId = "graph-cal-id",
            Name = "Outlook Calendar",
            CanEdit = true,
            IsSelected = true,
            RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        return binding;
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
