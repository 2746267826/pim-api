using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookIcsServiceTests
{
    [Fact]
    public void ImportOutlookIcs_ParsesAllDayEvent()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN
        BEGIN:VEVENT
        UID:all-day@example.com
        SUMMARY:Company holiday
        DTSTART;VALUE=DATE:20260601
        DTEND;VALUE=DATE:20260602
        END:VEVENT
        END:VCALENDAR
        """;

        var result = new OutlookIcsService().Parse(ics);

        var evt = Assert.Single(result.Events);
        Assert.True(evt.IsAllDay);
        Assert.Equal("all-day@example.com", evt.Uid);
        Assert.Equal("Company holiday", evt.Title);
    }

    [Fact]
    public void ImportOutlookIcs_PreservesMeetingMetadataAndRawComponent()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        METHOD:PUBLISH
        PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN
        BEGIN:VTIMEZONE
        TZID:China Standard Time
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:meeting@example.com
        SUMMARY:Planning meeting
        DTSTART;TZID=China Standard Time:20260603T090000
        DTEND;TZID=China Standard Time:20260603T100000
        ORGANIZER;CN=Owner:mailto:owner@example.com
        ATTENDEE;CN=Guest;ROLE=REQ-PARTICIPANT:mailto:guest@example.com
        SEQUENCE:3
        CLASS:PRIVATE
        TRANSP:OPAQUE
        CATEGORIES:Blue Category,Work
        X-MICROSOFT-CDO-BUSYSTATUS:BUSY
        X-ALT-DESC;FMTTYPE=text/html:<html><body><b>Planning</b></body></html>
        END:VEVENT
        END:VCALENDAR
        """;

        var result = new OutlookIcsService().Parse(ics);

        var evt = Assert.Single(result.Events);
        Assert.Equal("China Standard Time", evt.SourceTimeZoneId);
        Assert.Contains("BEGIN:VEVENT", evt.SourceIcsComponent);
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:BUSY", evt.SourceIcsComponent);

        using var metadata = JsonDocument.Parse(evt.ExternalMetadataJson);
        var root = metadata.RootElement;
        Assert.Equal("PUBLISH", root.GetProperty("method").GetString());
        Assert.Contains("owner@example.com", root.GetProperty("organizer").GetString());
        Assert.Contains("guest@example.com", root.GetProperty("attendees")[0].GetProperty("value").GetString());
        Assert.Equal(3, root.GetProperty("sequence").GetInt32());
        Assert.Equal("BUSY", root.GetProperty("outlookProperties").GetProperty("X-MICROSOFT-CDO-BUSYSTATUS").GetString());
        Assert.Contains("<b>Planning</b>", root.GetProperty("htmlDescription").GetString());
    }

    [Fact]
    public void ImportOutlookIcs_PreservesRecurrenceFields()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN
        BEGIN:VEVENT
        UID:recurring@example.com
        SUMMARY:Weekly sync exception
        DTSTART:20260601T010000Z
        DTEND:20260601T020000Z
        RRULE:FREQ=WEEKLY;COUNT=5
        EXDATE:20260608T010000Z
        RECURRENCE-ID:20260615T010000Z
        END:VEVENT
        END:VCALENDAR
        """;

        var result = new OutlookIcsService().Parse(ics);

        var evt = Assert.Single(result.Events);
        Assert.Equal("FREQ=WEEKLY;COUNT=5", evt.RRule);
        Assert.Contains("20260608T010000Z", evt.ExDatesJson);
        Assert.Contains("20260615T010000Z", evt.RecurrenceId);
        Assert.Contains("recurrenceId", evt.RecurrenceMetadataJson);
        Assert.Contains("exDates", evt.RecurrenceMetadataJson);
        Assert.Contains("RECURRENCE-ID", evt.RecurrenceMetadataJson);
    }

    [Fact]
    public async Task ImportOutlookIcsAsync_SkipsActiveDuplicateButIgnoresDeletedDuplicate()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var calendar = new CalendarEntity
        {
            UserId = userId,
            Name = "Default",
            Kind = "calendar",
            IsDefault = true
        };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().AddRange(
            new EventEntity
            {
                CalendarId = calendar.Id,
                Calendar = calendar,
                Uid = "deleted-duplicate@example.com",
                SourceUid = "deleted-duplicate@example.com",
                Title = "Deleted duplicate",
                DtStart = new DateTimeOffset(2026, 6, 4, 1, 0, 0, TimeSpan.Zero),
                DtEnd = new DateTimeOffset(2026, 6, 4, 2, 0, 0, TimeSpan.Zero),
                DeletedAt = DateTimeOffset.UtcNow
            },
            new EventEntity
            {
                CalendarId = calendar.Id,
                Calendar = calendar,
                Uid = "active-duplicate@example.com",
                SourceUid = "active-duplicate@example.com",
                Title = "Active duplicate",
                DtStart = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.Zero),
                DtEnd = new DateTimeOffset(2026, 6, 5, 2, 0, 0, TimeSpan.Zero)
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, userId);
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:deleted-duplicate@example.com
        SUMMARY:Deleted duplicate
        DTSTART:20260604T010000Z
        DTEND:20260604T020000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:active-duplicate@example.com
        SUMMARY:Active duplicate
        DTSTART:20260605T010000Z
        DTEND:20260605T020000Z
        END:VEVENT
        END:VCALENDAR
        """;

        var report = await service.ImportOutlookIcsAsync(ics, calendar.Id, new OutlookIcsService(), CancellationToken.None);

        Assert.Equal(1, report.Imported);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.SkippedReasons["duplicate_uid"]);
        Assert.Single(report.Samples);
        Assert.Equal("active-duplicate@example.com", report.Samples[0].Uid);
        Assert.Equal(2, await db.Set<EventEntity>().CountAsync());
        Assert.Equal(3, await db.Set<EventEntity>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task ImportOutlookIcsAsync_FallsBackToDefaultCalendarWhenTargetMissing()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var service = CreateService(db, userId);
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:fallback-target@example.com
        SUMMARY:Fallback target
        DTSTART:20260606T010000Z
        DTEND:20260606T020000Z
        END:VEVENT
        END:VCALENDAR
        """;

        var report = await service.ImportOutlookIcsAsync(ics, Guid.NewGuid(), new OutlookIcsService(), CancellationToken.None);

        Assert.Equal(1, report.Imported);
        Assert.Equal(0, report.Skipped);
        var calendar = await db.Set<CalendarEntity>().SingleAsync();
        Assert.True(calendar.IsDefault);
        Assert.Equal("calendar", calendar.Kind);
        var evt = await db.Set<EventEntity>().SingleAsync();
        Assert.Equal(calendar.Id, evt.CalendarId);
        Assert.Equal("fallback-target@example.com", evt.SourceUid);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-ics-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateService(PimDbContext db, Guid userId) =>
        new(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
