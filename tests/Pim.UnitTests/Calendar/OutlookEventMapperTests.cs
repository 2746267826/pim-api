using System.IO;
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
    public void BuildWritePayload_MapsGraphFieldsAndOmitsSourceOnlyValues()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Graph Write Fields",
            "<p>safe</p>",
            "Room A",
            new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
            "FREQ=WEEKLY;INTERVAL=1",
            DescriptionFormat: "html",
            Importance: "high",
            Sensitivity: "private",
            ShowAs: "tentative",
            Categories: new[] { "Project" },
            Attendees: new[] { new EventAttendeeDto("Zhang San", "zhangsan@contoso.com") },
            IsReminderOn: true,
            ReminderMinutesBeforeStart: 15,
            IsOnlineMeeting: true,
            OnlineMeetingProvider: "teams",
            Organizer: new EventPersonDto("Li Si", "lisi@contoso.com"),
            OnlineMeetingUrl: "https://teams.microsoft.com/l/meetup-join/xyz",
            ExternalLink: "https://outlook.office.com/calendar/deeplink/xyz",
            AttachmentReferences: new[]
            {
                new EventAttachmentReferenceDto("file", "attach-1", "report.pdf"),
            });

        var payload = OutlookEventMapper.BuildWritePayload(draft, "op-graph");

        var body = Assert.IsType<Dictionary<string, object?>>(payload["body"]);
        Assert.Equal("html", body["contentType"]);
        Assert.Equal("<p>safe</p>", body["content"]);

        Assert.Equal("high", payload["importance"]);
        Assert.Equal("private", payload["sensitivity"]);
        Assert.Equal("tentative", payload["showAs"]);
        Assert.Equal(new object?[] { "Project" },
            Assert.IsAssignableFrom<IEnumerable<object?>>(payload["categories"]));

        var attendee = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object?>>(payload["attendees"]));
        var attendeeMap = Assert.IsType<Dictionary<string, object?>>(attendee);
        Assert.Equal("required", attendeeMap["type"]);
        var emailAddress = Assert.IsType<Dictionary<string, object?>>(attendeeMap["emailAddress"]);
        Assert.Equal("Zhang San", emailAddress["name"]);
        Assert.Equal("zhangsan@contoso.com", emailAddress["address"]);

        Assert.Equal(true, payload["isReminderOn"]);
        Assert.Equal(15, payload["reminderMinutesBeforeStart"]);
        Assert.Equal(true, payload["isOnlineMeeting"]);
        Assert.Equal("teamsForBusiness", payload["onlineMeetingProvider"]);

        Assert.DoesNotContain("organizer", payload.Keys);
        Assert.DoesNotContain("onlineMeetingUrl", payload.Keys);
        Assert.DoesNotContain("onlineMeeting", payload.Keys);
        Assert.DoesNotContain("externalLink", payload.Keys);
        Assert.DoesNotContain("attachmentReferences", payload.Keys);
        Assert.Contains("recurrence", payload.Keys);
        var recurrence = Assert.IsType<Dictionary<string, object?>>(payload["recurrence"]);
        var pattern = Assert.IsType<Dictionary<string, object?>>(recurrence["pattern"]);
        Assert.Equal("weekly", pattern["type"]);
        Assert.Equal(1, pattern["interval"]);
        Assert.DoesNotContain("responseRequested", payload.Keys);
        Assert.DoesNotContain("allowNewTimeProposals", payload.Keys);
        Assert.DoesNotContain("hideAttendees", payload.Keys);

        var reminderOffDraft = new CreateEventRequest(
            PimCalendarId,
            "Reminder Off",
            null,
            null,
            new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
            null,
            IsReminderOn: false);

        var reminderOffPayload = OutlookEventMapper.BuildWritePayload(reminderOffDraft, null);
        Assert.Equal(false, reminderOffPayload["isReminderOn"]);
        Assert.DoesNotContain("reminderMinutesBeforeStart", reminderOffPayload.Keys);

        var emptyListsDraft = new CreateEventRequest(
            PimCalendarId,
            "Empty Lists",
            null,
            null,
            new DateTimeOffset(2026, 7, 18, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero),
            null,
            Categories: Array.Empty<string>(),
            Attendees: Array.Empty<EventAttendeeDto>());

        var emptyPayload = OutlookEventMapper.BuildWritePayload(emptyListsDraft, null);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object?>>(emptyPayload["categories"]));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object?>>(emptyPayload["attendees"]));
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
    public void ApplyGraphEvent_MapsAllTask3TypedFields()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Calendar", "Fixtures", "graph-event-pr2.json");
        Assert.True(File.Exists(fixturePath), $"Fixture not copied to test output: {fixturePath}");
        var json = File.ReadAllText(fixturePath);
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("high", target.Importance);
        Assert.Equal("private", target.Sensitivity);
        Assert.Equal("tentative", target.ShowAs);
        Assert.Equal("html", target.DescriptionFormat);
        Assert.True(target.IsReminderOn);
        Assert.Equal(15, target.ReminderMinutesBeforeStart);
        Assert.Equal("{}", target.GraphRecurrenceJson);

        Assert.Equal("teams", target.OnlineMeetingProvider);
        Assert.True(target.IsOnlineMeeting);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/xxx", target.OnlineMeetingUrl);
        Assert.Equal("https://outlook.office.com/calendar/deeplink/xxx", target.ExternalLink);

        Assert.DoesNotContain("<h1>", target.Description);
        Assert.Contains("会议通知", target.Description);

        using var catsDoc = JsonDocument.Parse(target.CategoriesJson);
        var cats = catsDoc.RootElement.EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("蓝组会议", cats);
        Assert.Contains("产品评审", cats);

        using var orgDoc = JsonDocument.Parse(target.OrganizerJson!);
        Assert.Equal("张三", orgDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("zhangsan@contoso.com", orgDoc.RootElement.GetProperty("email").GetString());

        using var attDoc = JsonDocument.Parse(target.AttendeesJson);
        var atts = attDoc.RootElement.EnumerateArray().ToList();
        Assert.Single(atts);
        Assert.Equal("required", atts[0].GetProperty("type").GetString());

        using var metaDoc = JsonDocument.Parse(target.ExternalMetadataJson);
        Assert.Equal(2, metaDoc.RootElement.GetProperty("mappingVersion").GetInt32());

        Assert.True(metaDoc.RootElement.TryGetProperty("sourceSnapshot", out var snapshot));
        var snapshotBody = snapshot.GetProperty("body");
        Assert.Equal("html", snapshotBody.GetProperty("contentType").GetString());
        Assert.Equal("<h1>会议通知</h1><p>请准时参加</p>",
            snapshotBody.GetProperty("content").GetString());

        Assert.True(metaDoc.RootElement.TryGetProperty("unmapped", out var unmapped));
        Assert.True(unmapped.TryGetProperty("responseRequested", out _));
        Assert.True(unmapped.TryGetProperty("allowNewTimeProposals", out _));
        Assert.True(unmapped.TryGetProperty("hideAttendees", out _));
        Assert.True(unmapped.TryGetProperty("hasAttachments", out _));
        Assert.True(unmapped.TryGetProperty("singleValueExtendedProperties", out var singleValueProps));
        Assert.Equal(JsonValueKind.Array, singleValueProps.ValueKind);
        Assert.Equal("String {00020329-0000-0000-C000-000000000046} name x-custom-tag",
            singleValueProps[0].GetProperty("id").GetString());
        Assert.Equal("custom-value-1", singleValueProps[0].GetProperty("value").GetString());

        Assert.True(unmapped.TryGetProperty("multiValueExtendedProperties", out var multiValueProps));
        Assert.Equal(JsonValueKind.Array, multiValueProps.ValueKind);
        Assert.Equal("String[] {00020329-0000-0000-C000-000000000046} name x-custom-tags",
            multiValueProps[0].GetProperty("id").GetString());
        var multiValues = multiValueProps[0].GetProperty("value")
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(new[] { "tag-a", "tag-b" }, multiValues);
        Assert.True(unmapped.TryGetProperty("futureGraphField", out _));
        Assert.True(unmapped.TryGetProperty("responseStatus", out var responseStatus));
        Assert.Equal("organizer", responseStatus.GetProperty("response").GetString());
        Assert.Equal("2026-08-10T08:00:00Z", responseStatus.GetProperty("time").GetString());
        Assert.Null(typeof(EventEntity).GetProperty("ResponseStatus"));

        Assert.False(unmapped.TryGetProperty("body", out _));
        Assert.False(unmapped.TryGetProperty("subject", out _));
        Assert.False(unmapped.TryGetProperty("importance", out _));
        Assert.False(unmapped.TryGetProperty("sensitivity", out _));
        Assert.False(unmapped.TryGetProperty("showAs", out _));
        Assert.False(unmapped.TryGetProperty("categories", out _));
        Assert.False(unmapped.TryGetProperty("isReminderOn", out _));
        Assert.False(unmapped.TryGetProperty("reminderMinutesBeforeStart", out _));
        Assert.False(unmapped.TryGetProperty("start", out _));
        Assert.False(unmapped.TryGetProperty("end", out _));
        Assert.False(unmapped.TryGetProperty("location", out _));
        Assert.False(unmapped.TryGetProperty("organizer", out _));
        Assert.False(unmapped.TryGetProperty("attendees", out _));
        Assert.False(unmapped.TryGetProperty("isOnlineMeeting", out _));
        Assert.False(unmapped.TryGetProperty("onlineMeetingProvider", out _));
        Assert.False(unmapped.TryGetProperty("onlineMeeting", out _));
        Assert.False(unmapped.TryGetProperty("webLink", out _));
        Assert.False(unmapped.TryGetProperty("type", out _));
        Assert.False(unmapped.TryGetProperty("iCalUId", out _));
        Assert.False(unmapped.TryGetProperty("changeKey", out _));
        Assert.False(unmapped.TryGetProperty("originalStartTimeZone", out _));
        Assert.False(unmapped.TryGetProperty("originalEndTimeZone", out _));
        Assert.False(unmapped.TryGetProperty("@odata.etag", out _));
        Assert.False(unmapped.TryGetProperty("id", out _));
        Assert.False(unmapped.TryGetProperty("seriesMasterId", out _));
        Assert.False(unmapped.TryGetProperty("recurrence", out _));
        Assert.False(unmapped.TryGetProperty("isAllDay", out _));

        Assert.True(unmapped.TryGetProperty("bodyPreview", out var bodyPreview));
        Assert.Equal("会议通知 请准时参加", bodyPreview.GetString());
    }

    [Fact]
    public void ApplyGraphEvent_MissingOrganizerAttendeesCategories_ProducesEmptyStructuredValues()
    {
        const string json = """
        {
            "@odata.etag": "etag-minimal",
            "id": "event-minimal",
            "iCalUId": "ical-minimal",
            "subject": "Minimal Event",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-04T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-04T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck-minimal",
            "type": "singleInstance"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        var exception = Record.Exception(() => OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation));

        Assert.Null(exception);
        Assert.Null(target.OrganizerJson);
        Assert.Equal("[]", target.AttendeesJson);
        Assert.Equal("[]", target.CategoriesJson);

        using var attDoc = JsonDocument.Parse(target.AttendeesJson);
        Assert.Empty(attDoc.RootElement.EnumerateArray());
        using var catDoc = JsonDocument.Parse(target.CategoriesJson);
        Assert.Empty(catDoc.RootElement.EnumerateArray());
    }

    [Fact]
    public void ApplyGraphEvent_SourceSnapshotEvent_PreservesFullRawGraphEvent()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Calendar", "Fixtures", "graph-event-pr2.json");
        var json = File.ReadAllText(fixturePath);
        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);

        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        using var metaDoc = JsonDocument.Parse(target.ExternalMetadataJson);
        Assert.True(metaDoc.RootElement.TryGetProperty("sourceSnapshot", out var snapshot));
        Assert.True(snapshot.TryGetProperty("event", out var snapshotEvent));
        Assert.Equal(JsonValueKind.Object, snapshotEvent.ValueKind);

        Assert.Equal("pr2-fixture-event", snapshotEvent.GetProperty("id").GetString());
        Assert.Equal("Task 3 完整字段测试", snapshotEvent.GetProperty("subject").GetString());

        var attendeeStatus = snapshotEvent.GetProperty("attendees")[0].GetProperty("status");
        Assert.Equal("accepted", attendeeStatus.GetProperty("response").GetString());
        Assert.Equal("2026-08-10T09:00:00Z", attendeeStatus.GetProperty("time").GetString());
        Assert.Equal("lisi@contoso.com",
            snapshotEvent.GetProperty("attendees")[0].GetProperty("emailAddress").GetProperty("address").GetString());

        var meeting = snapshotEvent.GetProperty("onlineMeeting");
        Assert.Equal("123456789", meeting.GetProperty("conferenceId").GetString());
        Assert.Equal("+86 21 1234 5678", meeting.GetProperty("tollNumber").GetString());
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/xxx", meeting.GetProperty("joinUrl").GetString());

        Assert.Equal("<h1>会议通知</h1><p>请准时参加</p>",
            snapshot.GetProperty("body").GetProperty("content").GetString());

        var unmapped = metaDoc.RootElement.GetProperty("unmapped");
        Assert.False(unmapped.TryGetProperty("attendees", out _));
        Assert.False(unmapped.TryGetProperty("onlineMeeting", out _));
        Assert.False(unmapped.TryGetProperty("organizer", out _));
    }

    [Fact]
    public void ApplyGraphEvent_InvalidReminderMinutes_ReturnsNullWithoutThrowing()
    {
        const string json = """
        {
            "@odata.etag": "etag-rm-invalid",
            "id": "event-rm-invalid",
            "iCalUId": "ical-rm-invalid",
            "subject": "Invalid Reminder Minutes",
            "body": { "content": "desc" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-02T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-02T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isReminderOn": true,
            "reminderMinutesBeforeStart": 15.5
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        var exception = Record.Exception(() => OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation));

        Assert.Null(exception);
        Assert.True(target.IsReminderOn);
        Assert.Null(target.ReminderMinutesBeforeStart);
    }

    [Fact]
    public void ApplyGraphEvent_OutOfRangeReminderMinutes_ReturnsNullWithoutThrowing()
    {
        const string json = """
        {
            "@odata.etag": "etag-rm-range",
            "id": "event-rm-range",
            "iCalUId": "ical-rm-range",
            "subject": "Out Of Range Reminder",
            "body": { "content": "desc" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-02T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-02T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isReminderOn": true,
            "reminderMinutesBeforeStart": 4000000000
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        var exception = Record.Exception(() => OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation));

        Assert.Null(exception);
        Assert.True(target.IsReminderOn);
        Assert.Null(target.ReminderMinutesBeforeStart);
    }

    [Fact]
    public void ApplyGraphEvent_ReminderOffClearsMinutes()
    {
        const string json = """
        {
            "@odata.etag": "etag-rm",
            "id": "event-rm",
            "iCalUId": "ical-rm",
            "subject": "Reminder Off",
            "body": { "content": "desc" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isReminderOn": false,
            "reminderMinutesBeforeStart": 15
        }
        """;

        var target = new EventEntity { IsReminderOn = true, ReminderMinutesBeforeStart = 15 };
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.False(target.IsReminderOn);
        Assert.Null(target.ReminderMinutesBeforeStart);
    }

    [Fact]
    public void ApplyGraphEvent_TeamsForBusinessNormalizesToTeams()
    {
        const string json = """
        {
            "@odata.etag": "etag-tfb",
            "id": "event-tfb",
            "iCalUId": "ical-tfb",
            "subject": "Teams Meeting",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isOnlineMeeting": true,
            "onlineMeetingProvider": "teamsForBusiness"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("teams", target.OnlineMeetingProvider);
    }

    [Fact]
    public void ApplyGraphEvent_UnknownContentType_DefaultsToHtmlSanitization()
    {
        const string json = """
        {
            "@odata.etag": "etag-unknown-ct",
            "id": "event-unknown-ct",
            "iCalUId": "ical-unknown-ct",
            "subject": "Unknown Content Type",
            "body": { "contentType": "weird", "content": "<p>ok</p><script>alert(1)</script>" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
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

        Assert.Equal("html", target.DescriptionFormat);
        Assert.Equal("<p>ok</p>", target.Description);
    }

    [Fact]
    public void ApplyGraphEvent_MissingContentType_DefaultsToHtmlSanitization()
    {
        const string json = """
        {
            "@odata.etag": "etag-missing-ct",
            "id": "event-missing-ct",
            "iCalUId": "ical-missing-ct",
            "subject": "Missing Content Type",
            "body": { "content": "<p>ok</p><script>alert(1)</script>" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
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

        Assert.Equal("html", target.DescriptionFormat);
        Assert.Equal("<p>ok</p>", target.Description);
    }

    [Fact]
    public void ApplyGraphEvent_PlainTextH1Literal_NotAltered()
    {
        const string json = """
        {
            "@odata.etag": "etag-plain-h1",
            "id": "event-plain-h1",
            "iCalUId": "ical-plain-h1",
            "subject": "Plain H1 Literal",
            "body": { "contentType": "text", "content": "<h1>literal heading</h1>" },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
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

        Assert.Equal("plain", target.DescriptionFormat);
        Assert.Equal("<h1>literal heading</h1>", target.Description);
    }

    [Theory]
    [InlineData("TEAMSFORBUSINESS", "teams")]
    [InlineData("TeamsForBusiness", "teams")]
    [InlineData("SKYPEFORBUSINESS", "teams")]
    [InlineData("skypeForConsumer", "teams")]
    [InlineData("TEAMSFORCONSUMER", "teams")]
    [InlineData("teamsForConsumer", "teams")]
    [InlineData("UNKNOWN", null)]
    [InlineData("Unknown", null)]
    [InlineData("zoom", "other")]
    public void ApplyGraphEvent_OnlineMeetingProvider_CaseInsensitive(string provider, string? expected)
    {
        var json = $$"""
        {
            "@odata.etag": "etag-provider",
            "id": "event-provider",
            "iCalUId": "ical-provider",
            "subject": "Provider Case",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isOnlineMeeting": true,
            "onlineMeetingProvider": "{{provider}}"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal(expected, target.OnlineMeetingProvider);
    }

    [Fact]
    public void ApplyGraphEvent_EmptyOnlineMeeting_FallsBackToRootOnlineMeetingUrl()
    {
        const string json = """
        {
            "@odata.etag": "etag-root-url-empty",
            "id": "event-root-url-empty",
            "iCalUId": "ical-root-url-empty",
            "subject": "Root Url Fallback",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isOnlineMeeting": true,
            "onlineMeeting": null,
            "onlineMeetingUrl": "https://teams.example/root-meeting"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("https://teams.example/root-meeting", target.OnlineMeetingUrl);
    }

    [Fact]
    public void ApplyGraphEvent_OnlineMeetingWithoutJoinUrl_FallsBackToRootOnlineMeetingUrl()
    {
        const string json = """
        {
            "@odata.etag": "etag-root-url-nojoin",
            "id": "event-root-url-nojoin",
            "iCalUId": "ical-root-url-nojoin",
            "subject": "Root Url Fallback",
            "body": { "content": null },
            "location": { "displayName": null },
            "start": { "dateTime": "2026-08-01T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-01T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "isOnlineMeeting": true,
            "onlineMeeting": { "conferenceId": "123456789", "tollNumber": "+86 21 1234 5678" },
            "onlineMeetingUrl": "https://teams.example/root-meeting"
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation);

        Assert.Equal("https://teams.example/root-meeting", target.OnlineMeetingUrl);
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
    public void BuildWritePayload_SanitizesHtmlDescriptionBeforeSendingToGraph()
    {
        var draft = new CreateEventRequest(
            PimCalendarId,
            "Sanitized Subject",
            "<p>Safe formatting survives</p><script>alert('xss')</script><a href=\"javascript:alert(1)\">safe link text</a>",
            "Room C",
            new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
            null,
            DescriptionFormat: "html");

        var payload = OutlookEventMapper.BuildWritePayload(draft, null);

        var body = Assert.IsType<Dictionary<string, object?>>(payload["body"]);
        Assert.Equal("html", body["contentType"]);
        var content = Assert.IsType<string>(body["content"]);
        Assert.Contains("<p>Safe formatting survives</p>", content);
        Assert.Contains("safe link text", content);
        Assert.DoesNotContain("script", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", content, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ApplyGraphEvent_NonStringTypedFields_ReturnsNullWithoutThrowing()
    {
        const string json = """
        {
            "@odata.etag": "etag-nonstring",
            "id": "event-nonstring",
            "iCalUId": "ical-nonstring",
            "subject": "Non String Fields",
            "body": { "content": 9 },
            "location": { "displayName": 7 },
            "start": { "dateTime": "2026-08-03T09:00:00.0000000", "timeZone": "UTC" },
            "end": { "dateTime": "2026-08-03T10:00:00.0000000", "timeZone": "UTC" },
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC",
            "changeKey": "ck",
            "type": "singleInstance",
            "importance": 5,
            "sensitivity": [],
            "showAs": {},
            "onlineMeetingProvider": {},
            "webLink": 42
        }
        """;

        var target = new EventEntity();
        using var doc = JsonDocument.Parse(json);
        var exception = Record.Exception(() => OutlookEventMapper.ApplyGraphEvent(
            target, doc.RootElement, BindingId, PimCalendarId, ConnectionId, Generation));

        Assert.Null(exception);
        Assert.Null(target.Description);
        Assert.Null(target.Location);
        Assert.Null(target.Importance);
        Assert.Null(target.Sensitivity);
        Assert.Null(target.ShowAs);
        Assert.Null(target.OnlineMeetingProvider);
        Assert.Null(target.ExternalLink);
    }
}
