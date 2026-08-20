using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarServiceRecurrencePathTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class TrackingRecurrenceService : RecurrenceService
    {
        public int ExpandEventsCalls { get; private set; }
        public int ExpandEventsV2Calls { get; private set; }
        public TrackingRecurrenceService() : base(NullLogger<RecurrenceService>.Instance) { }
        public override List<ExpandedEvent> ExpandEvents(IEnumerable<EventEntity> events, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
        {
            ExpandEventsCalls++;
            return base.ExpandEvents(events, rangeStart, rangeEnd);
        }
        public override List<ExpandedEvent> ExpandEventsV2(IEnumerable<EventEntity> events, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
        {
            ExpandEventsV2Calls++;
            return base.ExpandEventsV2(events, rangeStart, rangeEnd);
        }
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"cal-path-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarEntity SeedCalendar(PimDbContext db)
    {
        var cal = new CalendarEntity { UserId = UserId, Name = "Test", Kind = "calendar" };
        db.Set<CalendarEntity>().Add(cal);
        return cal;
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    [Fact]
    public async Task GetEventsAsync_UsesExpandEventsV2()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Calendar = cal, CalendarId = cal.Id, Uid = "u1", Title = "Single",
            DtStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
        var tracking = new TrackingRecurrenceService();
        var service = new CalendarService(db, new FixedCurrentUserService(UserId), tracking);
        await service.GetEventsAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), default);
        Assert.Equal(1, tracking.ExpandEventsV2Calls);
        Assert.Equal(0, tracking.ExpandEventsCalls);
    }

    [Fact]
    public async Task GetEventsPagedAsync_UsesExpandEventsV2()
    {
        await using var db = CreateDb();
        var cal = SeedCalendar(db);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Calendar = cal, CalendarId = cal.Id, Uid = "u2", Title = "Single2",
            DtStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
        var tracking = new TrackingRecurrenceService();
        var service = new CalendarService(db, new FixedCurrentUserService(UserId), tracking);
        await service.GetEventsPagedAsync(null, null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 1, 10, default);
        Assert.Equal(1, tracking.ExpandEventsV2Calls);
        Assert.Equal(0, tracking.ExpandEventsCalls);
    }

    [Fact]
    public void MapExpanded_IsCancelled_MappedToEventResponse()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(), CalendarId = Guid.NewGuid(), Uid = "x", Title = "Cancelled",
            DtStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
            Status = "CANCELLED",
            IsException = true,
        };
        var expanded = new ExpandedEvent(entity, entity.Id, entity.DtStart, entity.DtEnd, "2026-01-01T09:00:00.0000000+00:00", false, true, Guid.NewGuid());
        Assert.True(expanded.IsCancelled);
        var response = EventResponseMapper.MapExpanded(expanded);
        Assert.True(response.IsCancelled);
        Assert.Equal("CANCELLED", response.Status);
    }
}
