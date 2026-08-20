using System.Text.Json;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookRecurrenceMappingTests
{
    private static readonly Guid BindingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PimCalendarId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Generation = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ---- Mapper: seriesMaster daily interval count ----
    [Fact]
    public void ApplyGraphEvent_SeriesMaster_DailyIntervalCount_MapsRRuleAndMasterFlags()
    {
        const string json = """
        {
            "@odata.etag": "etag-1",
            "id": "master-1",
            "iCalUId": "ical-1",
            "subject": "Daily Standup",
            "body": { "content": "daily" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-13T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-13T09:30:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck1",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": { "type": "daily", "interval": 2 },
                "range": { "type": "numbered", "startDate": "2026-07-13", "numberOfOccurrences": 5, "recurrenceTimeZone": "UTC" }
            }
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("FREQ=DAILY;INTERVAL=2;COUNT=5", target.RRule);
        Assert.True(target.IsSeriesMaster);
        Assert.False(target.IsException);
        Assert.Equal("seriesMaster", target.OutlookEventType);
        Assert.Null(target.OutlookSeriesMasterId);
        Assert.Null(target.SeriesMasterId);
        Assert.Contains("daily", target.GraphRecurrenceJson);
        // ExternalMetadata retains original recurrence
        using var meta = JsonDocument.Parse(target.ExternalMetadataJson);
        var snapshotEvent = meta.RootElement.GetProperty("sourceSnapshot").GetProperty("event");
        Assert.True(snapshotEvent.TryGetProperty("recurrence", out var rec));
        Assert.Equal("daily", rec.GetProperty("pattern").GetProperty("type").GetString());
    }

    [Fact]
    public void ApplyGraphEvent_SeriesMaster_WeeklyNoEnd_MapsRRule()
    {
        const string json = """
        {
            "@odata.etag": "etag-2",
            "id": "master-2",
            "iCalUId": "ical-2",
            "subject": "Weekly",
            "body": { "content": "weekly" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-14T10:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-14T11:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck2",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": { "type": "weekly", "interval": 1 },
                "range": { "type": "noEnd", "startDate": "2026-07-14", "recurrenceTimeZone": "UTC" }
            }
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.Equal("FREQ=WEEKLY", target.RRule);
        Assert.True(target.IsSeriesMaster);
    }

    [Fact]
    public void ApplyGraphEvent_SeriesMaster_MonthlyEndDate_MapsUntil()
    {
        const string json = """
        {
            "@odata.etag": "etag-3",
            "id": "master-3",
            "iCalUId": "ical-3",
            "subject": "Monthly",
            "body": { "content": "monthly" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-15T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-15T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck3",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": { "type": "absoluteMonthly", "interval": 1 },
                "range": { "type": "endDate", "startDate": "2026-07-15", "endDate": "2026-12-15", "recurrenceTimeZone": "UTC" }
            }
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.StartsWith("FREQ=MONTHLY", target.RRule);
        Assert.Contains("UNTIL=", target.RRule);
        Assert.Contains("20261215", target.RRule);
        Assert.True(target.IsSeriesMaster);
    }

    [Fact]
    public void ApplyGraphEvent_SeriesMaster_YearlyNumbered_MapsCount()
    {
        const string json = """
        {
            "@odata.etag": "etag-4",
            "id": "master-4",
            "iCalUId": "ical-4",
            "subject": "Yearly",
            "body": { "content": "yearly" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-16T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-16T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck4",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": { "type": "absoluteYearly", "interval": 1 },
                "range": { "type": "numbered", "startDate": "2026-07-16", "numberOfOccurrences": 3, "recurrenceTimeZone": "UTC" }
            }
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.Equal("FREQ=YEARLY;COUNT=3", target.RRule);
    }

    [Fact]
    public void ApplyGraphEvent_SeriesMaster_RetainsGraphRecurrenceAndExternalMetadata()
    {
        const string json = """
        {
            "@odata.etag": "etag-5",
            "id": "master-5",
            "iCalUId": "ical-5",
            "subject": "Retain",
            "body": { "content": "x" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-17T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-17T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck5",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": { "type": "daily", "interval": 1 },
                "range": { "type": "noEnd", "startDate": "2026-07-17", "recurrenceTimeZone": "UTC" }
            }
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.NotEqual("{}", target.GraphRecurrenceJson);
        using var recDoc = JsonDocument.Parse(target.GraphRecurrenceJson);
        Assert.Equal("daily", recDoc.RootElement.GetProperty("pattern").GetProperty("type").GetString());
        using var meta = JsonDocument.Parse(target.ExternalMetadataJson);
        var ev = meta.RootElement.GetProperty("sourceSnapshot").GetProperty("event");
        Assert.True(ev.TryGetProperty("recurrence", out _));
    }

    [Fact]
    public void ApplyGraphEvent_Exception_MapsIsException()
    {
        const string json = """
        {
            "@odata.etag": "etag-ex",
            "id": "ex-1",
            "iCalUId": "ical-1",
            "subject": "Exception",
            "body": { "content": "ex" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-20T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-20T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck-ex",
            "type": "exception",
            "seriesMasterId": "master-1"
        }
        """;
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.Equal("exception", target.OutlookEventType);
        Assert.Equal("master-1", target.OutlookSeriesMasterId);
        Assert.True(target.IsException);
        Assert.False(target.IsSeriesMaster);
    }

    [Fact]
    public void ApplyGraphEvent_SingleInstance_ClearsRRule()
    {
        const string json = """
        {
            "@odata.etag": "etag-s",
            "id": "single-1",
            "iCalUId": "ical-s",
            "subject": "Single",
            "body": { "content": "single" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-18T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-18T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck-s",
            "type": "singleInstance"
        }
        """;
        var target = new EventEntity { RRule = "FREQ=DAILY;COUNT=5", IsSeriesMaster = true };
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);
        Assert.Null(target.RRule);
        Assert.False(target.IsSeriesMaster);
        Assert.False(target.IsException);
    }

    // ---- Writeback payload ----
    [Fact]
    public void BuildWritePayload_WithDailyRRule_GeneratesGraphRecurrence()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Series",
            "desc",
            "Room",
            new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
            "FREQ=DAILY;INTERVAL=2;COUNT=5");
        var payload = OutlookEventMapper.BuildWritePayload(draft, null);
        Assert.True(payload.ContainsKey("recurrence"));
        var rec = Assert.IsType<Dictionary<string, object?>>(payload["recurrence"]);
        var pattern = Assert.IsType<Dictionary<string, object?>>(rec["pattern"]);
        Assert.Equal("daily", pattern["type"]);
        Assert.Equal(2, pattern["interval"]);
        var range = Assert.IsType<Dictionary<string, object?>>(rec["range"]);
        Assert.Equal("numbered", range["type"]);
        Assert.Equal(5, range["numberOfOccurrences"]);
        Assert.Equal("2026-07-13", range["startDate"]);
    }

    [Fact]
    public void BuildWritePayload_WithWeeklyUntil_GeneratesEndDate()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "WeeklyUntil",
            "desc",
            null,
            new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 11, 0, 0, TimeSpan.Zero),
            "FREQ=WEEKLY;UNTIL=20261215T090000Z");
        var payload = OutlookEventMapper.BuildWritePayload(draft, null);
        var rec = Assert.IsType<Dictionary<string, object?>>(payload["recurrence"]);
        var pattern = Assert.IsType<Dictionary<string, object?>>(rec["pattern"]);
        Assert.Equal("weekly", pattern["type"]);
        var range = Assert.IsType<Dictionary<string, object?>>(rec["range"]);
        Assert.Equal("endDate", range["type"]);
        Assert.Equal("2026-12-15", range["endDate"]);
    }

    [Fact]
    public void BuildWritePayload_WithoutRRule_OmitsRecurrence()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Single",
            "desc",
            null,
            new DateTimeOffset(2026, 7, 18, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero),
            null);
        var payload = OutlookEventMapper.BuildWritePayload(draft, null);
        Assert.DoesNotContain("recurrence", payload.Keys);
    }

    [Fact]
    public void BuildWritePayload_MonthlyAndYearly()
    {
        var monthly = new CreateEventRequest(
            PimCalendarId, "M", null, null,
            new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            "FREQ=MONTHLY;COUNT=3");
        var p1 = OutlookEventMapper.BuildWritePayload(monthly, null);
        var pat1 = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<Dictionary<string, object?>>(p1["recurrence"])["pattern"]);
        Assert.Equal("absoluteMonthly", pat1["type"]);

        var yearly = new CreateEventRequest(
            PimCalendarId, "Y", null, null,
            new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            "FREQ=YEARLY;COUNT=2");
        var p2 = OutlookEventMapper.BuildWritePayload(yearly, null);
        var pat2 = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<Dictionary<string, object?>>(p2["recurrence"])["pattern"]);
        Assert.Equal("absoluteYearly", pat2["type"]);
    }
}
