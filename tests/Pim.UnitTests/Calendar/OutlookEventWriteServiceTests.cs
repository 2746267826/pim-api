using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookEventWriteServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OpId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid BatchIdSeed = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private const string DescriptionSecret = "secret-description-body-test";
    private const string GraphEventJson = """
    {
        "@odata.etag": "etag-1",
        "id": "graph-event-1",
        "subject": "Test Subject",
        "body": {"contentType": "text", "content": "Test desc"},
        "start": {"dateTime": "2026-07-08T09:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-07-08T10:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room A"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-1",
        "changeKey": "change-1",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string LatestGraphEventJson = """
    {
        "@odata.etag": "latest-etag",
        "id": "graph-event-1",
        "subject": "Latest Subject",
        "body": {"contentType": "text", "content": "Latest desc"},
        "start": {"dateTime": "2026-07-08T11:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-07-08T12:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room B"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-1",
        "changeKey": "change-latest",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string LatestConflictGraphEventJson = """
    {
        "@odata.etag": "latest-etag",
        "id": "graph-event-1",
        "subject": "Latest Subject",
        "body": {"contentType": "html", "content": "<p>Sanitized latest description</p><script>RAW-CONFLICT-BODY-MARKER</script>"},
        "start": {"dateTime": "2026-07-08T11:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-07-08T12:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room B"},
        "FUTURE-CONFLICT-FIELD-MARKER": "TOKEN-CONFLICT-MARKER"
    }
    """;

    // ---------- Tests ----------

    [Fact]
    public async Task Create_SendsStableTransactionIdThenPersistsGraphResult()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            Operation: "create",
            CalendarBindingId: bindingId,
            EventId: null,
            Draft: MakeDraft(calendarId),
            Scope: "instance",
            ClientOperationId: OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);
        Assert.Equal("graph-event-1", result.Event!.OutlookEventId);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        Assert.Equal("Test Subject", stored!.Title);

        var req = Assert.Single(handler.Requests);
        var body = await req.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(OpId.ToString("D"), json.RootElement.GetProperty("transactionId").GetString());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.CreatedCount);
    }

    [Fact]
    public async Task Create_GraphFailure_NoLocalEventAndFailedHistoryPersisted()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":"ServerError"}}""");
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);

        Assert.False(await db.Set<EventEntity>().AnyAsync());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    [Fact]
    public async Task Create_ReplayUpsertsExistingLocalEvent_NoDuplicateRow()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var r1 = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("created", r1.Status);

        var r2 = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("created", r2.Status);

        var events = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "graph-event-1").ToListAsync();
        Assert.Single(events);
    }

    [Fact]
    public async Task Update_SendsExactIfMatch_NoTransactionId_AppliesGraphResponse()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        Assert.NotNull(result.Event);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        var ifMatch = req.Headers.IfMatch.FirstOrDefault();
        Assert.NotNull(ifMatch);
        Assert.True(ifMatch!.IsWeak);
        Assert.Equal("\"expected-etag\"", ifMatch.Tag);

        var body = await req.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("transactionId", out _));
        Assert.False(json.RootElement.TryGetProperty("recurrence", out _));

        var stored = await db.Set<EventEntity>().FirstAsync(e => e.Id == eventId);
        Assert.Equal("Updated Subject", stored.Title);
    }

    [Fact]
    public async Task Update_GraphFailure_LocalUnchangedAndFailedHistory()
    {
        await using var db = CreateDb();
        var (connId, bindingId, calId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var service = CreateService(db, handler);

        var snapshot = await SnapshotEventWriteStateAsync(db, eventId);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<GraphRequestException>(() =>
            service.ExecuteAsync(UserId, request, default));

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal(snapshot.Title, stored.Title);
        Assert.Equal(snapshot.DtStart, stored.DtStart);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
    }

    [Fact]
    public async Task Update_412_ReturnsConflictLatest_LocalAndAuditUnchanged()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.OK, LatestGraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("conflict", result.Status);
        Assert.NotNull(result.LatestEvent);
        Assert.Equal("latest-etag", result.LatestEtag);
        Assert.Equal("Latest Subject", result.LatestEvent!.Title);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal("Original Title", stored.Title);

        Assert.False(await db.AuditLogs.AnyAsync());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.ConflictCount);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    [Fact]
    public async Task Update_412_ConflictReturnsSanitizedLatestEvent_NoRawJsonOrSecrets()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.OK, LatestConflictGraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        static string? GetPropertyStringIgnoreCase(JsonElement element, string name)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        var serialized = JsonSerializer.Serialize(result);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        var status = GetPropertyStringIgnoreCase(root, "status");
        Assert.NotNull(status);
        Assert.Equal("conflict", status);

        var latestEtag = GetPropertyStringIgnoreCase(root, "latestEtag");
        Assert.NotNull(latestEtag);
        Assert.Contains("latest-etag", latestEtag);

        Assert.True(TryGetPropertyIgnoreCase(root, "latestEvent", out var latestEvent));
        Assert.Equal(JsonValueKind.Object, latestEvent.ValueKind);

        var title = GetPropertyStringIgnoreCase(latestEvent, "title");
        Assert.NotNull(title);
        Assert.NotEmpty(title);

        var description = GetPropertyStringIgnoreCase(latestEvent, "description");
        Assert.NotNull(description);
        Assert.Equal("<p>Sanitized latest description</p>", description);
        Assert.DoesNotContain("<script", description);
        Assert.False(TryGetPropertyIgnoreCase(root, "latestOutlookJson", out _));
        Assert.False(TryGetPropertyIgnoreCase(root, "externalMetadataJson", out _));
        Assert.DoesNotContain("RAW-CONFLICT-BODY-MARKER", serialized);
        Assert.DoesNotContain("FUTURE-CONFLICT-FIELD-MARKER", serialized);
        Assert.DoesNotContain("TOKEN-CONFLICT-MARKER", serialized);
    }

    [Fact]
    public async Task Delete204_SoftDeletesWithSingleAudit()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(OpId, stored.DeletedByOperationId);
        Assert.Equal("outlook-writeback", stored.DeletedByOperationKind);

        var audits = await db.AuditLogs.ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal("calendar_event", audit.ResourceType);
        Assert.Equal("outlook.event.delete", audit.Action);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("completed", batch.Status);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    [Fact]
    public async Task Delete404_IdempotentSuccessWithSingleAudit()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.NotNull(stored.DeletedAt);

        var audits = await db.AuditLogs.ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal("calendar_event", audit.ResourceType);
        Assert.Equal("outlook.event.delete", audit.Action);
    }

    [Fact]
    public async Task Delete412_LocalUnchanged_ReturnsLatestRemote()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.OK, LatestGraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("conflict", result.Status);
        Assert.NotNull(result.LatestEvent);
        Assert.Equal("Latest Subject", result.LatestEvent!.Title);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Null(stored.DeletedAt);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.ConflictCount);
    }

    [Fact]
    public async Task ReadOnlyBinding_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var binding = await db.Set<OutlookCalendarBindingEntity>().FirstAsync(b => b.Id == bindingId);
        binding.CanEdit = false;
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RemoteMissingBinding_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var binding = await db.Set<OutlookCalendarBindingEntity>().FirstAsync(b => b.Id == bindingId);
        binding.RemoteState = "remote-missing";
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NotConnected_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync();
        conn.Status = "reauth-required";
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CrossUserBinding_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(OtherUserId, request, default));
        Assert.Equal(02005, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CrossBindingEvent_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var otherBindingId = Guid.NewGuid();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            Id = otherBindingId,
            ConnectionId = (await db.Set<OutlookConnectionEntity>().FirstAsync()).Id,
            PimCalendarId = (await db.Set<CalendarEntity>().FirstAsync()).Id,
            GraphCalendarId = "cal-other",
            CanEdit = true,
            RemoteState = "active",
            Name = "Other"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", otherBindingId, eventId, MakeDraft(Guid.NewGuid()), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyUnboundEvent_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.OutlookSyncState = "legacy-unbound";
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingOutlookEventId_Rejected_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.OutlookEventId = null;
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidOperation_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "invalid", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
    }

    [Fact]
    public async Task EmptyClientOperationId_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", Guid.Empty);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Create_MissingDraft_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, _) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "create", bindingId, null, null, "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Update_MissingDraft_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Delete_MissingEventId_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, _) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "delete", bindingId, null, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Update_MissingExpectedEtag_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(Guid.NewGuid()), "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task CalendarMismatch_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var otherCalendarId = Guid.NewGuid();
        db.Set<CalendarEntity>().Add(new CalendarEntity
        {
            Id = otherCalendarId,
            UserId = UserId,
            Name = "Other",
            IsDefault = false
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(otherCalendarId), "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task NonemptyRRule_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var draft = MakeDraft(calendarId) with { RRule = "FREQ=WEEKLY" };
        var request = new OutlookWriteRequest(
            "create", bindingId, null, draft, "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Update_InstanceTarget_CorrectGraphId()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("graph-event-1", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Update_SeriesTarget_UsesMasterId()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var eventId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = eventId,
            CalendarId = calendarId,
            Uid = "series-event",
            Title = "Series Event",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occurrence-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MasterUpdateResponseJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        var req = Assert.Single(handler.Requests);
        Assert.Contains("graph-series-1", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Delete_SeriesTarget_SoftDeletesMasterAndOccurrences()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Series Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, masterId, null, "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("deleted", result.Status);

        var deleted = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.DeletedAt != null && e.OutlookCalendarBindingId == bindingId)
            .ToListAsync();
        Assert.Equal(2, deleted.Count);
    }

    [Fact]
    public async Task InstanceScope_DoesNotUseSeriesMasterId()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var eventId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = eventId,
            CalendarId = calendarId,
            Uid = "instance",
            Title = "Instance",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-instance-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("graph-instance-1", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SeriesUpdate_DoesNotReplaceOccurrenceRowGraphId()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MasterUpdateResponseJson("graph-series-1"));
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, occId, MakeDraft(calendarId), "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("updated", result.Status);

        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal("graph-occ-1", occ.OutlookEventId);
    }

    [Fact]
    public async Task BatchConfirmationCount_ZeroAndNoBodyInJson()
    {
        // Create success
        {
            await using var db = CreateDb();
            var (_, bid, cid) = await SetupStandardAsync(db, UserId);
            var h = new ScriptedHttpMessageHandler();
            h.Enqueue(HttpStatusCode.Created, GraphEventJson);
            var s = CreateService(db, h);
            await s.ExecuteAsync(UserId, new OutlookWriteRequest(
                "create", bid, null, MakeDraft(cid), "instance", Guid.NewGuid()), default);
            var batch = await db.Set<OutlookSyncBatchEntity>().SingleAsync();
            Assert.Equal(0, batch.ConfirmationCount);
            Assert.DoesNotContain(DescriptionSecret, batch.PerCalendarJson);
            Assert.DoesNotContain(DescriptionSecret, batch.ErrorsJson);
        }

        // Update conflict
        {
            await using var db = CreateDb();
            var (_, bid, cid, eid) = await SetupUpdateAsync(db, UserId);
            var h = new ScriptedHttpMessageHandler();
            h.Enqueue(HttpStatusCode.PreconditionFailed);
            h.Enqueue(HttpStatusCode.OK, LatestGraphEventJson);
            var s = CreateService(db, h);
            await s.ExecuteAsync(UserId, new OutlookWriteRequest(
                "update", bid, eid, MakeDraft(cid), "instance", Guid.NewGuid(),
                ExpectedEtag: "W/\"e\""), default);
            var batch = await db.Set<OutlookSyncBatchEntity>().OrderByDescending(b => b.StartedAt).FirstAsync();
            Assert.Equal(0, batch.ConfirmationCount);
            Assert.DoesNotContain(DescriptionSecret, batch.PerCalendarJson);
            Assert.DoesNotContain(DescriptionSecret, batch.ErrorsJson);
        }

        // Delete fail
        {
            await using var db = CreateDb();
            var (_, bid, _, eid) = await SetupUpdateAsync(db, UserId);
            var h = new ScriptedHttpMessageHandler();
            h.Enqueue(HttpStatusCode.InternalServerError);
            var s = CreateService(db, h);
            try { await s.ExecuteAsync(UserId, new OutlookWriteRequest(
                "delete", bid, eid, null, "instance", Guid.NewGuid(),
                ExpectedEtag: "W/\"e\""), default); } catch { }
            var batch = await db.Set<OutlookSyncBatchEntity>().OrderByDescending(b => b.StartedAt).FirstAsync();
            Assert.Equal(0, batch.ConfirmationCount);
            Assert.DoesNotContain(DescriptionSecret, batch.ErrorsJson);
        }
    }

    [Fact]
    public async Task SecondUnauthorized_ReturnsReauth_UpdatesConnection_LocalUnchanged()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var tokens = new FakeOutlookAccessTokenProvider();
        var service = CreateService(db, handler, tokens);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.NotNull(result);
        Assert.Equal("reauth-required", result.Status);
        Assert.NotNull(result.ErrorCode);

        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync();
        Assert.Equal("reauth-required", conn.Status);
        Assert.Equal("interaction-required", conn.TokenHealth);
        Assert.NotNull(conn.LastError);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal("Original Title", stored.Title);
        Assert.Null(stored.DeletedAt);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
    }

    [Fact]
    public async Task Create_NonemptyEventId_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "create", bindingId, Guid.NewGuid(), MakeDraft(calendarId), "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    [Fact]
    public async Task Delete_MissingExpectedEtag_Rejected()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var service = CreateService(db, new ScriptedHttpMessageHandler());

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
    }

    // ---------- Fix 1: Exact batch ownership and reauthentication ----------

    [Fact]
    public async Task UpdateReauth_ExactBatchFailed_UnrelatedNewerNormalBatchUnchanged()
    {
        await using var db = CreateDb();
        var timeProvider = new StubTimeProvider { UtcNowValue = DateTimeOffset.UtcNow };
        var (connectionId, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);

        var unrelatedBatch = new OutlookSyncBatchEntity
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            ConnectionId = connectionId,
            Provider = "outlook",
            Mode = "normal",
            Status = "running",
            StartedAt = timeProvider.UtcNowValue.AddHours(1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(unrelatedBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var tokens = new FakeOutlookAccessTokenProvider();
        var service = CreateService(db, handler, tokens, timeProvider);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("reauth-required", result.Status);

        var unrelated = await db.Set<OutlookSyncBatchEntity>()
            .FirstAsync(b => b.Id == unrelatedBatch.Id);
        Assert.Equal("running", unrelated.Status);

        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync();
        Assert.Equal("reauth-required", conn.Status);
        Assert.NotNull(conn.LastError);
    }

    [Fact]
    public async Task CreateReauth_ExactBatchFailed()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var tokens = new FakeOutlookAccessTokenProvider();
        var service = CreateService(db, handler, tokens);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("reauth-required", result.Status);
        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal("writeback", batch.Mode);
        Assert.Equal(0, batch.ConfirmationCount);

        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync();
        Assert.Equal("reauth-required", conn.Status);
        Assert.Null(await db.Set<EventEntity>().FirstOrDefaultAsync());
    }

    // ---------- Fix 2: Reauth/cancellation during 412 recovery ----------

    [Fact]
    public async Task Update412ThenDouble401_ReauthPropagates_ConnectionReauthBatchFailed()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var tokens = new FakeOutlookAccessTokenProvider();
        var service = CreateService(db, handler, tokens);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("reauth-required", result.Status);
        Assert.Equal("REAUTH_REQUIRED", result.ErrorCode);

        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync();
        Assert.Equal("reauth-required", conn.Status);
        Assert.Equal("interaction-required", conn.TokenHealth);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal("Original Title", stored.Title);
        Assert.Null(stored.DeletedAt);

        Assert.False(await db.AuditLogs.AnyAsync());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.ErrorSummary);
    }

    // ---------- Fix 3: Cancellation and post-Graph persistence ----------

    [Fact]
    public async Task Create_GraphTimeout_FailedBatchPersisted_OperationCanceledRethrown()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueTimeout();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(UserId, request, default));

        Assert.False(await db.Set<EventEntity>().AnyAsync());
        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.FinishedAt);
    }

    [Fact]
    public async Task Update_GraphTimeout_FailedBatchPersisted_OperationCanceledRethrown()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueTimeout();
        var service = CreateService(db, handler);

        var snapshot = await SnapshotEventWriteStateAsync(db, eventId);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(UserId, request, default));

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal(snapshot.Title, stored.Title);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.NotNull(batch.FinishedAt);
    }

    // ---------- Fix 1 (task): 412 recovery cancellation ----------

    [Fact]
    public async Task Update412BecauseOfCancelDuringGetEvent_FailedBatch_OperationCanceledRethrown()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var snapshot = await SnapshotEventWriteStateAsync(db, eventId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(UserId, request, default));

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Equal(snapshot.Title, stored.Title);
        Assert.Null(stored.DeletedAt);
        Assert.False(await db.AuditLogs.AnyAsync());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.FinishedAt);
    }

    [Fact]
    public async Task Delete412BecauseOfCancelDuringGetEvent_FailedBatch_OperationCanceledRethrown()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(UserId, request, default));

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Null(stored.DeletedAt);
        Assert.False(await db.AuditLogs.AnyAsync());

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.FinishedAt);
        Assert.NotNull(batch.ErrorSummary);
        Assert.Contains("delete", batch.PerCalendarJson);
    }

    // ---------- Fix 2 (task): running batch meaningful history ----------

    [Fact]
    public async Task Create_RunningBatchInspectedAtRequestTime_HasParsableHistory()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var capturedBatchJson = new List<string>();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            var batch = db.Set<OutlookSyncBatchEntity>().AsNoTracking()
                .OrderByDescending(b => b.StartedAt)
                .First();
            capturedBatchJson.Add(JsonSerializer.Serialize(new
            {
                batch.Status,
                batch.ConfirmationCount,
                batch.PerCalendarJson,
                batch.StepsJson
            }));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(GraphEventJson, Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await service.ExecuteAsync(UserId, request, default);

        Assert.Single(capturedBatchJson);
        using var captured = JsonDocument.Parse(capturedBatchJson[0]);
        Assert.Equal("running", captured.RootElement.GetProperty("Status").GetString());
        Assert.Equal(0, captured.RootElement.GetProperty("ConfirmationCount").GetInt32());

        var perCalJson = captured.RootElement.GetProperty("PerCalendarJson").GetString();
        Assert.NotNull(perCalJson);
        Assert.NotEmpty(perCalJson);
        using var perCal = JsonDocument.Parse(perCalJson);
        Assert.True(perCal.RootElement.GetArrayLength() > 0);
        var entry = perCal.RootElement[0];
        Assert.Equal("running", entry.GetProperty("status").GetString());
        Assert.Equal("create", entry.GetProperty("operation").GetString());
        Assert.NotNull(entry.GetProperty("bindingId").GetString());
        Assert.NotNull(entry.GetProperty("calendarName").GetString());
        Assert.Equal(0, entry.GetProperty("createdCount").GetInt32());
        Assert.True(entry.GetProperty("timestamp").ValueKind != JsonValueKind.Null);
        Assert.DoesNotContain(DescriptionSecret, perCalJson);

        var stepsJson = captured.RootElement.GetProperty("StepsJson").GetString();
        Assert.NotNull(stepsJson);
        Assert.NotEmpty(stepsJson);
        using var steps = JsonDocument.Parse(stepsJson);
        Assert.True(steps.RootElement.GetArrayLength() > 0);
        var step = steps.RootElement[0];
        Assert.Equal("graph-create", step.GetProperty("step").GetString());
        Assert.Equal("running", step.GetProperty("status").GetString());
    }

    // ---------- Fix 3 (task): single SaveChanges for create/update ----------

    [Fact]
    public async Task Create_SingleSaveChanges_CompletesEventAndBatchAtomically()
    {
        var db = CreateCountingDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);

        var audit = new CalendarAuditWriter(new AuditLogService(db));
        var tokens = new FakeOutlookAccessTokenProvider();
        var timeProvider = new StubTimeProvider();
        var factory = new StubHttpClientFactory(handler);
        var graph = new GraphCalendarClient(factory, tokens, timeProvider);
        var service = new OutlookEventWriteService(
            db, graph, audit, timeProvider, NullLogger<OutlookEventWriteService>.Instance);

        db.ThrowOnCallNumber = 4;

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.CreatedCount);

        Assert.Equal(4, db.SaveCallCount);
        Assert.False(await db.AuditLogs.AnyAsync());
    }

    // ---------- Fix 4 (task): restore soft-deleted series master ----------

    [Fact]
    public async Task SeriesUpdate_RestoresSoftDeletedMaster_OccurrenceGraphIdUnchanged()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var softDeleteOpId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var oldDeletedAt = now.AddDays(-30);

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "master@outlook",
            Title = "Old Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster",
            DeletedAt = oldDeletedAt,
            DeletedByOperationId = softDeleteOpId,
            DeletedByOperationKind = "outlook-writeback"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ@outlook",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MasterUpdateResponseJson("graph-series-1"));
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "update", bindingId, occId, MakeDraft(calendarId), "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);

        db.ChangeTracker.Clear();
        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal("graph-occ-1", occ.OutlookEventId);
        Assert.Null(occ.DeletedAt);

        var master = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-series-1"
                && e.OutlookCalendarBindingId == bindingId);
        Assert.NotNull(master);
        Assert.Null(master!.DeletedAt);
        Assert.Null(master.DeletedByOperationId);
        Assert.Null(master.DeletedByOperationKind);
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, master.UpdatedAt);
        Assert.Equal(expected, master.DtStamp);
        Assert.Equal("Updated Master", master.Title);

        var all = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId).ToListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Create_CompletedBatchHasTimestampsAndSafeHistory()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var timeProvider = new StubTimeProvider { UtcNowValue = DateTimeOffset.UtcNow };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await service.ExecuteAsync(UserId, request, default);

        var batch = await db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.NotNull(batch.FinishedAt);
        Assert.True(batch.UpdatedAt >= batch.StartedAt);

        Assert.False(string.IsNullOrEmpty(batch.PerCalendarJson));
        Assert.DoesNotContain(DescriptionSecret, batch.PerCalendarJson);
        Assert.DoesNotContain("secret", batch.PerCalendarJson, StringComparison.OrdinalIgnoreCase);
        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        Assert.True(perCal.RootElement.GetArrayLength() > 0);

        Assert.False(string.IsNullOrEmpty(batch.StepsJson));
        using var steps = JsonDocument.Parse(batch.StepsJson);
        Assert.True(steps.RootElement.GetArrayLength() > 0);
    }

    [Fact]
    public async Task UpdateConflict_PerCalendarAndStepsPopulated_NoSecretContent()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.OK, LatestGraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var batch = await db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.False(string.IsNullOrEmpty(batch.PerCalendarJson));
        Assert.DoesNotContain(DescriptionSecret, batch.PerCalendarJson);
        Assert.False(string.IsNullOrEmpty(batch.StepsJson));
    }

    [Fact]
    public async Task CreateFailure_ErrorsJsonSanitized_NoRawExceptionMessage()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":"ServerError","message":"A secret internal detail"}}""");
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        try { await service.ExecuteAsync(UserId, request, default); } catch { }

        var batch = await db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.DoesNotContain(DescriptionSecret, batch.ErrorsJson);
        Assert.DoesNotContain("secret", batch.ErrorsJson, StringComparison.OrdinalIgnoreCase);

        Assert.False(string.IsNullOrEmpty(batch.PerCalendarJson));
        Assert.False(string.IsNullOrEmpty(batch.StepsJson));
    }

    [Fact]
    public async Task DeleteSuccess_PerCalendarAndStepsPopulated()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var batch = await db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.False(string.IsNullOrEmpty(batch.PerCalendarJson));
        Assert.False(string.IsNullOrEmpty(batch.StepsJson));
        Assert.NotNull(batch.FinishedAt);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    // ---------- Fix 5: Series master persistence ----------

    [Fact]
    public async Task SeriesUpdate_NewMasterPersisted_OccurrenceGraphIdUnchanged_DbHasTwoEntities()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MasterUpdateResponseJson("graph-series-1"));
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, occId, MakeDraft(calendarId), "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("updated", result.Status);

        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal("graph-occ-1", occ.OutlookEventId);

        var master = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-series-1"
                && e.OutlookCalendarBindingId == bindingId);
        Assert.NotNull(master);
        Assert.NotEqual(occId, master!.Id);
        Assert.NotEqual(default, master.CreatedAt);
        Assert.NotEqual(default, master.UpdatedAt);

        var all = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId).ToListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task SeriesUpdate_ExistingMasterUpdatedInPlace_OccurrenceGraphIdUnchanged()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Original Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MasterUpdateResponseJson("graph-series-1"));
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, occId, MakeDraft(calendarId), "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("updated", result.Status);

        var master = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == masterId);
        Assert.Equal("Updated Master", master.Title);

        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal("graph-occ-1", occ.OutlookEventId);

        var all = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId).ToListAsync();
        Assert.Equal(2, all.Count);
    }

    // ---------- Helpers ----------

    // ---------- Fix 6: Timestamps and rollback ----------

    [Fact]
    public async Task Create_Timestamps_SetCorrectly()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await service.ExecuteAsync(UserId, request, default);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, stored!.CreatedAt);
        Assert.Equal(expected, stored.UpdatedAt);
        Assert.Equal(expected, stored.DtStamp);
    }

    [Fact]
    public async Task ReplayCreate_ClearsDeletionAndSetsTimestamps()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "ical-1@outlook",
            Title = "Old Title",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "outlook-writeback",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        await service.ExecuteAsync(UserId, request, default);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        Assert.Null(stored!.DeletedAt);
        Assert.Null(stored.DeletedByOperationId);
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, stored.UpdatedAt);
        Assert.Equal(expected, stored.DtStamp);
        Assert.NotEqual(expected, stored.CreatedAt);
    }

    [Fact]
    public async Task Update_TimestampsSet_DeletionMetadataPreserved()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Updated Subject", stored.Title);
        Assert.Equal(expected, stored.UpdatedAt);
        Assert.Equal(expected, stored.DtStamp);
    }

    [Fact]
    public async Task Update_MappingFailure_RollbackToOriginal_BatchFailed()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var snapshot = await SnapshotEventWriteStateAsync(db, eventId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, MalformedEventJsonMissingStart());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        try { await service.ExecuteAsync(UserId, request, default); } catch { }

        db.ChangeTracker.Clear();
        var current = await SnapshotEventWriteStateAsync(db, eventId);
        Assert.Equal(snapshot, current);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.FinishedAt);
        Assert.Contains("unknown", batch.ErrorSummary);
        Assert.DoesNotContain("Malformed", batch.ErrorsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed-etag", batch.ErrorsJson);
        Assert.DoesNotContain("Malformed", batch.StepsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateReplay_MappingFailure_RollbackToOriginal_BatchFailed()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var existingId = Guid.NewGuid();
        var originalStart = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = existingId,
            CalendarId = calendarId,
            Uid = "ical-1",
            Title = "Original Title Before Replay",
            Description = "Original description",
            Location = "Original Room",
            DtStart = originalStart,
            DtEnd = originalStart.AddHours(1),
            IsAllDay = false,
            Source = "outlook",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "singleInstance",
            OutlookChangeKey = "change-original",
            OutlookEtag = "W/\"original-etag\"",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "outlook-writeback"
        });
        await db.SaveChangesAsync();

        var snapshot = await SnapshotEventWriteStateAsync(db, existingId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, MalformedEventJsonMissingStart());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        try { await service.ExecuteAsync(UserId, request, default); } catch { }

        db.ChangeTracker.Clear();
        var current = await SnapshotEventWriteStateAsync(db, existingId);
        Assert.Equal(snapshot, current);

        var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        var batch = Assert.Single(batches);
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.FailureCount);
        Assert.NotNull(batch.FinishedAt);
        Assert.Contains("unknown", batch.ErrorSummary);
        Assert.DoesNotContain("Malformed", batch.ErrorsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed-etag", batch.ErrorsJson);
        Assert.DoesNotContain("Malformed", batch.StepsJson, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Fix 6A: Validation before batch/Graph ----------

    [Fact]
    public async Task SoftDeletedEvent_Update_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().FirstAsync(e => e.Id == eventId);
        evt.DeletedAt = DateTimeOffset.UtcNow;
        evt.DeletedByOperationId = OpId;
        evt.DeletedByOperationKind = "outlook-writeback";
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    [Fact]
    public async Task SoftDeletedEvent_Delete_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().FirstAsync(e => e.Id == eventId);
        evt.DeletedAt = DateTimeOffset.UtcNow;
        evt.DeletedByOperationId = OpId;
        evt.DeletedByOperationKind = "outlook-writeback";
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    [Fact]
    public async Task DeselectedBinding_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var binding = await db.Set<OutlookCalendarBindingEntity>().FirstAsync(b => b.Id == bindingId);
        binding.IsSelected = false;
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    [Fact]
    public async Task Create_ScopeSeries_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "series", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    // ---------- Fix 6A: Unified field validation ----------

    [Fact]
    public async Task Create_InvalidShowAs_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var draft = MakeDraft(calendarId) with { ShowAs = "not-a-status" };
        var request = new OutlookWriteRequest(
            "create", bindingId, null, draft, "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    // ---------- Fix 6A: Whitespace RRule ----------

    [Fact]
    public async Task Create_WhitespaceRRule_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var draft = MakeDraft(calendarId) with { RRule = " " };
        var request = new OutlookWriteRequest(
            "create", bindingId, null, draft, "instance", OpId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    [Fact]
    public async Task Update_WhitespaceRRule_RejectedBeforeBatch_NoGraphCall()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, handler);

        var draft = MakeDraft(calendarId) with { RRule = " " };
        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, draft, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(await db.Set<OutlookSyncBatchEntity>().ToListAsync());
    }

    // ---------- Fix 6A: ETag and conflict metadata ----------

    [Fact]
    public async Task Delete_SendsIfMatchWithWeakETag()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"expected-etag\"");

        await service.ExecuteAsync(UserId, request, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("W/\"expected-etag\"", req.Headers.IfMatch.ToString());
    }

    [Fact]
    public async Task Delete412_BatchHistory_HasDeleteOperationAndSteps()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);
        handler.Enqueue(HttpStatusCode.OK, LatestGraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"stale-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);
        Assert.Equal("conflict", result.Status);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        Assert.Null(stored.DeletedAt);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("failed", batch.Status);
        Assert.Equal(1, batch.ConflictCount);

        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        var entry = perCal.RootElement[0];
        Assert.Equal("delete", entry.GetProperty("operation").GetString());
        Assert.Equal("failed", entry.GetProperty("status").GetString());
        Assert.Equal(0, entry.GetProperty("deletedCount").GetInt32());

        using var steps = JsonDocument.Parse(batch.StepsJson);
        var firstStep = steps.RootElement[0];
        Assert.Equal("graph-delete", firstStep.GetProperty("step").GetString());
        Assert.Equal("conflict", firstStep.GetProperty("status").GetString());
    }

    // ---------- Fix 6A: Cancellation after remote mutation ----------

    [Fact]
    public async Task Create_CancelAfterRemoteSuccess_PersistsLocalState()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var cts = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        var innerContent = new StringContent(GraphEventJson, Encoding.UTF8, "application/json");
        var cancellingContent = new CancellingDisposeContent(innerContent, () => cts.Cancel());
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = cancellingContent
        });
        var fakeAudit = new FakeAuditLogService();
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, fakeAudit.RecordCallCount);
    }

    // ---------- Fix 6A-1: Deletion state/counts ----------

    [Fact]
    public async Task DeleteInstance_UpdatedCountAndDeletedCountSet()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.UpdatedCount);

        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        var entry = perCal.RootElement[0];
        Assert.Equal(1, entry.GetProperty("deletedCount").GetInt32());
    }

    [Fact]
    public async Task DeleteInstance_SoftDeleteSetsUpdatedAt()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(OpId, stored.DeletedByOperationId);
        Assert.Equal("outlook-writeback", stored.DeletedByOperationKind);
        Assert.Equal(expected, stored.UpdatedAt);
    }

    [Fact]
    public async Task DeleteSeries_UpdatedCountAndDeletedCountSet()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Series Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "delete", bindingId, masterId, null, "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(2, batch.UpdatedCount);

        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        var entry = perCal.RootElement[0];
        Assert.Equal(2, entry.GetProperty("deletedCount").GetInt32());
    }

    [Fact]
    public async Task DeleteSeries_AllRowsHaveCorrectSoftDeleteMetadata()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var occId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Series Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "delete", bindingId, masterId, null, "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        db.ChangeTracker.Clear();
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

        var master = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == masterId);
        Assert.Equal(expected, master.DeletedAt);
        Assert.Equal(OpId, master.DeletedByOperationId);
        Assert.Equal("outlook-writeback", master.DeletedByOperationKind);
        Assert.Equal(expected, master.UpdatedAt);

        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal(expected, occ.DeletedAt);
        Assert.Equal(OpId, occ.DeletedByOperationId);
        Assert.Equal("outlook-writeback", occ.DeletedByOperationKind);
        Assert.Equal(expected, occ.UpdatedAt);
    }

    [Fact]
    public async Task DeleteSeries_IncludesRequestedEvent_EvenIfInconsistentMasterId()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Series Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "delete", bindingId, masterId, null, "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        await service.ExecuteAsync(UserId, request, default);

        db.ChangeTracker.Clear();
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == masterId);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(OpId, stored.DeletedByOperationId);
        Assert.Equal("outlook-writeback", stored.DeletedByOperationKind);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal(1, batch.UpdatedCount);
        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        Assert.Equal(1, perCal.RootElement[0].GetProperty("deletedCount").GetInt32());
    }

    // ---------- Fix 6A-4: Series delete ignores previously soft-deleted rows ----------

    [Fact]
    public async Task DeleteSeries_IgnoresPreviouslySoftDeletedRows()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var masterId = Guid.NewGuid();
        var occId = Guid.NewGuid();
        var preDeletedOccId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var oldDeletedAt = now.AddDays(-5);
        var oldOpId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var oldUpdatedAt = now.AddDays(-5);

        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master",
            Title = "Series Master",
            DtStart = now,
            DtEnd = now.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "seriesMaster"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = occId,
            CalendarId = calendarId,
            Uid = "occ",
            Title = "Occurrence",
            DtStart = now.AddDays(1),
            DtEnd = now.AddDays(1).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence"
        });
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = preDeletedOccId,
            CalendarId = calendarId,
            Uid = "pre-deleted",
            Title = "Already Deleted Occ",
            DtStart = now.AddDays(2),
            DtEnd = now.AddDays(2).AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-occ-2",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookSeriesMasterId = "graph-series-1",
            OutlookEventType = "occurrence",
            DeletedAt = oldDeletedAt,
            DeletedByOperationId = oldOpId,
            DeletedByOperationKind = "outlook-writeback",
            UpdatedAt = oldUpdatedAt
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var timeProvider = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, handler, timeProvider: timeProvider);

        var request = new OutlookWriteRequest(
            "delete", bindingId, occId, null, "series", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);

        db.ChangeTracker.Clear();
        var expected = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

        var master = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == masterId);
        Assert.Equal(expected, master.DeletedAt);
        Assert.Equal(OpId, master.DeletedByOperationId);
        Assert.Equal("outlook-writeback", master.DeletedByOperationKind);
        Assert.Equal(expected, master.UpdatedAt);

        var occ = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == occId);
        Assert.Equal(expected, occ.DeletedAt);
        Assert.Equal(OpId, occ.DeletedByOperationId);
        Assert.Equal("outlook-writeback", occ.DeletedByOperationKind);
        Assert.Equal(expected, occ.UpdatedAt);

        var preDeleted = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == preDeletedOccId);
        Assert.Equal(oldDeletedAt, preDeleted.DeletedAt);
        Assert.Equal(oldOpId, preDeleted.DeletedByOperationId);
        Assert.Equal("outlook-writeback", preDeleted.DeletedByOperationKind);
        Assert.Equal(oldUpdatedAt, preDeleted.UpdatedAt);

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(2, batch.UpdatedCount);

        using var perCal = JsonDocument.Parse(batch.PerCalendarJson);
        var entry = perCal.RootElement[0];
        Assert.Equal(2, entry.GetProperty("deletedCount").GetInt32());

        var allDeleted = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.DeletedAt != null && e.OutlookCalendarBindingId == bindingId)
            .ToListAsync();
        Assert.Equal(3, allDeleted.Count);
        var occDeleted = allDeleted.Single(e => e.Id == occId);
        Assert.Equal(expected, occDeleted.DeletedAt);

        var audits = await db.AuditLogs.ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal("calendar_event", audit.ResourceType);
        Assert.Equal("outlook.event.delete", audit.Action);
    }

    // ---------- Fix 6A-2: Persist completed batch before audit ----------

    [Fact]
    public async Task Create_BatchCompletedBeforeAudit_AuditFailureStillShowsCompletedBatch()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var fakeAudit = new FakeAuditLogService
        {
            NextException = new InvalidOperationException("audit-failure-secret-xyz")
        };
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);
        Assert.Equal(1, fakeAudit.RecordCallCount);

        db.ChangeTracker.Clear();
        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.CreatedCount);
        Assert.DoesNotContain("audit-failure-secret-xyz", batch.ErrorsJson);
        Assert.DoesNotContain("audit-failure-secret-xyz", batch.PerCalendarJson);
        Assert.DoesNotContain("audit-failure-secret-xyz", batch.StepsJson);
    }

    [Fact]
    public async Task Update_BatchCompletedBeforeAudit_AuditFailureStillShowsCompletedBatch()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var fakeAudit = new FakeAuditLogService
        {
            NextException = new InvalidOperationException("update-audit-failure-abc")
        };
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        Assert.Equal(1, fakeAudit.RecordCallCount);

        db.ChangeTracker.Clear();
        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.UpdatedCount);
        Assert.DoesNotContain("update-audit-failure-abc", batch.ErrorsJson);
    }

    // ---------- Fix 6A-3: Exactly-once best-effort success audit ----------

    [Fact]
    public async Task Create_AuditThrows_ReturnsSuccess_BatchCompleted_NoAuditRow_OneAttempt()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var fakeAudit = new FakeAuditLogService
        {
            NextException = new InvalidOperationException("my-secret-exception-message")
        };
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);

        db.ChangeTracker.Clear();
        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.CreatedCount);

        Assert.Equal(1, fakeAudit.RecordCallCount);
        Assert.DoesNotContain("my-secret-exception-message", batch.ErrorsJson);
        Assert.DoesNotContain("my-secret-exception-message", batch.PerCalendarJson);
        Assert.DoesNotContain("my-secret-exception-message", batch.StepsJson);

        Assert.False(await db.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task Update_AuditThrows_ReturnsSuccess_BatchCompleted_NoAuditRow_OneAttempt()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var fakeAudit = new FakeAuditLogService
        {
            NextException = new InvalidOperationException("update-secret-exception")
        };
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);

        db.ChangeTracker.Clear();
        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.UpdatedCount);

        Assert.Equal(1, fakeAudit.RecordCallCount);
        Assert.DoesNotContain("update-secret-exception", batch.ErrorsJson);

        Assert.False(await db.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task Delete_AuditThrows_ReturnsSuccess_BatchCompleted_NoAuditRow_OneAttempt()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        var fakeAudit = new FakeAuditLogService
        {
            NextException = new InvalidOperationException("delete-secret-exception")
        };
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);

        db.ChangeTracker.Clear();
        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("completed", batch.Status);
        Assert.Equal(1, batch.UpdatedCount);

        Assert.Equal(1, fakeAudit.RecordCallCount);
        Assert.DoesNotContain("delete-secret-exception", batch.ErrorsJson);

        Assert.False(await db.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task Create_Success_AuditResourceTypeCalendarEvent()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var fakeAudit = new FakeAuditLogService();
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.Equal(1, fakeAudit.RecordCallCount);

        var auditRequest = Assert.Single(fakeAudit.Requests);
        Assert.Equal("outlook.event.create", auditRequest.Action);
        Assert.Equal("calendar_event", auditRequest.ResourceType);
    }

    [Fact]
    public async Task Delete404_AuditResourceTypeCalendarEvent()
    {
        await using var db = CreateDb();
        var (_, bindingId, _, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound);
        var fakeAudit = new FakeAuditLogService();
        var service = CreateService(db, handler, auditLogService: fakeAudit);

        var request = new OutlookWriteRequest(
            "delete", bindingId, eventId, null, "instance", OpId,
            ExpectedEtag: "W/\"etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("deleted", result.Status);
        Assert.Equal(1, fakeAudit.RecordCallCount);

        var auditRequest = Assert.Single(fakeAudit.Requests);
        Assert.Equal("outlook.event.delete", auditRequest.Action);
        Assert.Equal("calendar_event", auditRequest.ResourceType);
    }

    // ---------- Fix 6A-4: Safe failure history ----------

    [Fact]
    public async Task FailBatch_GraphRequestException_UsesSafeMessage()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var secretMessage = "s3cret-graph-response-payload-leak";
        handler.Enqueue(HttpStatusCode.InternalServerError, $"{{\"error\":{{\"code\":\"ServerError\",\"message\":\"{secretMessage}\"}}}}");
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        try { await service.ExecuteAsync(UserId, request, default); } catch { }

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("failed", batch.Status);
        Assert.DoesNotContain(secretMessage, batch.ErrorsJson);
        Assert.DoesNotContain(secretMessage, batch.ErrorSummary);
        Assert.Contains("Graph write request failed", batch.ErrorSummary);
        Assert.Contains("graph-500", batch.ErrorSummary);
        Assert.Contains("graph-500", batch.ErrorsJson);
    }

    [Fact]
    public async Task FailBatch_UnknownException_UsesSafeMessage()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("top-secret-stack-trace"));
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        try { await service.ExecuteAsync(UserId, request, default); } catch { }

        var batch = await db.Set<OutlookSyncBatchEntity>().FirstAsync();
        Assert.Equal("failed", batch.Status);
        Assert.DoesNotContain("top-secret-stack-trace", batch.ErrorsJson);
        Assert.DoesNotContain("top-secret-stack-trace", batch.ErrorSummary);
        Assert.DoesNotContain("ArgumentException", batch.ErrorSummary);
    }

    // ---------- Task 5 correction: PIM file attachment ownership validation and persistence ----------

    [Fact]
    public async Task Create_ClientOutlookReference_NotPersisted_GraphPayloadHasNoAttachments()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var service = CreateService(db, handler);

        var forgedOutlookRef = new EventAttachmentReferenceDto("outlook", "forged-att-1", "Forged.pdf");
        var request = new OutlookWriteRequest(
            "create", bindingId, null,
            MakeDraft(calendarId) with { AttachmentReferences = new[] { forgedOutlookRef } },
            "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        Assert.NotNull(result.Event);
        Assert.NotNull(result.Event.AttachmentReferences);
        Assert.Empty(result.Event.AttachmentReferences!);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        Assert.Empty(EventFieldCodec.DeserializeAttachments(stored!.AttachmentReferencesJson));

        var req = Assert.Single(handler.Requests);
        var body = await req.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("attachments", out _));
    }

    [Fact]
    public async Task Update_ClientOutlookReference_NotPersisted_SyncedOutlookReferencesRetained()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.AttachmentReferencesJson = """
            [{"kind":"outlook","id":"att-1","name":"Synced.pdf","contentType":"application/pdf","size":20,"canDownload":true}]
            """;
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var forgedOutlookRef = new EventAttachmentReferenceDto("outlook", "forged-att-9", "Forged.pdf");
        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with { AttachmentReferences = new[] { forgedOutlookRef } },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        Assert.NotNull(result.Event);
        var returned = result.Event!.AttachmentReferences!;
        var outlook = Assert.Single(returned);
        Assert.Equal("att-1", outlook.Id);
        Assert.Equal("Synced.pdf", outlook.Name);
        Assert.DoesNotContain(returned, r => r.Id == "forged-att-9");

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var persisted = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        Assert.Equal("att-1", Assert.Single(persisted).Id);
        Assert.DoesNotContain(persisted, r => r.Id == "forged-att-9");

        var req = Assert.Single(handler.Requests);
        var body = await req.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("attachments", out _));
    }

    [Fact]
    public async Task Create_WithValidOwnedPimFile_PersistsPimFileReference_AndGraphPayloadHasNoAttachmentField()
    {
        await using var db = CreateDb();
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var (_, item) = SeedFileItem(db, UserId);
        await db.SaveChangesAsync();
        var (_, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var service = CreateService(db, handler);

        var pimFileRef = new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name);
        var request = new OutlookWriteRequest(
            "create", bindingId, null,
            MakeDraft(calendarId) with { AttachmentReferences = new[] { pimFileRef } },
            "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        var returnedRef = Assert.Single(result.Event!.AttachmentReferences!);
        Assert.Equal("pimFile", returnedRef.Kind);
        Assert.Equal(item.Id.ToString(), returnedRef.Id);
        Assert.Equal(item.Name, returnedRef.Name);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        var persisted = EventFieldCodec.DeserializeAttachments(stored!.AttachmentReferencesJson);
        Assert.Equal(item.Id.ToString(), Assert.Single(persisted).Id);

        var req = Assert.Single(handler.Requests);
        var body = await req.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("attachments", out _));
    }

    [Fact]
    public async Task Update_WithNewValidPimFile_ReplacesLocalPimFileReferences_AndRetainsSyncedOutlookReference()
    {
        await using var db = CreateDb();
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var (_, item) = SeedFileItem(db, UserId);
        await db.SaveChangesAsync();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.AttachmentReferencesJson = """
            [
                {"kind":"pimFile","id":"pim-file-old","name":"Old.pdf","contentType":"application/pdf","size":10,"canDownload":true},
                {"kind":"outlook","id":"att-1","name":"Synced.pdf","contentType":"application/pdf","size":20,"canDownload":true}
            ]
            """;
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var pimFileRef = new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name);
        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with { AttachmentReferences = new[] { pimFileRef } },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        var returned = result.Event!.AttachmentReferences!;
        Assert.Equal(2, returned.Count);
        Assert.Equal(item.Id.ToString(), Assert.Single(returned, r => r.Kind == "pimFile").Id);
        var outlook = Assert.Single(returned, r => r.Kind == "outlook");
        Assert.Equal("att-1", outlook.Id);
        Assert.Equal("Synced.pdf", outlook.Name);

        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var persisted = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        Assert.Equal(2, persisted.Count);
        Assert.DoesNotContain(persisted, r => r.Id == "pim-file-old");
        Assert.Equal(item.Id.ToString(), Assert.Single(persisted, r => r.Kind == "pimFile").Id);
        Assert.Equal("att-1", Assert.Single(persisted, r => r.Kind == "outlook").Id);
    }

    [Fact]
    public async Task Update_InvalidPimFileReference_FailsBeforeAnyGraphRequest()
    {
        await using var db = CreateDb();
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with
            {
                AttachmentReferences = new[]
                {
                    new EventAttachmentReferenceDto("pimFile", Guid.NewGuid().ToString(), "Missing.pdf")
                }
            },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.True(ex.ErrorCode > 0);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Update_FolderPimFileReference_FailsBeforeAnyGraphRequest()
    {
        await using var db = CreateDb();
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var (_, folderItem) = SeedFileItem(db, UserId, itemType: "folder");
        await db.SaveChangesAsync();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with
            {
                AttachmentReferences = new[]
                {
                    new EventAttachmentReferenceDto("pimFile", folderItem.Id.ToString(), folderItem.Name)
                }
            },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.True(ex.ErrorCode > 0);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Update_NonOwnedPimFileReference_FailsBeforeAnyGraphRequest()
    {
        await using var db = CreateDb();
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var (_, item) = SeedFileItem(db, OtherUserId);
        await db.SaveChangesAsync();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with
            {
                AttachmentReferences = new[]
                {
                    new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name)
                }
            },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteAsync(UserId, request, default));
        Assert.True(ex.ErrorCode > 0);
        Assert.Empty(handler.Requests);
    }

    // ---------- Task 5 correction: null/empty attachment reference contract ----------

    private const string ExistingMixedReferencesJson = """
        [
            {"kind":"pimFile","id":"pim-file-old","name":"Old.pdf","contentType":"application/pdf","size":10,"canDownload":true},
            {"kind":"outlook","id":"att-1","name":"Synced.pdf","contentType":"application/pdf","size":20,"canDownload":true}
        ]
        """;

    [Fact]
    public async Task Update_NullAttachmentReferences_PreservesExistingPimFileAndOutlookReferences()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.AttachmentReferencesJson = ExistingMixedReferencesJson;
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId, MakeDraft(calendarId), "instance", OpId,
            ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var persisted = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        Assert.Equal(2, persisted.Count);
        Assert.Equal("pim-file-old", Assert.Single(persisted, r => r.Kind == "pimFile").Id);
        Assert.Equal("att-1", Assert.Single(persisted, r => r.Kind == "outlook").Id);
    }

    [Fact]
    public async Task Update_EmptyAttachmentReferences_ClearsPimFileButRetainsOutlookReferences()
    {
        await using var db = CreateDb();
        var (_, bindingId, calendarId, eventId) = await SetupUpdateAsync(db, UserId);
        var evt = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        evt.AttachmentReferencesJson = ExistingMixedReferencesJson;
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, UpdatedEventJson());
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "update", bindingId, eventId,
            MakeDraft(calendarId) with { AttachmentReferences = Array.Empty<EventAttachmentReferenceDto>() },
            "instance", OpId, ExpectedEtag: "W/\"expected-etag\"");

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("updated", result.Status);
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == eventId);
        var persisted = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        var outlook = Assert.Single(persisted);
        Assert.Equal("outlook", outlook.Kind);
        Assert.Equal("att-1", outlook.Id);
    }

    [Fact]
    public async Task ReplayCreate_SoftDeletedEntity_NullAttachmentReferences_PreservesExistingReferences()
    {
        await using var db = CreateDb();
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, UserId);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "ical-1@outlook",
            Title = "Old Title",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "outlook-writeback",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AttachmentReferencesJson = ExistingMixedReferencesJson
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var service = CreateService(db, handler);

        var request = new OutlookWriteRequest(
            "create", bindingId, null, MakeDraft(calendarId), "instance", OpId);

        var result = await service.ExecuteAsync(UserId, request, default);

        Assert.Equal("created", result.Status);
        var stored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "graph-event-1");
        Assert.NotNull(stored);
        var persisted = EventFieldCodec.DeserializeAttachments(stored!.AttachmentReferencesJson);
        Assert.Equal(2, persisted.Count);
        Assert.Equal("pim-file-old", Assert.Single(persisted, r => r.Kind == "pimFile").Id);
        Assert.Equal("att-1", Assert.Single(persisted, r => r.Kind == "outlook").Id);
    }

    // ---------- Helpers ----------

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"writeback-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static (FileProviderEntity Provider, FileItemEntity Item) SeedFileItem(
        PimDbContext db, Guid ownerUserId, bool deleted = false, string itemType = "file")
    {
        var provider = new FileProviderEntity
        {
            Id = Guid.NewGuid(),
            UserId = ownerUserId,
            Provider = "nextcloud",
            BaseUrl = "https://nc.example",
            Username = "test-user"
        };
        var item = new FileItemEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            Name = "Contract.pdf",
            ItemType = itemType,
            Path = "/Contract.pdf",
            IsDeleted = deleted
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        return (provider, item);
    }

    private static SaveCountingDbContext CreateCountingDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"writeback-counting-{Guid.NewGuid()}")
            .Options;
        return new SaveCountingDbContext(options);
    }

    private static async Task<(Guid ConnectionId, Guid BindingId, Guid CalendarId)> SetupStandardAsync(
        PimDbContext db, Guid userId)
    {
        var calendarId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();

        db.Set<CalendarEntity>().Add(new CalendarEntity
        {
            Id = calendarId,
            UserId = userId,
            Name = "Test Calendar",
            IsDefault = true,
            Source = "outlook"
        });

        db.Set<OutlookConnectionEntity>().Add(new OutlookConnectionEntity
        {
            Id = connectionId,
            UserId = userId,
            Status = "connected",
            TokenHealth = "healthy"
        });

        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            Id = bindingId,
            ConnectionId = connectionId,
            PimCalendarId = calendarId,
            GraphCalendarId = "cal-1",
            CanEdit = true,
            RemoteState = "active",
            Name = "Test Binding"
        });

        await db.SaveChangesAsync();
        return (connectionId, bindingId, calendarId);
    }

    private static async Task<(Guid ConnectionId, Guid BindingId, Guid CalendarId, Guid EventId)> SetupUpdateAsync(
        PimDbContext db, Guid userId)
    {
        var (connectionId, bindingId, calendarId) = await SetupStandardAsync(db, userId);
        var eventId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = eventId,
            CalendarId = calendarId,
            Uid = "original@outlook",
            Title = "Original Title",
            Description = DescriptionSecret,
            Location = "Original Room",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook",
            Status = "CONFIRMED",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "singleInstance",
            OutlookChangeKey = "change-original",
            OutlookEtag = "W/\"original-etag\""
        });
        await db.SaveChangesAsync();
        return (connectionId, bindingId, calendarId, eventId);
    }

    private static OutlookEventWriteService CreateService(
        PimDbContext db,
        ScriptedHttpMessageHandler handler,
        FakeOutlookAccessTokenProvider? tokens = null,
        StubTimeProvider? timeProvider = null,
        IAuditLogService? auditLogService = null,
        ILogger<OutlookEventWriteService>? logger = null)
    {
        tokens ??= new FakeOutlookAccessTokenProvider();
        timeProvider ??= new StubTimeProvider();
        logger ??= NullLogger<OutlookEventWriteService>.Instance;
        var factory = new StubHttpClientFactory(handler);
        var graph = new GraphCalendarClient(factory, tokens, timeProvider);
        var audit = auditLogService is not null
            ? new CalendarAuditWriter(auditLogService)
            : new CalendarAuditWriter(new AuditLogService(db));
        return new OutlookEventWriteService(
            db, graph, audit, timeProvider, logger);
    }

    private static CreateEventRequest MakeDraft(Guid calendarId) => new(
        CalendarId: calendarId,
        Title: "Test Subject",
        Description: DescriptionSecret,
        Location: "Room A",
        DtStart: new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
        DtEnd: new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
        RRule: null);

    private static string UpdatedEventJson() => """
    {
        "@odata.etag": "updated-etag",
        "id": "graph-event-1",
        "subject": "Updated Subject",
        "body": {"contentType": "text", "content": "Updated desc"},
        "start": {"dateTime": "2026-07-08T14:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-07-08T15:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room C"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-1",
        "changeKey": "change-updated",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private static string MasterUpdateResponseJson(string? id = null)
    {
        var eid = id ?? "graph-series-1";
        return $@"
{{
    ""@odata.etag"": ""master-etag"",
    ""id"": ""{eid}"",
    ""subject"": ""Updated Master"",
    ""body"": {{""contentType"": ""text"", ""content"": ""Master desc""}},
    ""start"": {{""dateTime"": ""2026-07-08T09:00:00.0000000Z"", ""timeZone"": ""UTC""}},
    ""end"": {{""dateTime"": ""2026-07-08T10:00:00.0000000Z"", ""timeZone"": ""UTC""}},
    ""location"": {{""displayName"": ""Master Room""}},
    ""isAllDay"": false,
    ""type"": ""seriesMaster"",
    ""seriesMasterId"": null,
    ""recurrence"": {{""pattern"": {{""type"": ""weekly""}}, ""range"": {{""type"": ""noEnd""}}}},
    ""iCalUId"": ""ical-series"",
    ""changeKey"": ""change-master"",
    ""originalStartTimeZone"": ""UTC"",
    ""originalEndTimeZone"": ""UTC""
}}
";
    }

    private static string MalformedEventJsonMissingStart() => """
    {
        "@odata.etag": "malformed-etag",
        "id": "graph-event-1",
        "subject": "Malformed Event",
        "body": {"contentType": "text", "content": "Malformed desc"},
        "location": {"displayName": "Room X"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-1",
        "changeKey": "change-mal",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private sealed record EventWriteSnapshot(
        string Title, string? Description, string? Location,
        DateTimeOffset DtStart, DateTimeOffset DtEnd,
        bool IsAllDay, string Uid, string Source,
        string? OutlookEventId, string? OutlookEtag, string? OutlookEventType,
        string? OutlookSeriesMasterId, string? OutlookChangeKey,
        string? OriginalStartTimeZone, string? OriginalEndTimeZone,
        Guid? LastSeenSyncGeneration, string? OutlookSyncState,
        DateTimeOffset? DeletedAt, Guid? DeletedByOperationId, string? DeletedByOperationKind);

    private static async Task<EventWriteSnapshot> SnapshotEventWriteStateAsync(
        PimDbContext db, Guid eventId)
    {
        var e = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(x => x.Id == eventId);
        return new EventWriteSnapshot(
            e.Title, e.Description, e.Location, e.DtStart, e.DtEnd,
            e.IsAllDay, e.Uid, e.Source, e.OutlookEventId, e.OutlookEtag,
            e.OutlookEventType, e.OutlookSeriesMasterId, e.OutlookChangeKey,
            e.OriginalStartTimeZone, e.OriginalEndTimeZone,
            e.LastSeenSyncGeneration, e.OutlookSyncState,
            e.DeletedAt, e.DeletedByOperationId, e.DeletedByOperationKind);
    }

    private sealed class SaveCountingDbContext : PimDbContext
    {
        public int SaveCallCount { get; private set; }
        public int? ThrowOnCallNumber { get; set; }

        public SaveCountingDbContext(DbContextOptions<PimDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            if (ThrowOnCallNumber.HasValue && SaveCallCount == ThrowOnCallNumber.Value)
                throw new InvalidOperationException($"forced-save-failure-on-call-{SaveCallCount}");
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public int RecordCallCount { get; private set; }
        public Exception? NextException { get; set; }
        public List<CreateAuditLogRequest> Requests { get; } = [];

        public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
        {
            RecordCallCount++;
            Requests.Add(request);
            if (NextException is not null)
                throw NextException;
            return Task.FromResult(new AuditLogDto(
                Guid.NewGuid(), request.UserId, request.ActorType, request.Action,
                request.ResourceType, request.ResourceId, request.Source,
                AuditResult.Success, null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class CancellingDisposeContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _onDispose;

        public CancellingDisposeContent(HttpContent inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _onDispose();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
