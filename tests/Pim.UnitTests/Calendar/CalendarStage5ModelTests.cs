using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarStage5ModelTests
{
    [Fact]
    public async Task EventEntity_PreservesOutlookImportMetadata()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var calendar = new CalendarEntity
        {
            UserId = userId,
            Name = "Outlook",
            Kind = "calendar",
            IsDefault = true
        };
        var evt = new EventEntity
        {
            CalendarId = calendar.Id,
            Calendar = calendar,
            Uid = "local-uid@pim",
            SourceUid = "outlook-source-uid",
            Title = "Outlook all day",
            DtStart = new DateTimeOffset(2026, 5, 26, 0, 0, 0, TimeSpan.FromHours(8)),
            DtEnd = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.FromHours(8)),
            IsAllDay = true,
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time",
            Source = "outlook-ics",
            SourceIcsComponent = "BEGIN:VEVENT\r\nUID:outlook-source-uid\r\nEND:VEVENT",
            ExternalMetadataJson = "{\"organizer\":\"mailto:owner@example.com\"}",
            RecurrenceId = "20260526T090000",
            ExDatesJson = "[\"2026-05-27\"]",
            RecurrenceMetadataJson = "{\"exceptionCount\":1}"
        };

        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();

        var saved = await db.Set<EventEntity>().SingleAsync();
        Assert.True(saved.IsAllDay);
        Assert.Equal("Asia/Shanghai", saved.TimeZoneId);
        Assert.Equal("China Standard Time", saved.SourceTimeZoneId);
        Assert.Equal("outlook-ics", saved.Source);
        Assert.Equal("outlook-source-uid", saved.SourceUid);
        Assert.Contains("BEGIN:VEVENT", saved.SourceIcsComponent);
        Assert.Contains("organizer", saved.ExternalMetadataJson);
        Assert.Equal("20260526T090000", saved.RecurrenceId);
        Assert.Contains("2026-05-27", saved.ExDatesJson);
        Assert.Contains("exceptionCount", saved.RecurrenceMetadataJson);
    }

    [Fact]
    public async Task CalendarTaskAndEvent_SupportDeleteOperationTracking()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var operationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var calendar = new CalendarEntity
        {
            UserId = userId,
            Name = "Work",
            Kind = "calendar",
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = "calendar-book"
        };
        var task = new TaskEntity
        {
            UserId = userId,
            Uid = "task@pim",
            Title = "Planned work",
            PlannedEnd = new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero),
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = "task-book"
        };
        var evt = new EventEntity
        {
            CalendarId = calendar.Id,
            Calendar = calendar,
            Uid = "event@pim",
            Title = "Deleted event",
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedByOperationId = operationId,
            DeletedByOperationKind = "calendar-book"
        };

        db.Set<CalendarEntity>().Add(calendar);
        db.Set<TaskEntity>().Add(task);
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Set<CalendarEntity>().ToListAsync());
        Assert.Empty(await db.Set<EventEntity>().ToListAsync());
        Assert.Empty(await db.Set<TaskEntity>().ToListAsync());

        var deletedCalendar = await db.Set<CalendarEntity>().IgnoreQueryFilters().SingleAsync();
        var deletedTask = await db.Set<TaskEntity>().IgnoreQueryFilters().SingleAsync();
        var deletedEvent = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(operationId, deletedCalendar.DeletedByOperationId);
        Assert.Equal(operationId, deletedTask.DeletedByOperationId);
        Assert.Equal(operationId, deletedEvent.DeletedByOperationId);
        Assert.Equal("calendar-book", deletedCalendar.DeletedByOperationKind);
        Assert.Equal("task-book", deletedTask.DeletedByOperationKind);
        Assert.Equal("calendar-book", deletedEvent.DeletedByOperationKind);
        Assert.Equal(new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero), deletedTask.PlannedEnd);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsSourceIcsComponent()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var calendar = new CalendarEntity
        {
            UserId = userId,
            Name = "Outlook",
            Kind = "calendar",
            IsDefault = true
        };
        var evt = new EventEntity
        {
            CalendarId = calendar.Id,
            Calendar = calendar,
            Uid = "source-ics@pim",
            Title = "Mapped event",
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            SourceIcsComponent = "BEGIN:VEVENT\r\nUID:source-ics@pim\r\nEND:VEVENT"
        };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = new CalendarService(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

        var events = await service.GetEventsAsync(
            new DateTimeOffset(2026, 5, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        var response = Assert.Single(events);
        Assert.Contains("BEGIN:VEVENT", response.SourceIcsComponent);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-stage5-model-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
