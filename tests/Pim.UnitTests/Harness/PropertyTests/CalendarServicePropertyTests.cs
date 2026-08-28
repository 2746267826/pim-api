using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class CalendarServicePropertyTests
{
    private static DateTimeOffset D(int y, int m, int d, int h = 10) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    private static CalendarEntity SeedCalendar(PimDbContext db, Guid? userId = null)
    {
        var cal = new CalendarEntity
        {
            UserId = userId ?? ServiceTestBase.DefaultUserId,
            Name = $"cal-{Guid.NewGuid():N}",
            Kind = "calendar",
            IsDefault = true,
            Color = "#3B82F6"
        };
        db.Set<CalendarEntity>().Add(cal);
        return cal;
    }

    [Fact]
    public async Task Calendar_CreateEvent_Single_ReturnsCreated()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var resp = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Single Meeting", "desc", "Room 1",
            D(2026, 3, 10, 9), D(2026, 3, 10, 10), null), CancellationToken.None);

        Assert.Equal("Single Meeting", resp.Title);
        Assert.Equal(cal.Id, resp.CalendarId);
        Assert.False(resp.IsSeriesMaster);
        Assert.False(resp.IsException);
        Assert.Equal(D(2026, 3, 10, 9), resp.DtStart);
    }

    [Fact]
    public async Task Calendar_GetEvents_Empty_ReturnsEmpty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);

        var events = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 1, 31), CancellationToken.None);

        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Calendar_GetEvents_WithSingleEvent_ReturnsWithinRange()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();
        await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Range Event", null, null,
            D(2026, 2, 15, 10), D(2026, 2, 15, 11), null), CancellationToken.None);

        var events = await svc.GetEventsAsync(D(2026, 2, 1), D(2026, 2, 28), CancellationToken.None);

        Assert.Single(events);
        Assert.Equal("Range Event", events[0].Title);
        Assert.True(events[0].DtStart >= D(2026, 2, 1));
        Assert.True(events[0].DtEnd <= D(2026, 2, 28).AddDays(1));
    }

    [Fact]
    public async Task Calendar_CreateEvent_Recurring_ExpandsViaGetEvents()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly Standup", null, null,
            D(2026, 1, 5, 9), D(2026, 1, 5, 10), "FREQ=WEEKLY;COUNT=4"), CancellationToken.None);

        Assert.True(master.IsSeriesMaster);

        var expanded = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 2, 1), CancellationToken.None);

        Assert.Equal(4, expanded.Count);
        Assert.All(expanded, e => Assert.Equal("Weekly Standup", e.Title));
        var ordered = expanded.OrderBy(e => e.DtStart).ToList();
        Assert.Equal(D(2026, 1, 5, 9), ordered[0].DtStart);
        Assert.Equal(D(2026, 1, 26, 9), ordered[3].DtStart);
    }

    [Fact]
    public async Task RecurrenceService_ExpandSeries_Daily_ReturnsOccurrences()
    {
        await using var db = ServiceTestBase.CreateDb();
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var masterEntity = new EventEntity
        {
            CalendarId = cal.Id,
            Uid = Guid.NewGuid() + "@pim",
            Title = "Daily",
            DtStart = D(2026, 3, 1, 9),
            DtEnd = D(2026, 3, 1, 10),
            RRule = "FREQ=DAILY;COUNT=5",
            IsSeriesMaster = true
        };
        db.Set<EventEntity>().Add(masterEntity);
        await db.SaveChangesAsync();

        var recurrence = new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var expanded = recurrence.ExpandEventsV2(new[] { masterEntity }, D(2026, 3, 1, 0), D(2026, 3, 10, 0));

        Assert.Equal(5, expanded.Count);
        Assert.All(expanded, occ => Assert.Equal(TimeSpan.FromHours(1), occ.OccurrenceEnd - occ.OccurrenceStart));
        var starts = expanded.Select(o => o.OccurrenceStart).OrderBy(x => x).ToList();
        Assert.Equal(D(2026, 3, 1, 9), starts[0]);
        Assert.Equal(D(2026, 3, 5, 9), starts[4]);
    }

    [Fact]
    public async Task RecurrenceService_ExpandSeries_WithException_OverlayWorks()
    {
        await using var db = ServiceTestBase.CreateDb();
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var master = new EventEntity
        {
            CalendarId = cal.Id,
            Uid = Guid.NewGuid() + "@pim",
            Title = "Weekly",
            DtStart = D(2026, 1, 5, 10),
            DtEnd = D(2026, 1, 5, 11),
            RRule = "FREQ=WEEKLY;COUNT=4",
            IsSeriesMaster = true
        };
        db.Set<EventEntity>().Add(master);
        await db.SaveChangesAsync();

        var recId = D(2026, 1, 12, 10).ToString("O");
        var exception = new EventEntity
        {
            CalendarId = cal.Id,
            Uid = master.Uid,
            Title = "Rescheduled",
            DtStart = D(2026, 1, 12, 14),
            DtEnd = D(2026, 1, 12, 15),
            IsException = true,
            SeriesMasterId = master.Id,
            RecurrenceId = recId
        };
        db.Set<EventEntity>().Add(exception);
        await db.SaveChangesAsync();

        var recurrence = new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var all = await db.Set<EventEntity>().ToListAsync();
        var expanded = recurrence.ExpandEventsV2(all, D(2026, 1, 1), D(2026, 2, 1));

        Assert.Equal(4, expanded.Count);
        var exOcc = expanded.Single(o => o.RecurrenceId == recId);
        Assert.True(exOcc.IsException);
        Assert.Equal("Rescheduled", exOcc.Entity.Title);
        Assert.Equal(D(2026, 1, 12, 14), exOcc.OccurrenceStart);
    }

    [Fact]
    public async Task Calendar_DeleteEvent_Single_SoftDeletes()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();
        var created = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "ToDelete", null, null,
            D(2026, 4, 1, 10), D(2026, 4, 1, 11), null), CancellationToken.None);

        await svc.DeleteEventAsync(created.Id, CancellationToken.None);

        var entity = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == created.Id);
        Assert.NotNull(entity.DeletedAt);
        var visible = await svc.GetEventsAsync(D(2026, 4, 1), D(2026, 4, 2), CancellationToken.None);
        Assert.Empty(visible);
    }

    [Fact]
    public async Task Calendar_DeleteEvent_Series_CascadesToExceptions()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly Series", null, null,
            D(2026, 1, 5, 10), D(2026, 1, 5, 11), "FREQ=WEEKLY;COUNT=3"), CancellationToken.None);
        var recId = D(2026, 1, 12, 10).ToString("O");
        await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Exception", null, null,
            D(2026, 1, 12, 14), D(2026, 1, 12, 15), null,
            IsException: true, SeriesMasterId: master.Id, RecurrenceId: recId), CancellationToken.None);

        await svc.DeleteEventAsync(master.Id, "series", CancellationToken.None);

        var masterEntity = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == master.Id);
        Assert.NotNull(masterEntity.DeletedAt);
        var exEntity = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.IsException);
        Assert.NotNull(exEntity.DeletedAt);
    }

    [Fact]
    public async Task Calendar_DeleteEvent_This_CreatesCancelledOccurrence()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null,
            D(2026, 1, 5, 10), D(2026, 1, 5, 11), "FREQ=WEEKLY;COUNT=4"), CancellationToken.None);
        var recId = D(2026, 1, 12, 10).ToString("O");

        await svc.DeleteEventAsync(master.Id, "this", recId, CancellationToken.None);

        var masterStill = await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(e => e.Id == master.Id);
        Assert.Null(masterStill.DeletedAt);
        var cancelled = await db.Set<EventEntity>().SingleAsync(e => e.IsException && e.RecurrenceId == recId);
        Assert.Equal("CANCELLED", cancelled.Status);

        var expanded = await svc.GetEventsAsync(D(2026, 1, 1), D(2026, 2, 1), CancellationToken.None);
        var occ = expanded.Single(e => e.RecurrenceId == recId);
        Assert.True(occ.IsCancelled);
    }

    [Fact]
    public async Task ReminderService_CreateAsync_ReturnsOpenWithChannels()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateReminderService(db);
        var relatedId = Guid.NewGuid();

        var reminder = await svc.CreateAsync(new CreateReminderRequest(
            "confirmation", relatedId, "Review change", "Body text",
            "Trigger", "L1LowRiskAction", new[] { "Web", "WindowsToast" },
            "22:00", "07:00", D(2026, 7, 8, 9)), CancellationToken.None);

        Assert.Equal("Review change", reminder.Title);
        Assert.Equal("Open", reminder.Status);
        Assert.Contains("Web", reminder.Channels);
        Assert.Contains("WindowsToast", reminder.Channels);
        Assert.Equal("22:00", reminder.DoNotDisturbStart);
        Assert.Equal(relatedId, reminder.RelatedObjectId);
    }

    [Fact]
    public async Task ReminderService_HandleAction_Dismiss_SetsDismissed()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateReminderService(db);
        var reminder = await svc.CreateAsync(new CreateReminderRequest(
            "object", Guid.NewGuid(), "Low risk", "Body",
            "Trigger", "L1LowRiskAction", new[] { "Web" }, null, null,
            DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        var result = await svc.HandleActionAsync(reminder.Id, "dismiss", CancellationToken.None);

        Assert.Equal("Executed", result.Kind);
        Assert.Equal("Dismissed", result.Status);
        var updated = (await svc.ListAsync(CancellationToken.None)).Single(r => r.Id == reminder.Id);
        Assert.Equal("Dismissed", updated.Status);
    }

    [Fact]
    public async Task ReminderService_Snooze_ShiftsScheduledAt()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateReminderService(db);
        var reminder = await svc.CreateAsync(new CreateReminderRequest(
            "object", Guid.NewGuid(), "Snooze test", "Body",
            "Trigger", "L1LowRiskAction", new[] { "Web" }, null, null,
            D(2026, 7, 8, 9)), CancellationToken.None);

        var newTime = D(2026, 7, 9, 10);
        var snoozed = await svc.SnoozeAsync(reminder.Id, newTime, CancellationToken.None);

        Assert.Equal("Snoozed", snoozed.Status);
        Assert.Equal(newTime.ToUniversalTime(), snoozed.ScheduledAt);
    }

    [Fact]
    public async Task ReportService_GenerateReportAsync_Daily_ReturnsArtifact()
    {
        await using var db = ServiceTestBase.CreateDb();
        var calendar = SeedCalendar(db);
        db.Set<TaskEntity>().Add(new TaskEntity { UserId = ServiceTestBase.DefaultUserId, Uid = Guid.NewGuid() + "@pim", Title = "Sample Task" });
        await db.SaveChangesAsync();

        var svc = new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        var report = await svc.GenerateAsync(new GenerateReportRequest("Daily", DateOnly.FromDateTime(D(2026, 7, 8).UtcDateTime), null), CancellationToken.None);

        Assert.Equal("Daily", report.Kind);
        Assert.Equal("L0AutomaticArtifact", report.RiskLevel);
        Assert.NotEmpty(report.ContentMarkdown);
        Assert.Contains("Tasks:", report.ContentMarkdown);
        Assert.Equal("Active", report.Status);
        var list = await svc.ListAsync(CancellationToken.None);
        Assert.Contains(list, r => r.Id == report.Id);
    }

    [Fact]
    public async Task Calendar_Create_Then_Get_VerifiesPersistenceAndOrdering()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var e1 = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "AAA Early", null, null,
            D(2026, 5, 1, 9), D(2026, 5, 1, 10), null), CancellationToken.None);
        var e2 = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "BBB Late", null, null,
            D(2026, 5, 1, 14), D(2026, 5, 1, 15), null), CancellationToken.None);

        var events = await svc.GetEventsAsync(D(2026, 5, 1, 0), D(2026, 5, 2, 0), CancellationToken.None);

        Assert.Equal(2, events.Count);
        var ordered = events.OrderBy(e => e.DtStart).ToList();
        Assert.Equal(e1.Id, ordered[0].Id);
        Assert.Equal(e2.Id, ordered[1].Id);
        Assert.True(ordered[0].DtStart < ordered[1].DtStart);
    }

    [Fact]
    public async Task Calendar_UpdateEvent_ScopeThis_CreatesOrUpdatesException()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateCalendarService(db);
        var cal = SeedCalendar(db);
        await db.SaveChangesAsync();

        var master = await svc.CreateEventAsync(new CreateEventRequest(
            cal.Id, "Weekly", null, null,
            D(2026, 1, 5, 10), D(2026, 1, 5, 11), "FREQ=WEEKLY;COUNT=4"), CancellationToken.None);
        var recId = D(2026, 1, 12, 10).ToString("O");

        var updated = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Rescheduled Title", "new desc", null,
            D(2026, 1, 12, 14), D(2026, 1, 12, 15), null,
            RecurrenceId: recId), "this", CancellationToken.None);

        Assert.True(updated.IsException);
        Assert.Equal(master.Id, updated.SeriesMasterId);
        Assert.Equal(recId, updated.RecurrenceId);
        Assert.Equal("Rescheduled Title", updated.Title);

        var masterEntity = await db.Set<EventEntity>().FirstAsync(e => e.Id == master.Id);
        Assert.Equal("Weekly", masterEntity.Title);

        var second = await svc.UpdateEventAsync(master.Id, new UpdateEventRequest(
            cal.Id, "Rescheduled Again", null, null,
            D(2026, 1, 12, 16), D(2026, 1, 12, 17), null,
            RecurrenceId: recId), "this", CancellationToken.None);
        Assert.Equal(updated.Id, second.Id);
        Assert.Equal("Rescheduled Again", second.Title);
    }
}
