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

public class CalendarRecurrenceServiceTests
{
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId { get; } = id;
        public string? Role => "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var opts = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"cal-rec-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(opts);
    }

    private static CalendarEntity SeedCal(PimDbContext db)
    {
        var cal = new CalendarEntity { UserId = UserId, Name = "Test", Kind = "calendar" };
        db.Set<CalendarEntity>().Add(cal);
        return cal;
    }

    private static CalendarService Svc(PimDbContext db)
        => new(db, new FixedUser(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private static DateTimeOffset D(int y, int m, int d, int h = 10) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    // 3.2 create series (RRule+IsSeriesMaster)
    [Fact]
    public async Task CreateSeries_WithRRule_SetsIsSeriesMaster()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var resp = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        Assert.True(resp.IsSeriesMaster);
        Assert.False(resp.IsException);
        Assert.Equal("FREQ=WEEKLY;COUNT=4", resp.RRule);

        var e = await db.Set<EventEntity>().SingleAsync();
        Assert.True(e.IsSeriesMaster);
        Assert.False(e.IsException);
        Assert.Null(e.SeriesMasterId);
    }

    [Fact]
    public async Task CreateSeries_ExplicitIsSeriesMasterFalse_WithRRule_StillMaster()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var resp = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly2", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2",
            IsSeriesMaster: false), default);

        Assert.True(resp.IsSeriesMaster);
    }

    // IsException requires master + recurrenceId
    [Fact]
    public async Task CreateException_MissingMasterOrRecurrence_Throws()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        await Assert.ThrowsAsync<DomainException>(() => svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: null, RecurrenceId: null), default));

        await Assert.ThrowsAsync<DomainException>(() => svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: Guid.NewGuid(), RecurrenceId: null), default));
    }

    [Fact]
    public async Task CreateException_Success()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Master", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        var recId = D(2026, 1, 12).ToString("O");
        var ex = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId), default);

        Assert.True(ex.IsException);
        Assert.False(ex.IsSeriesMaster);
        Assert.Equal(master.Id, ex.SeriesMasterId);
        Assert.Equal(recId, ex.RecurrenceId);
        Assert.Null(ex.RRule);
    }

    // edit single occurrence -> creates exception (scope=this)
    [Fact]
    public async Task EditSingleOccurrence_ScopeThis_CreatesException()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        var recId = D(2026, 1, 12).ToString("O");
        var updated = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Rescheduled", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            RecurrenceId: recId), "this", default);

        Assert.True(updated.IsException);
        Assert.Equal(master.Id, updated.SeriesMasterId);
        Assert.Equal(recId, updated.RecurrenceId);
        // Ensure master not modified
        var masterEntity = await db.Set<EventEntity>().FirstAsync(e => e.Id == master.Id);
        Assert.Equal("Weekly", masterEntity.Title);

        // Second edit same occurrence should update existing exception, not duplicate
        var updated2 = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Rescheduled2", null, null, D(2026, 1, 12, 14), D(2026, 1, 12, 15), null,
            RecurrenceId: recId), "this", default);
        Assert.Equal(updated.Id, updated2.Id);
        Assert.Equal("Rescheduled2", updated2.Title);
        Assert.Equal(1, await db.Set<EventEntity>().CountAsync(e => e.IsException && e.SeriesMasterId == master.Id && e.RecurrenceId == recId));
    }

    // cancel single -> creates CANCELLED exception (via Create with exception crossing, and via Delete scope=this)
    [Fact]
    public async Task CancelSingle_ViaCreateExceptionWithCancelled_OverlayMarksCancelled()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        var recId = D(2026, 1, 19).ToString("O");
        // Create exception entity directly then set CANCELLED status via DB to simulate Cancel
        var masterEnt = await db.Set<EventEntity>().SingleAsync(e => e.Id == master.Id);
        var cancelledEx = new EventEntity
        {
            CalendarId = cal.Id,
            Uid = masterEnt.Uid,
            Title = "Cancelled",
            DtStart = D(2026, 1, 19),
            DtEnd = D(2026, 1, 19).AddHours(1),
            IsException = true,
            SeriesMasterId = master.Id,
            RecurrenceId = recId,
            Status = "CANCELLED",
        };
        db.Set<EventEntity>().Add(cancelledEx);
        await db.SaveChangesAsync();

        var expanded = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 2, 1), default);
        var occ = expanded.First(e => e.RecurrenceId == recId);
        Assert.True(occ.IsException);
        Assert.True(occ.IsCancelled);
        Assert.Equal("CANCELLED", occ.Status);
    }

    // edit series -> updates master (scope=series)
    [Fact]
    public async Task EditSeries_ScopeSeries_UpdatesMaster()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        var updated = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Weekly Renamed", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), "series", default);

        Assert.Equal(master.Id, updated.Id);
        Assert.Equal("Weekly Renamed", updated.Title);
        Assert.True(updated.IsSeriesMaster);
        Assert.Equal(0, await db.Set<EventEntity>().CountAsync(e => e.IsException));
    }

    // delete single -> cancelled exception (scope=this)
    [Fact]
    public async Task DeleteSingle_ScopeThis_CreatesCancelledException()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);

        var recId = D(2026, 1, 12).ToString("O");
        await svc.DeleteEventAsync(master.Id, "this", recId, default);

        var masterStill = await db.Set<EventEntity>().FirstOrDefaultAsync(e => e.Id == master.Id);
        Assert.NotNull(masterStill);
        Assert.Null(masterStill.DeletedAt);

        var ex = await db.Set<EventEntity>().SingleAsync(e => e.IsException && e.SeriesMasterId == master.Id && e.RecurrenceId == recId);
        Assert.Equal("CANCELLED", ex.Status);

        var expanded = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 2, 1), default);
        var occ = expanded.First(e => e.RecurrenceId == recId);
        Assert.True(occ.IsCancelled);
    }

    [Fact]
    public async Task DeleteSingle_ScopeThis_ExistingException_MarksCancelled()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);
        var recId = D(2026, 1, 12).ToString("O");
        var ex = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId), default);

        await svc.DeleteEventAsync(master.Id, "this", recId, default);
        var updatedEx = await db.Set<EventEntity>().FirstAsync(e => e.Id == ex.Id);
        Assert.Equal("CANCELLED", updatedEx.Status);
    }

    // delete series -> soft-delete master + exceptions
    [Fact]
    public async Task DeleteSeries_SoftDeletesMasterAndExceptions()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);
        var recId = D(2026, 1, 12).ToString("O");
        await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId), default);

        await svc.DeleteEventAsync(master.Id, "series", default);
        var m = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == master.Id);
        Assert.NotNull(m.DeletedAt);
        var e2 = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.IsException);
        Assert.NotNull(e2.DeletedAt);

        // No scope (default) also cascades
        await using var db2 = CreateDb();
        var cal2 = SeedCal(db2);
        await db2.SaveChangesAsync();
        var svc2 = Svc(db2);
        var master2 = await svc2.CreateEventAsync(new CreateEventRequest(
            cal2.Id, "Weekly2", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId2 = D(2026, 1, 12).ToString("O");
        await svc2.CreateEventAsync(new CreateEventRequest(
            cal2.Id, "Ex2", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master2.Id, RecurrenceId: recId2), default);
        await svc2.DeleteEventAsync(master2.Id, default);
        Assert.NotNull((await db2.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == master2.Id)).DeletedAt);
        Assert.NotNull((await db2.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.IsException)).DeletedAt);
    }

    // GetEvents filters legacy occurrences
    [Fact]
    public async Task GetEvents_FiltersLegacyOccurrenceRows()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        db.Set<EventEntity>().Add(new EventEntity
        {
            CalendarId = cal.Id,
            Calendar = cal,
            Uid = "legacy-uid",
            Title = "Legacy occurrence",
            DtStart = D(2026, 1, 10),
            DtEnd = D(2026, 1, 10).AddHours(1),
            OutlookEventType = "occurrence",
            IsSeriesMaster = false,
            SeriesMasterId = null,
            IsException = false,
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            CalendarId = cal.Id,
            Calendar = cal,
            Uid = "normal-uid",
            Title = "Normal",
            DtStart = D(2026, 1, 11),
            DtEnd = D(2026, 1, 11).AddHours(1),
        });
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var events = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 1, 31), default);
        Assert.DoesNotContain(events, e => e.Title == "Legacy occurrence");
        Assert.Contains(events, e => e.Title == "Normal");
    }

    [Fact]
    public async Task GetEvents_UsesExpandEventsV2_IsCancelledDerivedCorrectly()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId = D(2026, 1, 12).ToString("O");
        // Create exception then mark cancelled via delete
        await svc.DeleteEventAsync(master.Id, "this", recId, default);
        var expanded = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 1, 31), default);
        var cancelled = expanded.First(e => e.RecurrenceId == recId);
        Assert.True(cancelled.IsCancelled);
        Assert.True(cancelled.IsException);
    }
}
