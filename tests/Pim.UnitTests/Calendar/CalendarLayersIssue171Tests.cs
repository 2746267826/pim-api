using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class CalendarLayersIssue171Tests
{
    private static readonly Guid UserId = Guid.Parse("17117171-1717-1717-1717-171717171717");

    [Fact]
    public async Task Layers_All_Alias_ReturnsEvents_SameAsEventsEndpoint()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Work", Color = "#2563EB", Kind = "calendar", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        // 6 events similar to issue: 2026-08-30T16:00:00Z ~ 2026-09-01T00:00:00Z
        var start = new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Event(calendar, "电工学", new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 7, 40, 0, TimeSpan.Zero)),
            Event(calendar, "土地评估", new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero)),
            Event(calendar, "概论", new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 11, 0, 0, TimeSpan.Zero)),
            Event(calendar, "班委述职", new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 13, 0, 0, TimeSpan.Zero)),
            Event(calendar, "大地测量实习", new DateTimeOffset(2026, 8, 31, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero)),
            Event(calendar, "测试重复事件", new DateTimeOffset(2026, 8, 30, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero)),
        };
        db.Set<EventEntity>().AddRange(events);
        await db.SaveChangesAsync();

        var planning = new PlanningModelService(db, new FixedUser(UserId), null, new RecurrenceService(NullLogger<RecurrenceService>.Instance));
        var calendarSvc = new CalendarService(db, new FixedUser(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

        var rangeStart = new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var eventsViaEvents = await calendarSvc.GetEventsAsync(rangeStart, rangeEnd, CancellationToken.None);
        Assert.Equal(6, eventsViaEvents.Count);

        // layers=all should return same 6
        var layersAll = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(rangeStart, rangeEnd, new[] { "all" }));
        Assert.Equal(6, layersAll.Items.Count(x => x.Layer == "events"));

        // layers=event singular alias should also work
        var layersEvent = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(rangeStart, rangeEnd, new[] { "event" }));
        Assert.Equal(6, layersEvent.Items.Count(x => x.Layer == "events"));

        // layers=null (default) should also contain events
        var layersDefault = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(rangeStart, rangeEnd, null));
        Assert.Contains(layersDefault.Items, x => x.Layer == "events");
        Assert.Equal(6, layersDefault.Items.Count(x => x.Layer == "events"));

        // layers=events explicit
        var layersEvents = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(rangeStart, rangeEnd, new[] { "events" }));
        Assert.Equal(6, layersEvents.Items.Count);
    }

    [Fact]
    public async Task Layers_RecurringEvents_AreExpanded_LikeEventsEndpoint()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Work", Color = "#3B82EB", Kind = "calendar", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        await db.SaveChangesAsync();
        var svc = new CalendarService(db, new FixedUser(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));
        var master = await svc.CreateEventAsync(new CreateEventRequest(calendar.Id, "Weekly", null, null, new DateTimeOffset(2026, 8, 25, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 25, 7, 0, 0, TimeSpan.Zero), "FREQ=WEEKLY;COUNT=3"), CancellationToken.None);

        var planning = new PlanningModelService(db, new FixedUser(UserId), null, new RecurrenceService(NullLogger<RecurrenceService>.Instance));
        var rangeStart = new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        // master repeats 2026-08-25, 2026-09-01, 2026-09-08 -> two occurrences (2026-09-01, 2026-09-08) fall in window
        var layers = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(rangeStart, rangeEnd, new[] { "events" }));
        Assert.Equal(2, layers.Items.Count(x => x.Layer == "events"));
        // via events endpoint should also return 2 occurrences in same window (expanded)
        var events = await svc.GetEventsAsync(rangeStart, rangeEnd, CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Equal(events[0].DtStart, layers.Items.OrderBy(x => x.StartsAt).First().StartsAt);
    }

    [Fact]
    public void OutlookEventMapper_SetsTimeZoneId_FromOriginalStartTimeZone()
    {
        const string json = """
        {
            "@odata.etag": "etag-1",
            "id": "event-1",
            "iCalUId": "ical-1",
            "subject": "电工学②",
            "body": { "content": "第5-6节 (14:00-15:40)" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-08-31T06:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-31T07:40:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "Asia/Shanghai",
            "originalEndTimeZone": "Asia/Shanghai",
            "changeKey": "ck",
            "type": "singleInstance"
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("Asia/Shanghai", target.TimeZoneId);
        Assert.Equal("Asia/Shanghai", target.SourceTimeZoneId);
        Assert.Equal("Asia/Shanghai", target.OriginalStartTimeZone);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero), target.DtStart);
    }

    [Fact]
    public void OutlookEventMapper_FallbackToStartTimeZone_WhenOriginalMissing()
    {
        const string json = """
        {
            "@odata.etag": "etag-2",
            "id": "event-2",
            "iCalUId": "ical-2",
            "subject": "Test",
            "body": { "content": "desc" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-08-31T06:00:00.0000000", "timeZone": "China Standard Time" },
            "end": { "dateTime": "2026-08-31T07:40:00.0000000", "timeZone": "China Standard Time" },
            "changeKey": "ck",
            "type": "singleInstance"
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("China Standard Time", target.TimeZoneId);
        Assert.Equal("China Standard Time", target.SourceTimeZoneId);
    }

    private static EventEntity Event(CalendarEntity cal, string title, DateTimeOffset start, DateTimeOffset end)
        => new()
        {
            Calendar = cal,
            CalendarId = cal.Id,
            Uid = Guid.NewGuid() + "@pim",
            Title = title,
            DtStart = start,
            DtEnd = end,
            Source = "outlook",
            Status = "CONFIRMED"
        };

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var opts = new DbContextOptionsBuilder<PimDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        return new PimDbContext(opts);
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Role => "user";
    }
}
