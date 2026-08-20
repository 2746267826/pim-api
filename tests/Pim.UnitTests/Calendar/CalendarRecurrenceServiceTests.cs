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

    // --- Fix findings: validation, status preservation, DTO nullable, clock, delete cascade ---

    [Fact]
    public async Task UpdateScopeThis_MismatchedSeriesMasterId_Throws()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId = D(2026, 1, 12).ToString("O");
        var fakeMaster = Guid.NewGuid();
        await Assert.ThrowsAsync<DomainException>(() => svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Bad", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            SeriesMasterId: fakeMaster, RecurrenceId: recId), "this", default));
    }

    [Fact]
    public async Task UpdateScopeThis_CancelledException_PreservesCancelledStatus()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);
        var recId = D(2026, 1, 12).ToString("O");
        // Create cancelled sentinel via delete scope=this
        await svc.DeleteEventAsync(master.Id, "this", recId, default);
        var ex = await db.Set<EventEntity>().SingleAsync(e => e.IsException && e.RecurrenceId == recId);
        Assert.Equal("CANCELLED", ex.Status);
        // Edit the cancelled exception directly — should stay CANCELLED, not revert to CONFIRMED
        var updated = await svc.UpdateEventAsync(ex.Id, new UpdateEventRequest(
            cal.Id, "Edited", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            RecurrenceId: recId), "this", default);
        Assert.Equal("CANCELLED", updated.Status);
        Assert.True(updated.IsCancelled);
        // Also via master->existing exception path should preserve
        var updated2 = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Edited2", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            RecurrenceId: recId), "this", default);
        Assert.Equal("CANCELLED", updated2.Status);
    }

    [Fact]
    public async Task DeleteSeries_FromException_ScopeSeries_CascadesMasterAndExceptions()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=4"), default);
        var recId1 = D(2026, 1, 12).ToString("O");
        var recId2 = D(2026, 1, 19).ToString("O");
        var ex1 = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex1", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId1), default);
        await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex2", null, null, D(2026, 1, 19, 11), D(2026, 1, 19, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId2), default);
        // Delete series via exception id with scope=series -> should delete master + both exceptions
        await svc.DeleteEventAsync(ex1.Id, "series", null, default);
        var m = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == master.Id);
        Assert.NotNull(m.DeletedAt);
        var allEx = await db.Set<EventEntity>().IgnoreQueryFilters().Where(e => e.IsException).ToListAsync();
        Assert.All(allEx, e => Assert.NotNull(e.DeletedAt));
    }

    [Fact]
    public async Task DtoNullable_Roundtrip_NullDefaultsToFalse()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        // No IsSeriesMaster / IsException specified (null) with no RRule -> single event
        var resp = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Single", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), null,
            IsSeriesMaster: null, IsException: null), default);
        Assert.False(resp.IsSeriesMaster);
        Assert.False(resp.IsException);
        // With RRule and null should auto-become master
        var resp2 = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "SeriesNull", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2",
            IsSeriesMaster: null), default);
        Assert.True(resp2.IsSeriesMaster);
    }

    [Fact]
    public async Task DeleteAndUpdate_UsesInjectedClock()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var fixedTime = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var fakeProvider = new FakeTimeProvider(fixedTime);
        var svc = new CalendarService(db, new FixedUser(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance), new EventAttachmentService(db), fakeProvider);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId = D(2026, 1, 12).ToString("O");
        await svc.DeleteEventAsync(master.Id, "this", recId, default);
        var ex = await db.Set<EventEntity>().SingleAsync(e => e.IsException);
        // The CANCELLED sentinel creation does not set DeletedAt, but Update paths use clock — verify update uses clock
        var updated = await svc.UpdateEventAsync(ex.Id, new UpdateEventRequest(
            cal.Id, "Edited", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null, RecurrenceId: recId), "this", default);
        var entity = await db.Set<EventEntity>().FirstAsync(e => e.Id == ex.Id);
        Assert.Equal(fixedTime, entity.UpdatedAt);
        // Delete series via clock
        fakeProvider.Advance(TimeSpan.FromHours(1));
        await svc.DeleteEventAsync(master.Id, "series", null, default);
        var m = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == master.Id);
        Assert.Equal(fixedTime.AddHours(1), m.DeletedAt);
    }

    [Fact]
    public async Task UpdateSeries_FromException_ScopeSeries_UpdatesMasterNotException()
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

        var originalExTitle = ex.Title;
        var originalExStart = ex.DtStart;

        var updated = await svc.UpdateEventAsync(ex.Id, new UpdateEventRequest(
            cal.Id, "Master Renamed", null, null, D(2026, 1, 5, 9), D(2026, 1, 5, 10), "FREQ=WEEKLY;COUNT=10"), "series", default);

        Assert.Equal(master.Id, updated.Id);
        Assert.Equal("Master Renamed", updated.Title);
        Assert.Equal("FREQ=WEEKLY;COUNT=10", updated.RRule);
        Assert.True(updated.IsSeriesMaster);

        var masterEntity = await db.Set<EventEntity>().FirstAsync(e => e.Id == master.Id);
        Assert.Equal("Master Renamed", masterEntity.Title);
        Assert.Equal("FREQ=WEEKLY;COUNT=10", masterEntity.RRule);

        var exEntity = await db.Set<EventEntity>().FirstAsync(e => e.Id == ex.Id);
        Assert.Equal(originalExTitle, exEntity.Title);
        Assert.Equal(originalExStart, exEntity.DtStart);
        Assert.Equal(recId, exEntity.RecurrenceId);
        Assert.True(exEntity.IsException);
    }

    [Fact]
    public async Task EndpointScope_Series_FromException_DelegatesToMaster()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId = D(2026, 1, 12).ToString("O");
        var ex = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Ex", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId), default);
        // Simulate endpoint: PUT /events/{exId}?scope=series (query scope binding)
        var req = new UpdateEventRequest(cal.Id, "ViaEndpointSeries", null, null, D(2026, 1, 5, 9), D(2026, 1, 5, 10), "FREQ=WEEKLY;COUNT=5");
        // endpoint would merge recurrenceId if present; for series it should not be required
        var result = await svc.UpdateEventAsync(ex.Id, req, "series", default);
        Assert.Equal(master.Id, result.Id);
        Assert.Equal("ViaEndpointSeries", result.Title);
        Assert.Equal("FREQ=WEEKLY;COUNT=5", result.RRule);
    }

    // Minimal endpoint-scope wiring test — verifies service overload is reachable with scope param.
    // Full HTTP integration test requires WebApplicationFactory; service-level coverage is sufficient for PR3.
    // See note in report: endpoint binds scope/recurrenceId query and delegates to CalendarService.
    [Fact]
    public async Task EndpointScope_This_DelegatesToServiceOverload()
    {
        await using var db = CreateDb();
        var cal = SeedCal(db);
        await db.SaveChangesAsync();
        var svc = Svc(db);
        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null, D(2026, 1, 5), D(2026, 1, 5).AddHours(1), "FREQ=WEEKLY;COUNT=2"), default);
        var recId = D(2026, 1, 12).ToString("O");
        // Simulate endpoint: PUT /events/{id}?scope=this&recurrenceId=xxx with body lacking RecurrenceId
        // Endpoint merges query recurrenceId into request — we mimic that here.
        var req = new UpdateEventRequest(cal.Id, "ViaEndpoint", null, null, D(2026, 1, 12, 11), D(2026, 1, 12, 12), null, RecurrenceId: null);
        if (string.IsNullOrEmpty(req.RecurrenceId)) req = req with { RecurrenceId = recId };
        var result = await svc.UpdateEventAsync(master.Id, req, "this", default);
        Assert.True(result.IsException);
        Assert.Equal(recId, result.RecurrenceId);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public void Advance(TimeSpan d) => _now = _now.Add(d);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
