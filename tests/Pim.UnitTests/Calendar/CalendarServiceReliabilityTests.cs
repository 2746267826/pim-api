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

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
