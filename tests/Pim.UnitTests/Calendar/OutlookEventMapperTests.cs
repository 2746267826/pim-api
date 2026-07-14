using System.Text.Json;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookEventMapperTests
{
    [Fact]
    public void CalendarResponse_DefaultSource_IsManual()
    {
        var cal = new CalendarResponse(
            Guid.NewGuid(), "Test", "#fff", "personal", true, 5);
        Assert.Equal("manual", cal.Source);
        Assert.Null(cal.OutlookCalendarBindingId);
        Assert.True(cal.CanEdit);
    }

    [Fact]
    public void CalendarResponse_SevenArgs_SourceIsOutlook()
    {
        var cal = new CalendarResponse(
            Guid.NewGuid(), "Test", "#fff", "sync", false, 3,
            Source: "outlook");
        Assert.Equal("outlook", cal.Source);
        Assert.Null(cal.OutlookCalendarBindingId);
        Assert.True(cal.CanEdit);
    }
    private static readonly Guid BindingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PimCalendarId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Generation = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void ApplyGraphEvent_MapsUtcTimedEvent()
    {
        const string json = """
        {
            "@odata.etag": "etag-1",
            "id": "event-1",
            "iCalUId": "ical-uid-1",
            "subject": "Test Subject",
            "body": { "content": "Test body content" },
            "location": { "displayName": "Room A" },
            "start": { "dateTime": "2026-07-12T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-12T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "change-key-1",
            "type": "singleInstance"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("event-1", target.OutlookEventId);
        Assert.Equal("ical-uid-1", target.Uid);
        Assert.Equal("ical-uid-1", target.SourceUid);
        Assert.Equal("Test Subject", target.Title);
        Assert.Equal("Test body content", target.Description);
        Assert.Equal("Room A", target.Location);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero), target.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero), target.DtEnd);
        Assert.Equal("UTC", target.OriginalStartTimeZone);
        Assert.Equal("UTC", target.OriginalEndTimeZone);
        Assert.Equal("change-key-1", target.OutlookChangeKey);
        Assert.Equal("etag-1", target.OutlookEtag);
        Assert.Equal(BindingId, target.OutlookCalendarBindingId);
        Assert.Equal(PimCalendarId, target.CalendarId);
        Assert.Equal(ConnectionId, target.OutlookConnectionId);
        Assert.Equal(Generation, target.LastSeenSyncGeneration);
        Assert.Equal("outlook", target.Source);
        Assert.False(target.IsAllDay);
        Assert.Equal("{}", target.GraphRecurrenceJson);
        Assert.Null(target.OutlookSeriesMasterId);
        Assert.Equal("singleInstance", target.OutlookEventType);
    }

    [Fact]
    public void ApplyGraphEvent_MapsAllDayOccurrence()
    {
        const string json = """
        {
            "@odata.etag": "etag-2",
            "id": "event-2",
            "iCalUId": "ical-uid-2",
            "subject": "All Day Birthday",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-07-12", "timeZone": "Asia/Shanghai" },
            "end": { "dateTime": "2026-07-13", "timeZone": "Asia/Shanghai" },
            "originalStartTimeZone": "Asia/Shanghai",
            "originalEndTimeZone": "Asia/Shanghai",
            "changeKey": "change-key-2",
            "type": "occurrence",
            "seriesMasterId": "series-1",
            "isAllDay": true
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.True(target.IsAllDay);
        Assert.Equal(new DateOnly(2026, 7, 12), target.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 13), target.AllDayEndDateExclusive);
        Assert.Equal("occurrence", target.OutlookEventType);
        Assert.Equal("series-1", target.OutlookSeriesMasterId);
    }

    [Fact]
    public void ApplyGraphEvent_MapsSeriesMaster()
    {
        const string json = """
        {
            "@odata.etag": "etag-3",
            "id": "event-3",
            "iCalUId": "ical-uid-3",
            "subject": "Weekly Standup",
            "body": { "content": "Recurring meeting" },
            "location": { "displayName": "Room B" },
            "start": { "dateTime": "2026-07-13T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-13T09:30:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "change-key-3",
            "type": "seriesMaster",
            "recurrence": {
                "pattern": {
                    "type": "weekly",
                    "interval": 1,
                    "daysOfWeek": ["monday"],
                    "firstDayOfWeek": "sunday",
                    "index": "first"
                },
                "range": {
                    "type": "noEnd",
                    "startDate": "2026-07-13"
                }
            }
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("seriesMaster", target.OutlookEventType);
        Assert.Null(target.OutlookSeriesMasterId);
        Assert.NotEqual("{}", target.GraphRecurrenceJson);
        using var recDoc = JsonDocument.Parse(target.GraphRecurrenceJson);
        Assert.Equal("weekly", recDoc.RootElement.GetProperty("pattern").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildWritePayload_ContainsExpectedFields()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Test Subject",
            "Body text content",
            "Conference Room",
            new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero),
            null);

        var payload = OutlookEventMapper.BuildWritePayload(draft, "op-1");

        Assert.Equal("Test Subject", payload["subject"]);

        var body = Assert.IsType<Dictionary<string, object?>>(payload["body"]);
        Assert.Equal("text", body["contentType"]);
        Assert.Equal("Body text content", body["content"]);

        var location = Assert.IsType<Dictionary<string, object?>>(payload["location"]);
        Assert.Equal("Conference Room", location["displayName"]);

        var start = Assert.IsType<Dictionary<string, object?>>(payload["start"]);
        Assert.Equal("2026-07-12T09:00:00.0000000", start["dateTime"]);
        Assert.Equal("UTC", start["timeZone"]);

        var end = Assert.IsType<Dictionary<string, object?>>(payload["end"]);
        Assert.Equal("2026-07-12T10:00:00.0000000", end["dateTime"]);
        Assert.Equal("UTC", end["timeZone"]);

        Assert.Equal(false, payload["isAllDay"]);

        Assert.Equal("op-1", payload["transactionId"]);

        Assert.DoesNotContain("recurrence", payload.Keys);
        Assert.DoesNotContain("rrule", payload.Keys);
        Assert.DoesNotContain("uid", payload.Keys);
    }

    [Fact]
    public void BuildWritePayload_NullTransactionId_OmitsKey()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "No Transaction",
            null,
            null,
            new DateTimeOffset(2026, 7, 13, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero),
            null);

        var payload = OutlookEventMapper.BuildWritePayload(draft, null);

        Assert.DoesNotContain("transactionId", payload.Keys);
        Assert.Equal("No Transaction", payload["subject"]);
    }

    [Fact]
    public void ApplyGraphEvent_TimedWithPositiveOffset_ConvertsToUtc()
    {
        const string json = """
        {
            "@odata.etag": "etag-offset",
            "id": "event-offset",
            "iCalUId": "ical-offset",
            "subject": "With Offset",
            "body": { "content": "desc" },
            "location": { "displayName": "Loc" },
            "start": { "dateTime": "2026-07-12T09:00:00.0000000+08:00", "timeZone": "Asia/Shanghai" },
            "end": { "dateTime": "2026-07-12T10:00:00.0000000+08:00", "timeZone": "Asia/Shanghai" },
            "originalStartTimeZone": "Asia/Shanghai",
            "originalEndTimeZone": "Asia/Shanghai",
            "changeKey": "ck",
            "type": "singleInstance"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero), target.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero), target.DtEnd);
        Assert.Equal(TimeSpan.Zero, target.DtStart.Offset);
        Assert.Equal(TimeSpan.Zero, target.DtEnd.Offset);
    }

    [Fact]
    public void ApplyGraphEvent_AllDayWithGraphTimestamp_SetsDateOnly()
    {
        const string json = """
        {
            "@odata.etag": "etag-ad-ts",
            "id": "event-ad-ts",
            "iCalUId": "ical-ad-ts",
            "subject": "All Day Timestamp",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-07-12T00:00:00.0000000", "timeZone": "Asia/Shanghai" },
            "end": { "dateTime": "2026-07-13T00:00:00.0000000", "timeZone": "Asia/Shanghai" },
            "originalStartTimeZone": "Asia/Shanghai",
            "originalEndTimeZone": "Asia/Shanghai",
            "changeKey": "ck",
            "type": "singleInstance",
            "isAllDay": true
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.True(target.IsAllDay);
        Assert.Equal(new DateOnly(2026, 7, 12), target.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 13), target.AllDayEndDateExclusive);
    }

    [Fact]
    public void ApplyGraphEvent_TimedEvent_ClearsExistingAllDayDates()
    {
        var target = new EventEntity
        {
            AllDayStartDate = new DateOnly(2026, 7, 12),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 13),
        };

        const string json = """
        {
            "@odata.etag": "etag-clear",
            "id": "event-clear",
            "iCalUId": "ical-clear",
            "subject": "Timed Clear",
            "body": { "content": "desc" },
            "location": { "displayName": "Room" },
            "start": { "dateTime": "2026-07-14T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-14T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.False(target.IsAllDay);
        Assert.Null(target.AllDayStartDate);
        Assert.Null(target.AllDayEndDateExclusive);
    }

    [Fact]
    public void ApplyGraphEvent_TimedEventNoOffset_ParsesAsUtc()
    {
        const string json = """
        {
            "@odata.etag": "etag-nooffset",
            "id": "event-nooffset",
            "iCalUId": "ical-nooffset",
            "subject": "No Offset",
            "body": { "content": "desc" },
            "location": { "displayName": "Loc" },
            "start": { "dateTime": "2026-07-12T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-12T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero), target.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero), target.DtEnd);
        Assert.Equal(TimeSpan.Zero, target.DtStart.Offset);
        Assert.Equal(TimeSpan.Zero, target.DtEnd.Offset);
    }

    [Fact]
    public void BuildWritePayload_AllDayEvent_PreservesDateBoundaries()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "All Day Birthday",
            null,
            null,
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8)),
            null,
            IsAllDay: true);

        var payload = OutlookEventMapper.BuildWritePayload(draft, null);

        var start = Assert.IsType<Dictionary<string, object?>>(payload["start"]);
        Assert.Equal("2026-07-12T00:00:00.0000000", start["dateTime"]);
        Assert.Equal("UTC", start["timeZone"]);

        var end = Assert.IsType<Dictionary<string, object?>>(payload["end"]);
        Assert.Equal("2026-07-13T00:00:00.0000000", end["dateTime"]);
        Assert.Equal("UTC", end["timeZone"]);

        Assert.True((bool)payload["isAllDay"]!);
    }

    [Fact]
    public void BuildWritePayload_NullDescriptionLocation_OutputsEmptyString()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Null Content",
            null,
            null,
            new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            null);

        var payload = OutlookEventMapper.BuildWritePayload(draft, null);

        var body = Assert.IsType<Dictionary<string, object?>>(payload["body"]);
        Assert.Equal("", body["content"]);
        var location = Assert.IsType<Dictionary<string, object?>>(payload["location"]);
        Assert.Equal("", location["displayName"]);
    }

    [Fact]
    public void ApplyGraphEvent_FallbackUid_WhenICalUIdMissing()
    {
        const string json = """
        {
            "@odata.etag": "etag-4",
            "id": "event-no-ical",
            "subject": "No ICalUId",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-07-14T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-07-14T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "change-key-4",
            "type": "singleInstance"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("event-no-ical", target.OutlookEventId);
        Assert.Equal("event-no-ical@outlook", target.Uid);
        Assert.Equal("event-no-ical@outlook", target.SourceUid);
    }
}
