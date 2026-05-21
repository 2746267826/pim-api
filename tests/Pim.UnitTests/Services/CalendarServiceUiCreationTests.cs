using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class CalendarServiceUiCreationTests
{
    [Fact]
    public async Task CreateEventAsync_WithEmptyCalendarId_UsesDefaultCalendar()
    {
        var (service, db, userId) = CreateService();
        var start = new DateTimeOffset(2026, 5, 21, 9, 0, 0, TimeSpan.Zero);

        var created = await service.CreateEventAsync(
            new CreateEventRequest(Guid.Empty, "UI event", null, null, start, start.AddHours(1), null),
            CancellationToken.None);

        var calendar = await db.Set<CalendarEntity>().SingleAsync(CancellationToken.None);

        Assert.Equal(calendar.Id, created.CalendarId);
        Assert.Equal(userId, calendar.UserId);
        Assert.True(calendar.IsDefault);
    }

    private static (CalendarService Service, PimDbContext Db, Guid UserId) CreateService()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PimDbContext(options);
        var service = new CalendarService(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

        return (service, db, userId);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
