using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Mcp.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

/// <summary>
/// R03 focused evidence: the public event endpoints, the idempotency ledger and the
/// reverse/undo path run against a real PostgreSQL schema and the real HTTP pipeline.
/// Graph itself is a deterministic fake; no Microsoft tenant or token is contacted.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class OutlookWritebackEndpointE2ETests
{
    private readonly ITestOutputHelper _output;

    public OutlookWritebackEndpointE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string GraphEventJson = """
    {
        "@odata.etag": "W/\"etag-1\"",
        "id": "graph-event-1",
        "subject": "Created Subject",
        "body": {"contentType": "text", "content": "Created description"},
        "start": {"dateTime": "2026-11-30T09:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T10:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room A"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-e2e",
        "changeKey": "change-1",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string UpdatedEventJson = """
    {
        "@odata.etag": "W/\"updated-etag\"",
        "id": "graph-event-1",
        "subject": "Updated Subject",
        "body": {"contentType": "text", "content": "Updated description"},
        "start": {"dateTime": "2026-11-30T11:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T12:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room B"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-e2e",
        "changeKey": "change-2",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string LatestEventJson = """
    {
        "@odata.etag": "W/\"latest-etag\"",
        "id": "graph-event-1",
        "subject": "Latest Remote Subject",
        "body": {"contentType": "text", "content": "Latest remote description"},
        "start": {"dateTime": "2026-11-30T13:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T14:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room C"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-e2e",
        "changeKey": "change-3",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string RestoredEventJson = """
    {
        "@odata.etag": "W/\"restored-etag\"",
        "id": "graph-event-restored",
        "subject": "Updated Subject",
        "body": {"contentType": "text", "content": "Updated description"},
        "start": {"dateTime": "2026-11-30T09:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T10:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Original Room"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-restored",
        "changeKey": "change-restored",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string UndoRestoredEventJson = """
    {
        "@odata.etag": "W/\"undo-etag\"",
        "id": "graph-event-1",
        "subject": "Original Title",
        "body": {"contentType": "text", "content": "Original description"},
        "start": {"dateTime": "2026-11-30T09:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T10:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Original Room"},
        "isAllDay": false,
        "type": "singleInstance",
        "seriesMasterId": null,
        "recurrence": null,
        "iCalUId": "ical-e2e",
        "changeKey": "change-undo",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC"
    }
    """;

    private const string SeriesOccurrenceListJson = """
    {
        "value": [
            {
                "@odata.etag": "W/\"occ-etag\"",
                "id": "graph-occ-real-1",
                "subject": "Series Master",
                "body": {"contentType": "text", "content": "Occurrence description"},
                "start": {"dateTime": "2026-11-30T09:00:00.0000000Z", "timeZone": "UTC"},
                "end": {"dateTime": "2026-11-30T10:00:00.0000000Z", "timeZone": "UTC"},
                "location": {"displayName": "Room A"},
                "isAllDay": false,
                "type": "occurrence",
                "seriesMasterId": "graph-series-1",
                "originalStart": "2026-11-30T09:00:00.0000000Z",
                "originalStartTimeZone": "UTC",
                "originalEndTimeZone": "UTC",
                "iCalUId": "ical-series",
                "changeKey": "change-occ",
                "recurrence": null
            }
        ]
    }
    """;

    private const string SeriesOccurrenceExceptionJson = """
    {
        "@odata.etag": "W/\"occ-updated-etag\"",
        "id": "graph-occ-real-1",
        "subject": "Changed occurrence",
        "body": {"contentType": "text", "content": "Occurrence description"},
        "start": {"dateTime": "2026-11-30T11:00:00.0000000Z", "timeZone": "UTC"},
        "end": {"dateTime": "2026-11-30T12:00:00.0000000Z", "timeZone": "UTC"},
        "location": {"displayName": "Room A"},
        "isAllDay": false,
        "type": "exception",
        "seriesMasterId": "graph-series-1",
        "originalStart": "2026-11-30T09:00:00.0000000Z",
        "originalStartTimeZone": "UTC",
        "originalEndTimeZone": "UTC",
        "iCalUId": "ical-series",
        "changeKey": "change-occ-updated",
        "recurrence": null
    }
    """;

    [SkippableFact]
    public async Task CreateReplayUpdateConflictDeleteAndUndo_RunOverRealHttpAndPostgres()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var client = Authed(factory, seed.UserId);

        var indexNames = await ReadIndexNamesAsync(temp);
        Assert.Contains("UX_outlook_writeback_operations_user_client_operation", indexNames);
        Assert.Contains("UX_outlook_writeback_operations_user_undo_of", indexNames);

        var operationId = Guid.NewGuid();
        graph.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var createBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Created Subject",
          "description": "Created description",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;
        var create = await client.PostAsync(
            $"/api/v1/calendar/events?clientOperationId={operationId:D}", Json(createBody));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadDataAsync(create);
        var eventId = created.GetProperty("id").GetGuid();
        Assert.Equal("W/\"etag-1\"", created.GetProperty("outlookEtag").GetString());
        Assert.Equal(operationId, created.GetProperty("clientOperationId").GetGuid());
        Assert.True(created.GetProperty("undoAvailable").GetBoolean());
        Assert.False(created.GetProperty("requiresConfirmation").GetBoolean());

        var replay = await client.PostAsync(
            $"/api/v1/calendar/events?clientOperationId={operationId:D}", Json(createBody));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await ReadDataAsync(replay);
        Assert.Equal(eventId, replayed.GetProperty("id").GetGuid());
        Assert.Single(graph.Requests);
        Assert.Equal(1, await temp.Db.Set<EventEntity>().CountAsync(e => e.OutlookEventId == "graph-event-1"));

        graph.Enqueue(HttpStatusCode.OK, UpdatedEventJson);
        var updateBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Updated Subject",
          "dtStart": "2026-11-30T11:00:00Z",
          "dtEnd": "2026-11-30T12:00:00Z"
        }
        """;
        var update = await client.PutAsync($"/api/v1/calendar/events/{eventId}", Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("W/\"etag-1\"", graph.Requests[^1].IfMatch);

        graph.Enqueue(HttpStatusCode.PreconditionFailed);
        graph.Enqueue(HttpStatusCode.OK, LatestEventJson);
        var conflict = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId}?expectedEtag=W%2F%22stale-etag%22",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var conflictDoc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
        {
            var data = conflictDoc.RootElement.GetProperty("data");
            var latest = data.GetProperty("latestEvent");
            Assert.Equal("Latest Remote Subject", latest.GetProperty("title").GetString());
            Assert.True(data.GetProperty("noChangesApplied").GetBoolean());
        }
        temp.Db.ChangeTracker.Clear();
        Assert.Equal("Updated Subject",
            (await temp.Db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == eventId)).Title);

        var deleteOperationId = Guid.NewGuid();
        graph.Enqueue(HttpStatusCode.NoContent);
        var delete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{eventId}?clientOperationId={deleteOperationId:D}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deleteReceipt = await ReadDataAsync(delete);
        Assert.Equal("deleted", deleteReceipt.GetProperty("status").GetString());
        Assert.Equal(deleteOperationId, deleteReceipt.GetProperty("clientOperationId").GetGuid());
        Assert.True(deleteReceipt.GetProperty("undoAvailable").GetBoolean());
        Assert.False(deleteReceipt.GetProperty("noChangesApplied").GetBoolean());
        Assert.Contains("撤销", deleteReceipt.GetProperty("userMessage").GetString());
        temp.Db.ChangeTracker.Clear();
        Assert.NotNull(await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.Id == eventId).Select(e => e.DeletedAt).SingleAsync());

        var deleteReplay = await client.DeleteAsync(
            $"/api/v1/calendar/events/{eventId}?clientOperationId={deleteOperationId:D}");
        Assert.Equal(HttpStatusCode.OK, deleteReplay.StatusCode);
        var deleteReplayReceipt = await ReadDataAsync(deleteReplay);
        Assert.Equal("deleted", deleteReplayReceipt.GetProperty("status").GetString());
        Assert.True(deleteReplayReceipt.GetProperty("replayed").GetBoolean());
        Assert.True(deleteReplayReceipt.GetProperty("undoAvailable").GetBoolean());
        Assert.Equal(5, graph.CoreRequests.Count);

        graph.Enqueue(HttpStatusCode.Created, RestoredEventJson);
        var undo = await client.PostAsync(
            $"/api/v1/calendar/outlook/events/{deleteOperationId:D}/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undoData = await ReadDataAsync(undo);
        Assert.Equal("created", undoData.GetProperty("status").GetString());
        Assert.Equal("Updated Subject",
            undoData.GetProperty("event").GetProperty("title").GetString());

        var secondUndo = await client.PostAsync(
            $"/api/v1/calendar/outlook/events/{deleteOperationId:D}/undo", null);
        Assert.Equal(HttpStatusCode.BadRequest, secondUndo.StatusCode);

        var coreRequests = graph.CoreRequests;
        Assert.Equal(6, coreRequests.Count);
        Assert.Equal("POST", coreRequests[0].Method);
        Assert.Equal("PATCH", coreRequests[1].Method);
        Assert.Equal("PATCH", coreRequests[2].Method);
        Assert.Equal("GET", coreRequests[3].Method);
        Assert.Equal("DELETE", coreRequests[4].Method);
        Assert.Equal("POST", coreRequests[5].Method);

        _output.WriteLine(
            "Real PostgreSQL migrations/unique indexes + HTTP create/replay/update/conflict/delete/undo passed with a fake Graph.");
    }

    [SkippableFact]
    public async Task ProviderFailure_Returns502ThenSameIdRetrySucceeds()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var client = Authed(factory, seed.UserId);
        var operationId = Guid.NewGuid();
        var body = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Retry Subject",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;

        graph.EnqueueException(new HttpRequestException(
            "token=TOKEN-MUST-NOT-LEAK RAW-GRAPH-BODY"));
        var failed = await client.PostAsync(
            $"/api/v1/calendar/events?clientOperationId={operationId:D}", Json(body));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var failedText = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TOKEN-MUST-NOT-LEAK", failedText);
        Assert.DoesNotContain("RAW-GRAPH-BODY", failedText);
        Assert.Contains("PROVIDER_UNAVAILABLE", failedText);
        using (var failedDoc = JsonDocument.Parse(failedText))
        {
            Assert.False(failedDoc.RootElement
                .GetProperty("data").GetProperty("noChangesApplied").GetBoolean());
        }

        graph.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var retried = await client.PostAsync(
            $"/api/v1/calendar/events?clientOperationId={operationId:D}", Json(body));
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);

        temp.Db.ChangeTracker.Clear();
        var operation = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .AsNoTracking().SingleAsync(o => o.ClientOperationId == operationId);
        Assert.Equal("completed", operation.State);
        Assert.Equal(2, operation.AttemptCount);
        Assert.Equal(2, graph.Requests.Count);
        Assert.Equal(1, await temp.Db.Set<EventEntity>().CountAsync(e => e.OutlookEventId == "graph-event-1"));
    }

    [SkippableFact]
    public async Task SyntheticOccurrenceUpdateAndDelete_UseGraphOccurrence_NotSeriesMaster()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var (masterId, recurrenceId) = await SeedSeriesMasterAsync(temp, seed);
        var client = Authed(factory, seed.UserId);
        var syntheticOccurrenceId = SyntheticOccurrenceId(masterId, recurrenceId);
        var updateOperationId = Guid.NewGuid();

        graph.Enqueue(HttpStatusCode.OK, SeriesOccurrenceListJson);
        graph.Enqueue(HttpStatusCode.OK, SeriesOccurrenceExceptionJson);
        var updateBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Changed occurrence",
          "dtStart": "2026-11-30T11:00:00Z",
          "dtEnd": "2026-11-30T12:00:00Z"
        }
        """;
        var update = await client.PutAsync(
            $"/api/v1/calendar/events/{syntheticOccurrenceId:D}" +
            $"?scope=instance&originalEventId={masterId:D}" +
            $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
            $"&clientOperationId={updateOperationId:D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await ReadDataAsync(update);
        Assert.True(updated.GetProperty("isException").GetBoolean());
        Assert.Equal(masterId, updated.GetProperty("seriesMasterId").GetGuid());
        Assert.NotEqual(masterId, updated.GetProperty("id").GetGuid());

        var deleteOperationId = Guid.NewGuid();
        graph.Enqueue(HttpStatusCode.NoContent);
        var delete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{syntheticOccurrenceId:D}" +
            $"?scope=instance&originalEventId={masterId:D}" +
            $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
            $"&clientOperationId={deleteOperationId:D}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        temp.Db.ChangeTracker.Clear();
        var master = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == masterId);
        Assert.Null(master.DeletedAt);
        Assert.Equal("Series Master", master.Title);
        var exception = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.OutlookEventId == "graph-occ-real-1");
        Assert.True(exception.IsException);
        Assert.Equal(masterId, exception.SeriesMasterId);
        Assert.Equal("CANCELLED", exception.Status);
        Assert.Equal(recurrenceId, exception.RecurrenceId);

        var core = graph.CoreRequests;
        Assert.Equal(3, core.Count);
        Assert.Equal("GET", core[0].Method);
        Assert.Contains("/graph-series-1/instances", core[0].Uri);
        Assert.Equal("PATCH", core[1].Method);
        Assert.Contains("graph-occ-real-1", core[1].Uri);
        Assert.DoesNotContain("/graph-series-1", new Uri(core[1].Uri).AbsolutePath);
        Assert.Equal("W/\"occ-etag\"", core[1].IfMatch);
        Assert.Equal("DELETE", core[2].Method);
        Assert.Contains("graph-occ-real-1", core[2].Uri);
        Assert.DoesNotContain("/graph-series-1", new Uri(core[2].Uri).AbsolutePath);
    }

    [SkippableFact]
    public async Task SyntheticOccurrenceDelete_ReusesLegacyOccurrenceRow_WithoutUniqueViolation()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var (masterId, recurrenceId) = await SeedSeriesMasterAsync(temp, seed);
        var legacyOccurrenceId = await SeedLegacyOccurrenceAsync(temp, seed, masterId, recurrenceId);
        var client = Authed(factory, seed.UserId);
        var operationId = Guid.NewGuid();

        graph.Enqueue(HttpStatusCode.OK, SeriesOccurrenceListJson);
        graph.Enqueue(HttpStatusCode.NoContent);
        var delete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{SyntheticOccurrenceId(masterId, recurrenceId):D}" +
            $"?scope=instance&originalEventId={masterId:D}" +
            $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
            $"&clientOperationId={operationId:D}");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var receipt = await ReadDataAsync(delete);
        Assert.Equal(operationId, receipt.GetProperty("clientOperationId").GetGuid());
        Assert.Equal("deleted", receipt.GetProperty("status").GetString());

        temp.Db.ChangeTracker.Clear();
        var rows = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "graph-occ-real-1")
            .ToListAsync();
        var occurrence = Assert.Single(rows);
        Assert.Equal(legacyOccurrenceId, occurrence.Id);
        Assert.True(occurrence.IsException);
        Assert.False(occurrence.IsSeriesMaster);
        Assert.Equal(masterId, occurrence.SeriesMasterId);
        Assert.Equal(recurrenceId, occurrence.RecurrenceId);
        Assert.Equal("CANCELLED", occurrence.Status);
        Assert.Null(occurrence.DeletedAt);

        var operation = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .AsNoTracking().SingleAsync(o => o.ClientOperationId == operationId);
        Assert.Equal("completed", operation.State);
        Assert.Single(graph.Requests, r => r.Method == "DELETE");
    }

    [SkippableFact]
    public async Task McpToolsCall_WritebackSuccess409And502_ExposeActionableReceipts()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var rest = Authed(factory, seed.UserId);
        var mcpToken = await CreateMcpWriteClientAsync(rest);
        var mcp = factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);

        var createOperationId = Guid.NewGuid();
        graph.Enqueue(HttpStatusCode.Created, GraphEventJson);
        var created = await CallMcpToolAsync(mcp, "create_event", new
        {
            calendarId = seed.CalendarId,
            title = "Created Subject",
            dtStart = "2026-11-30T09:00:00Z",
            dtEnd = "2026-11-30T10:00:00Z",
            clientOperationId = createOperationId,
        });
        Assert.False(created.IsError);
        Assert.Equal(0, created.Payload.GetProperty("code").GetInt32());
        var createdData = created.Payload.GetProperty("data");
        var eventId = createdData.GetProperty("id").GetGuid();
        Assert.Equal("W/\"etag-1\"", createdData.GetProperty("outlookEtag").GetString());
        Assert.Equal(createOperationId, createdData.GetProperty("clientOperationId").GetGuid());
        Assert.True(createdData.GetProperty("undoAvailable").GetBoolean());
        Assert.False(createdData.GetProperty("requiresConfirmation").GetBoolean());

        graph.Enqueue(HttpStatusCode.PreconditionFailed);
        graph.Enqueue(HttpStatusCode.OK, LatestEventJson);
        var conflict = await CallMcpToolAsync(mcp, "update_event", new
        {
            eventId,
            calendarId = seed.CalendarId,
            title = "Updated Subject",
            dtStart = "2026-11-30T11:00:00Z",
            dtEnd = "2026-11-30T12:00:00Z",
            scope = "instance",
            expectedEtag = "W/\"stale-etag\"",
        });
        Assert.True(conflict.IsError);
        Assert.Equal(409, conflict.Payload.GetProperty("code").GetInt32());
        Assert.Equal("Latest Remote Subject",
            conflict.Payload.GetProperty("latestEvent").GetProperty("title").GetString());
        Assert.Equal("W/\"latest-etag\"",
            conflict.Payload.GetProperty("latestEtag").GetString());
        Assert.True(conflict.Payload.GetProperty("noChangesApplied").GetBoolean());
        Assert.Equal("CONFLICT", conflict.Payload.GetProperty("errorCode").GetString());

        graph.EnqueueException(new HttpRequestException(
            "token=TOKEN-MUST-NOT-LEAK RAW-GRAPH-BODY"));
        var failed = await CallMcpToolAsync(mcp, "create_event", new
        {
            calendarId = seed.CalendarId,
            title = "Provider failure",
            dtStart = "2026-11-30T09:00:00Z",
            dtEnd = "2026-11-30T10:00:00Z",
            clientOperationId = Guid.NewGuid(),
        });
        Assert.True(failed.IsError);
        Assert.Equal(502, failed.Payload.GetProperty("code").GetInt32());
        Assert.Equal("PROVIDER_UNAVAILABLE",
            failed.Payload.GetProperty("errorCode").GetString());
        Assert.False(failed.Payload.GetProperty("noChangesApplied").GetBoolean());
        Assert.DoesNotContain("TOKEN-MUST-NOT-LEAK", failed.Payload.GetRawText());
        Assert.DoesNotContain("RAW-GRAPH-BODY", failed.Payload.GetRawText());

        _output.WriteLine(
            "MCP /mcp tools/call wrote through the real HTTP/PostgreSQL pipeline: success exposed outlookEtag/clientOperationId/undoAvailable; 409 exposed latestEvent/latestEtag/noChangesApplied directly; 502 exposed PROVIDER_UNAVAILABLE without leaking Graph secrets. Graph remained a deterministic fake.");
    }

    [SkippableFact]
    public async Task OriginalEventIdMismatch_RejectsUpdateAndDeleteWithoutGraphOrLocalChange()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var firstEventId = await SeedSimpleEventAsync(
            temp, seed, "First Event", "graph-first");
        var secondEventId = await SeedSimpleEventAsync(
            temp, seed, "Second Event", "graph-second");
        var client = Authed(factory, seed.UserId);

        var updateBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Must not be applied",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;
        var update = await client.PutAsync(
            $"/api/v1/calendar/events/{secondEventId:D}" +
            $"?scope=instance&originalEventId={firstEventId:D}" +
            $"&clientOperationId={Guid.NewGuid():D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);

        var delete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{secondEventId:D}" +
            $"?scope=instance&originalEventId={firstEventId:D}" +
            $"&clientOperationId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);

        var (masterId, recurrenceId) = await SeedSeriesMasterAsync(temp, seed);
        var unrelatedOccurrence = DateTimeOffset.Parse(
                recurrenceId, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .AddHours(1)
            .ToString("O");
        var mismatchedOccurrenceId = SyntheticOccurrenceId(masterId, unrelatedOccurrence);
        var syntheticDelete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{mismatchedOccurrenceId:D}" +
            $"?scope=instance&originalEventId={masterId:D}" +
            $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
            $"&clientOperationId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.BadRequest, syntheticDelete.StatusCode);

        Assert.Empty(graph.Requests);
        temp.Db.ChangeTracker.Clear();
        var first = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == firstEventId);
        var second = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == secondEventId);
        Assert.Equal("First Event", first.Title);
        Assert.Equal("Second Event", second.Title);
        Assert.Null(first.DeletedAt);
        Assert.Null(second.DeletedAt);

        _output.WriteLine(
            "originalEventId mismatches were rejected with 400 before Graph; neither target event was modified or soft-deleted.");
    }

    [SkippableFact]
    public async Task UpdateProviderFailures_MapGraphStatusesToDocumentedHttpSemantics()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var eventId = await SeedSimpleEventAsync(
            temp, seed, "Provider Status Target", "graph-status");
        var client = Authed(factory, seed.UserId);
        var body = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Attempted update",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;

        var cases = new (Action Enqueue, HttpStatusCode Expected, string ErrorCode, bool NoChangesApplied, bool RetryHint)[]
        {
            (() => graph.Enqueue(HttpStatusCode.BadRequest), HttpStatusCode.BadRequest,
                "GRAPH_BAD_REQUEST", true, false),
            (() => graph.Enqueue(HttpStatusCode.Forbidden), HttpStatusCode.Forbidden,
                "GRAPH_FORBIDDEN", true, false),
            (() => graph.Enqueue(HttpStatusCode.NotFound), HttpStatusCode.NotFound,
                "GRAPH_EVENT_NOT_FOUND", true, false),
            (() => graph.Enqueue(HttpStatusCode.TooManyRequests), HttpStatusCode.BadGateway,
                "PROVIDER_RATE_LIMITED", false, true),
            (() => graph.Enqueue(HttpStatusCode.InternalServerError), HttpStatusCode.BadGateway,
                "PROVIDER_UNAVAILABLE", false, true),
            (() => graph.EnqueueException(new OperationCanceledException("provider timeout")),
                HttpStatusCode.BadGateway, "PROVIDER_TIMEOUT", false, true)
        };

        foreach (var testCase in cases)
        {
            var operationId = Guid.NewGuid();
            testCase.Enqueue();
            var response = await client.PutAsync(
                $"/api/v1/calendar/events/{eventId:D}" +
                $"?expectedEtag=W%2F%22etag%22&clientOperationId={operationId:D}",
                Json(body));
            Assert.Equal(testCase.Expected, response.StatusCode);
            var receipt = await ReadDataAsync(response);
            Assert.Equal(testCase.ErrorCode, receipt.GetProperty("errorCode").GetString());
            Assert.Equal(operationId, receipt.GetProperty("clientOperationId").GetGuid());
            Assert.Equal(testCase.NoChangesApplied,
                receipt.GetProperty("noChangesApplied").GetBoolean());
            var message = receipt.GetProperty("userMessage").GetString()!;
            if (testCase.RetryHint)
                Assert.Contains("clientOperationId", message);
            else
                Assert.DoesNotContain("同一 clientOperationId", message);
        }

        temp.Db.ChangeTracker.Clear();
        var stored = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == eventId);
        Assert.Equal("Provider Status Target", stored.Title);
        Assert.Null(stored.DeletedAt);

        _output.WriteLine(
            "Graph 400/403/404 mapped to 400/403/404 with noChangesApplied=true and no same-id retry hint; 429/5xx/timeout mapped to structured 502 retry receipts with clientOperationId and noChangesApplied=false.");
    }

    [SkippableFact]
    public async Task WritebackValidation400_ReturnsOutlookWriteResultEnvelope()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var eventId = await SeedSimpleEventAsync(
            temp, seed, "Validation Target", "graph-validation");
        var client = Authed(factory, seed.UserId);
        var body = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Must not be applied",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;

        var updateOperationId = Guid.NewGuid();
        var eventValidation = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}" +
            $"?recurrenceId=not-a-date&clientOperationId={updateOperationId:D}",
            Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, eventValidation.StatusCode);
        var eventValidationData = await ReadDataAsync(eventValidation);
        Assert.Equal("INVALID_REQUEST",
            eventValidationData.GetProperty("errorCode").GetString());
        Assert.Equal(updateOperationId,
            eventValidationData.GetProperty("clientOperationId").GetGuid());
        Assert.True(eventValidationData.GetProperty("noChangesApplied").GetBoolean());

        var legacyOperationId = Guid.NewGuid();
        var legacyValidation = await client.PostAsync(
            "/api/v1/calendar/outlook/events/writeback",
            Json($$"""
            {
              "operation": "bogus",
              "calendarBindingId": "{{seed.BindingId}}",
              "scope": "instance",
              "clientOperationId": "{{legacyOperationId:D}}"
            }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, legacyValidation.StatusCode);
        var legacyValidationData = await ReadDataAsync(legacyValidation);
        Assert.Equal("INVALID_REQUEST",
            legacyValidationData.GetProperty("errorCode").GetString());
        Assert.Equal(legacyOperationId,
            legacyValidationData.GetProperty("clientOperationId").GetGuid());
        Assert.True(legacyValidationData.GetProperty("noChangesApplied").GetBoolean());

        var unknownUndoId = Guid.NewGuid();
        var undoValidation = await client.PostAsync(
            $"/api/v1/calendar/outlook/events/{unknownUndoId:D}/undo", null);
        Assert.Equal(HttpStatusCode.BadRequest, undoValidation.StatusCode);
        var undoValidationData = await ReadDataAsync(undoValidation);
        Assert.Equal("INVALID_REQUEST",
            undoValidationData.GetProperty("errorCode").GetString());
        Assert.Equal(unknownUndoId,
            undoValidationData.GetProperty("clientOperationId").GetGuid());
        Assert.True(undoValidationData.GetProperty("noChangesApplied").GetBoolean());

        Assert.Empty(graph.Requests);
        temp.Db.ChangeTracker.Clear();
        var stored = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == eventId);
        Assert.Equal("Validation Target", stored.Title);
        Assert.Null(stored.DeletedAt);

        _output.WriteLine(
            "Event PUT, legacy writeback and undo validation all returned 400 with the documented ApiResponse<OutlookWriteResult> envelope and no Graph request.");
    }

    [SkippableFact]
    public async Task SyntheticOccurrenceTargetResolutionFailures_ReturnStructuredReceipts()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var (masterId, recurrenceId) = await SeedSeriesMasterAsync(temp, seed);
        var client = Authed(factory, seed.UserId);
        var syntheticOccurrenceId = SyntheticOccurrenceId(masterId, recurrenceId);
        var operationIds = new List<Guid>();

        var cases = new (string Name, Action Enqueue, HttpStatusCode Expected, string ErrorCode)[]
        {
            ("not-found", () => graph.Enqueue(
                HttpStatusCode.OK, """{"value":[]}"""),
                HttpStatusCode.NotFound, "GRAPH_EVENT_NOT_FOUND"),
            ("forbidden", () => graph.Enqueue(HttpStatusCode.Forbidden),
                HttpStatusCode.Forbidden, "GRAPH_FORBIDDEN"),
            ("rate-limited", () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    graph.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "0");
            }, HttpStatusCode.TooManyRequests, "PROVIDER_RATE_LIMITED"),
            ("server-error", () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    graph.Enqueue(HttpStatusCode.InternalServerError, retryAfter: "0");
            }, HttpStatusCode.BadGateway, "PROVIDER_UNAVAILABLE"),
            ("timeout", () =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                    graph.EnqueueException(new OperationCanceledException("fake graph timeout"));
            }, HttpStatusCode.GatewayTimeout, "PROVIDER_TIMEOUT")
        };

        foreach (var testCase in cases)
        {
            var operationId = Guid.NewGuid();
            operationIds.Add(operationId);
            testCase.Enqueue();
            var response = await client.DeleteAsync(
                $"/api/v1/calendar/events/{syntheticOccurrenceId:D}" +
                $"?scope=instance&originalEventId={masterId:D}" +
                $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
                $"&clientOperationId={operationId:D}");

            Assert.True(testCase.Expected == response.StatusCode,
                $"{testCase.Name}: expected {(int)testCase.Expected}, got " +
                $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            var receipt = await ReadDataAsync(response);
            Assert.Equal(testCase.ErrorCode,
                receipt.GetProperty("errorCode").GetString());
            Assert.Equal(operationId,
                receipt.GetProperty("clientOperationId").GetGuid());
            Assert.True(receipt.GetProperty("noChangesApplied").GetBoolean());
            Assert.All(graph.Requests, request => Assert.Equal("GET", request.Method));
        }

        Assert.Equal(11, graph.Requests.Count);
        temp.Db.ChangeTracker.Clear();
        var operations = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .Where(o => operationIds.Contains(o.ClientOperationId))
            .ToListAsync();
        Assert.Equal(cases.Length, operations.Count);
        Assert.All(operations, operation => Assert.Equal("failed", operation.State));

        var batches = await temp.Db.Set<OutlookSyncBatchEntity>().ToListAsync();
        Assert.Equal(cases.Length, batches.Count);
        Assert.All(batches, batch => Assert.Equal("failed", batch.Status));

        var master = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == masterId);
        Assert.Equal("Series Master", master.Title);
        Assert.Null(master.DeletedAt);
        Assert.Empty(await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.SeriesMasterId == masterId && e.IsException)
            .ToListAsync());

        _output.WriteLine(
            "Synthetic occurrence lookup 404/403/429/5xx/timeout all produced structured receipts and failed ledger rows without touching the series master or escaping as 500.");
    }

    [SkippableFact]
    public async Task SyntheticOccurrenceMalformed2xx_IsLedgeredInsteadOfEscapingAs500()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var (masterId, recurrenceId) = await SeedSeriesMasterAsync(temp, seed);
        var client = Authed(factory, seed.UserId);
        var syntheticOccurrenceId = SyntheticOccurrenceId(masterId, recurrenceId);
        var operationId = Guid.NewGuid();

        graph.Enqueue(HttpStatusCode.OK, $$"""
        {
          "value": [
            {
              "@odata.etag": "W/\"occ-etag\"",
              "id": "graph-occ-malformed",
              "subject": "Malformed occurrence",
              "start": {"dateTime": "2026-11-30T09:00:00.0000000Z", "timeZone": "UTC"},
              "originalStart": "{{recurrenceId}}"
            }
          ]
        }
        """);

        var response = await client.DeleteAsync(
            $"/api/v1/calendar/events/{syntheticOccurrenceId:D}" +
            $"?scope=instance&originalEventId={masterId:D}" +
            $"&recurrenceId={Uri.EscapeDataString(recurrenceId)}" +
            $"&clientOperationId={operationId:D}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var receipt = await ReadDataAsync(response);
        Assert.Equal(operationId, receipt.GetProperty("clientOperationId").GetGuid());
        Assert.Equal("PROVIDER_UNAVAILABLE",
            receipt.GetProperty("errorCode").GetString());
        Assert.True(receipt.GetProperty("noChangesApplied").GetBoolean());

        temp.Db.ChangeTracker.Clear();
        var operation = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .SingleAsync(o => o.ClientOperationId == operationId);
        Assert.Equal("failed", operation.State);
        Assert.Equal("PROVIDER_UNAVAILABLE", operation.ErrorCode);

        var batch = await temp.Db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.Equal("failed", batch.Status);
        Assert.Contains("PROVIDER_UNAVAILABLE", batch.ErrorsJson);

        var master = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == masterId);
        Assert.Equal("Series Master", master.Title);
        Assert.Null(master.DeletedAt);
        Assert.Empty(await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.SeriesMasterId == masterId && e.IsException)
            .ToListAsync());

        _output.WriteLine(
            "A malformed 200 instances body produced a structured 502 receipt and a failed operation/batch ledger row instead of a 500 with no ledger.");
    }

    [SkippableFact]
    public async Task DeleteSeries_GraphSuccessThenLocalReadFailure_IsLedgeredNotLeftProcessing()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var (masterId, _) = await SeedSeriesMasterAsync(temp, seed);
        var client = Authed(factory, seed.UserId);
        var operationId = Guid.NewGuid();

        graph.AfterResponse = async request =>
        {
            if (request.Method == "DELETE")
            {
                await temp.Db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE events RENAME TO events_writeback_local_failure");
            }
        };
        graph.Enqueue(HttpStatusCode.NoContent);

        var response = await client.DeleteAsync(
            $"/api/v1/calendar/events/{masterId:D}" +
            $"?scope=series&clientOperationId={operationId:D}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var receipt = await ReadDataAsync(response);
        Assert.Equal(operationId, receipt.GetProperty("clientOperationId").GetGuid());
        Assert.Equal("LOCAL_STATE_PENDING", receipt.GetProperty("errorCode").GetString());
        Assert.False(receipt.GetProperty("noChangesApplied").GetBoolean());

        temp.Db.ChangeTracker.Clear();
        var operation = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .SingleAsync(o => o.ClientOperationId == operationId);
        Assert.Equal("failed", operation.State);
        Assert.Equal("LOCAL_STATE_PENDING", operation.ErrorCode);
        Assert.Null(operation.CompletedAt);

        var batch = await temp.Db.Set<OutlookSyncBatchEntity>().SingleAsync();
        Assert.Equal("failed", batch.Status);
        Assert.Contains("local-state-pending", batch.ErrorsJson);
        Assert.Contains("graph-delete", batch.StepsJson);
        Assert.Contains("success", batch.StepsJson);

        _output.WriteLine(
            "A local read failure after Graph DELETE success was persisted as LOCAL_STATE_PENDING/failed instead of leaving the operation in processing or escaping as 500.");
    }

    [SkippableFact]
    public async Task MissingOrSoftDeletedUpdateReplay_ComparesTheFullRequestPayload()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var eventId = await SeedSimpleEventAsync(
            temp, seed, "Replay Target", "graph-event-1");
        var client = Authed(factory, seed.UserId);
        var operationId = Guid.NewGuid();
        var updateBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Updated Subject",
          "dtStart": "2026-11-30T11:00:00Z",
          "dtEnd": "2026-11-30T12:00:00Z"
        }
        """;

        graph.Enqueue(HttpStatusCode.OK, UpdatedEventJson);
        var update = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}?clientOperationId={operationId:D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        await temp.Db.Database.ExecuteSqlRawAsync(
            "UPDATE events SET deleted_at = now() WHERE id = {0}", eventId);

        var softDeletedReplay = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}?clientOperationId={operationId:D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, softDeletedReplay.StatusCode);
        var softDeletedData = await ReadDataAsync(softDeletedReplay);
        Assert.Equal(operationId,
            softDeletedData.GetProperty("clientOperationId").GetGuid());
        Assert.True(softDeletedData.GetProperty("replayed").GetBoolean());

        await temp.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM events WHERE id = {0}", eventId);

        var missingReplay = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}?clientOperationId={operationId:D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, missingReplay.StatusCode);
        Assert.True((await ReadDataAsync(missingReplay))
            .GetProperty("replayed").GetBoolean());

        var differentBody = updateBody.Replace(
            "Updated Subject", "Different Payload", StringComparison.Ordinal);
        var conflict = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}?clientOperationId={operationId:D}",
            Json(differentBody));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictData = await ReadDataAsync(conflict);
        Assert.Equal("IDEMPOTENCY_CONFLICT",
            conflictData.GetProperty("errorCode").GetString());
        Assert.True(conflictData.GetProperty("noChangesApplied").GetBoolean());
        Assert.Equal(operationId,
            conflictData.GetProperty("clientOperationId").GetGuid());

        Assert.Single(graph.Requests);

        _output.WriteLine(
            "Soft-deleted and hard-deleted event update replays matched the original full payload; a changed payload returned IDEMPOTENCY_CONFLICT without returning the old success or calling Graph.");
    }

    [SkippableFact]
    public async Task UndoConflict_RetryWithSameDeterministicOperationIdSucceeds()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var eventId = await SeedSimpleEventAsync(
            temp, seed, "Undo Conflict Target", "graph-event-1");
        var client = Authed(factory, seed.UserId);
        var operationId = Guid.NewGuid();
        var updateBody = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Updated Subject",
          "dtStart": "2026-11-30T11:00:00Z",
          "dtEnd": "2026-11-30T12:00:00Z"
        }
        """;

        graph.Enqueue(HttpStatusCode.OK, UpdatedEventJson);
        var updated = await client.PutAsync(
            $"/api/v1/calendar/events/{eventId:D}" +
            $"?expectedEtag=W%2F%22etag%22&clientOperationId={operationId:D}",
            Json(updateBody));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        graph.EnforceIfMatch = true;
        graph.CurrentEtag = "W/\"latest-etag\"";
        graph.Enqueue(HttpStatusCode.OK, LatestEventJson);
        var conflict = await client.PostAsync(
            $"/api/v1/calendar/outlook/events/{operationId:D}/undo", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictData = await ReadDataAsync(conflict);
        Assert.Equal("CONFLICT", conflictData.GetProperty("errorCode").GetString());

        temp.Db.ChangeTracker.Clear();
        var original = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .SingleAsync(o => o.ClientOperationId == operationId);
        var undoOperation = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .SingleAsync(o => o.UndoOfOperationId == original.Id);
        Assert.Equal("conflict", undoOperation.State);
        var deterministicUndoId = undoOperation.ClientOperationId;

        graph.Enqueue(HttpStatusCode.OK, UndoRestoredEventJson);
        var retried = await client.PostAsync(
            $"/api/v1/calendar/outlook/events/{operationId:D}/undo", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var retriedText = await retried.Content.ReadAsStringAsync();
        Assert.DoesNotContain("IDEMPOTENCY_CONFLICT", retriedText);
        using (var retriedDoc = JsonDocument.Parse(retriedText))
        {
            var data = retriedDoc.RootElement.GetProperty("data");
            Assert.Equal("updated", data.GetProperty("status").GetString());
            Assert.Equal(deterministicUndoId,
                data.GetProperty("clientOperationId").GetGuid());
        }

        temp.Db.ChangeTracker.Clear();
        var completedUndo = await temp.Db.Set<OutlookWritebackOperationEntity>()
            .SingleAsync(o => o.UndoOfOperationId == original.Id);
        Assert.Equal(deterministicUndoId, completedUndo.ClientOperationId);
        Assert.Equal("completed", completedUndo.State);
        Assert.Equal(2, completedUndo.AttemptCount);
        Assert.Equal(
            new[] { "PATCH", "PATCH", "GET", "PATCH" },
            graph.CoreRequests.Select(request => request.Method).ToList());
        Assert.Equal("W/\"updated-etag\"", graph.CoreRequests[1].IfMatch);
        Assert.Equal("W/\"latest-etag\"", graph.CoreRequests[^1].IfMatch);

        var stored = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == eventId);
        Assert.Equal("Original Title", stored.Title);

        _output.WriteLine(
            "Undo 412 retried with the same deterministic undo clientOperationId, reached Graph again, completed, and did not return a forged IDEMPOTENCY_CONFLICT.");
    }

    [SkippableFact]
    public async Task PreWriteGet404AndReadTimeout_DifferFromPostWriteUnknown()
    {
        var graph = new GraphQueueHandler();
        var (temp, factory) = await NewIsolatedHostAsync(graph);
        await using var _ = temp;
        using var factoryScope = factory;
        var seed = await SeedAsync(temp);
        var client = Authed(factory, seed.UserId);
        var body = $$"""
        {
          "calendarId": "{{seed.CalendarId}}",
          "title": "Must not be applied",
          "dtStart": "2026-11-30T09:00:00Z",
          "dtEnd": "2026-11-30T10:00:00Z"
        }
        """;

        var notFoundEventId = await SeedSimpleEventAsync(
            temp, seed, "Pre-read 404", "graph-pre-read-404");
        var notFoundEntity = await temp.Db.Set<EventEntity>()
            .SingleAsync(e => e.Id == notFoundEventId);
        notFoundEntity.OutlookEtag = null;
        await temp.Db.SaveChangesAsync();
        temp.Db.ChangeTracker.Clear();

        graph.Enqueue(HttpStatusCode.NotFound);
        var notFoundOperationId = Guid.NewGuid();
        var notFound = await client.PutAsync(
            $"/api/v1/calendar/events/{notFoundEventId:D}" +
            $"?clientOperationId={notFoundOperationId:D}",
            Json(body));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var notFoundData = await ReadDataAsync(notFound);
        Assert.Equal("GRAPH_EVENT_NOT_FOUND",
            notFoundData.GetProperty("errorCode").GetString());
        Assert.Equal(notFoundOperationId,
            notFoundData.GetProperty("clientOperationId").GetGuid());
        Assert.True(notFoundData.GetProperty("noChangesApplied").GetBoolean());
        Assert.Equal("GET", graph.CoreRequests[^1].Method);

        temp.Db.ChangeTracker.Clear();
        var stillNotFound = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == notFoundEventId);
        Assert.Equal("Pre-read 404", stillNotFound.Title);
        Assert.DoesNotContain(
            graph.CoreRequests, request => request.Method == "PATCH");

        var readTimeoutEventId = await SeedSimpleEventAsync(
            temp, seed, "Pre-read timeout", "graph-pre-read-timeout");
        var readTimeoutEntity = await temp.Db.Set<EventEntity>()
            .SingleAsync(e => e.Id == readTimeoutEventId);
        readTimeoutEntity.OutlookEtag = null;
        await temp.Db.SaveChangesAsync();
        temp.Db.ChangeTracker.Clear();

        var coreRequestsBeforeReadTimeout = graph.CoreRequests.Count;
        for (var attempt = 0; attempt < 3; attempt++)
            graph.EnqueueException(new OperationCanceledException("fake pre-read timeout"));
        var readTimeoutOperationId = Guid.NewGuid();
        var readTimeout = await client.PutAsync(
            $"/api/v1/calendar/events/{readTimeoutEventId:D}" +
            $"?clientOperationId={readTimeoutOperationId:D}",
            Json(body));
        Assert.Equal(HttpStatusCode.GatewayTimeout, readTimeout.StatusCode);
        var readTimeoutData = await ReadDataAsync(readTimeout);
        Assert.Equal("PROVIDER_TIMEOUT",
            readTimeoutData.GetProperty("errorCode").GetString());
        Assert.True(readTimeoutData.GetProperty("noChangesApplied").GetBoolean());
        Assert.Contains("尚未写入任何内容",
            readTimeoutData.GetProperty("userMessage").GetString());
        Assert.Equal(
            coreRequestsBeforeReadTimeout + 3, graph.CoreRequests.Count);
        Assert.All(
            graph.CoreRequests.Skip(coreRequestsBeforeReadTimeout),
            request => Assert.Equal("GET", request.Method));

        temp.Db.ChangeTracker.Clear();
        var stillReadTimeout = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == readTimeoutEventId);
        Assert.Equal("Pre-read timeout", stillReadTimeout.Title);

        var writeTimeoutEventId = await SeedSimpleEventAsync(
            temp, seed, "Write timeout", "graph-write-timeout");
        graph.EnqueueException(new OperationCanceledException("fake post-write timeout"));
        var writeTimeoutOperationId = Guid.NewGuid();
        var writeTimeout = await client.PutAsync(
            $"/api/v1/calendar/events/{writeTimeoutEventId:D}" +
            $"?clientOperationId={writeTimeoutOperationId:D}",
            Json(body));
        Assert.Equal(HttpStatusCode.BadGateway, writeTimeout.StatusCode);
        var writeTimeoutData = await ReadDataAsync(writeTimeout);
        Assert.Equal("PROVIDER_TIMEOUT",
            writeTimeoutData.GetProperty("errorCode").GetString());
        Assert.False(writeTimeoutData.GetProperty("noChangesApplied").GetBoolean());
        Assert.Equal("PATCH", graph.CoreRequests[^1].Method);

        temp.Db.ChangeTracker.Clear();
        var stillWriteTimeout = await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == writeTimeoutEventId);
        Assert.Equal("Write timeout", stillWriteTimeout.Title);

        _output.WriteLine(
            "Pre-write GET 404 returned not-found/noChangesApplied=true; pre-read timeout returned error/PROVIDER_TIMEOUT with no remote write; post-write timeout remained unknown with noChangesApplied=false.");
    }

    private static async Task<(TempMigrationDatabase Db, WebApplicationFactory<Program> Factory)>
        NewIsolatedHostAsync(GraphQueueHandler graph)
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_outlook_writeback");
        await temp.MigrateAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-r03-dp-{Guid.NewGuid():N}"));
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IOutlookAccessTokenProvider>(_ => new E2ETokenProvider());
                services.AddHttpClient("outlook")
                    .ConfigurePrimaryHttpMessageHandler(() => graph);
            });
        });
        return (temp, factory);
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, Guid userId)
    {
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.GenerateAccessToken(userId, $"r03-{userId:N}", "user"));
        return client;
    }

    private static async Task<string> CreateMcpWriteClientAsync(HttpClient rest)
    {
        var created = await rest.PostAsJsonAsync(
            "/api/v1/mcp/clients", new { name = $"r03-mcp-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var clientId = data.GetProperty("client").GetProperty("id").GetGuid();
        var token = data.GetProperty("token").GetString()!;

        var permissions = McpToolCatalog.DefaultPermissions();
        permissions["write"] = permissions["write"].ToDictionary(
            entry => entry.Key, _ => true);
        var updated = await rest.PutAsJsonAsync(
            $"/api/v1/mcp/clients/{clientId:D}", new { permissions });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        return token;
    }

    private static async Task<(bool IsError, JsonElement Payload)> CallMcpToolAsync(
        HttpClient mcp, string name, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name, arguments }
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var response = await mcp.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {raw}");
        var dataLine = raw.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(
            dataLine is null ? raw : dataLine["data:".Length..].Trim());
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
        var result = root.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray()
            .First(block => block.GetProperty("type").GetString() == "text")
            .GetProperty("text")
            .GetString();
        using var payload = JsonDocument.Parse(text!);
        return (isError, payload.RootElement.Clone());
    }

    private static async Task<Seed> SeedAsync(TempMigrationDatabase temp)
    {
        var userId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        temp.Db.Add(new UserEntity
        {
            Id = userId,
            Username = $"r03-{userId:N}",
            Email = $"r03-{userId:N}@example.test",
            PasswordHash = "test-only",
            Role = "user",
            IsActive = true
        });
        temp.Db.Add(new CalendarEntity
        {
            Id = calendarId,
            UserId = userId,
            Name = "Outlook Calendar",
            Source = "outlook",
            IsDefault = true
        });
        temp.Db.Add(new OutlookConnectionEntity
        {
            Id = connectionId,
            UserId = userId,
            Status = "connected",
            TokenHealth = "healthy"
        });
        temp.Db.Add(new OutlookCalendarBindingEntity
        {
            Id = bindingId,
            ConnectionId = connectionId,
            PimCalendarId = calendarId,
            GraphCalendarId = "graph-cal-1",
            Name = "Outlook Calendar",
            CanEdit = true,
            IsSelected = true,
            RemoteState = "active"
        });
        await temp.Db.SaveChangesAsync();
        return new Seed(userId, calendarId, bindingId);
    }

    private static async Task<(Guid MasterId, string RecurrenceId)> SeedSeriesMasterAsync(
        TempMigrationDatabase temp, Seed seed)
    {
        var masterId = Guid.NewGuid();
        var seriesStart = new DateTimeOffset(2026, 11, 29, 9, 0, 0, TimeSpan.Zero);
        var recurrenceId = new DateTimeOffset(2026, 11, 30, 9, 0, 0, TimeSpan.Zero)
            .ToUniversalTime()
            .ToString("O");
        temp.Db.Add(new EventEntity
        {
            Id = masterId,
            CalendarId = seed.CalendarId,
            Uid = "ical-series",
            Title = "Series Master",
            DtStart = seriesStart,
            DtEnd = seriesStart.AddHours(1),
            Source = "outlook",
            Status = "CONFIRMED",
            RRule = "FREQ=DAILY;COUNT=5",
            IsSeriesMaster = true,
            TimeZoneId = "UTC",
            SourceTimeZoneId = "UTC",
            OriginalStartTimeZone = "UTC",
            OriginalEndTimeZone = "UTC",
            OutlookEventId = "graph-series-1",
            OutlookCalendarBindingId = seed.BindingId,
            OutlookConnectionId = await temp.Db.Set<OutlookCalendarBindingEntity>()
                .Where(b => b.Id == seed.BindingId)
                .Select(b => b.ConnectionId)
                .SingleAsync(),
            OutlookEventType = "seriesMaster",
            OutlookEtag = "W/\"master-etag\""
        });
        await temp.Db.SaveChangesAsync();
        return (masterId, recurrenceId);
    }

    private static async Task<Guid> SeedSimpleEventAsync(
        TempMigrationDatabase temp, Seed seed, string title, string graphEventId)
    {
        var connectionId = await temp.Db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.Id == seed.BindingId)
            .Select(b => b.ConnectionId)
            .SingleAsync();
        var eventId = Guid.NewGuid();
        temp.Db.Add(new EventEntity
        {
            Id = eventId,
            CalendarId = seed.CalendarId,
            Uid = $"{graphEventId}@outlook",
            Title = title,
            DtStart = new DateTimeOffset(2026, 11, 30, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook",
            Status = "CONFIRMED",
            OutlookEventId = graphEventId,
            OutlookCalendarBindingId = seed.BindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "singleInstance",
            OutlookEtag = "W/\"etag\""
        });
        await temp.Db.SaveChangesAsync();
        return eventId;
    }

    private static Guid SyntheticOccurrenceId(Guid masterId, string recurrenceId)
        => RecurrenceService.DeriveOccurrenceId(
            masterId,
            DateTimeOffset.Parse(
                recurrenceId,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));

    private static async Task<Guid> SeedLegacyOccurrenceAsync(
        TempMigrationDatabase temp, Seed seed, Guid masterId, string recurrenceId)
    {
        var connectionId = await temp.Db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.Id == seed.BindingId)
            .Select(b => b.ConnectionId)
            .SingleAsync();
        var occurrenceId = Guid.NewGuid();
        temp.Db.Add(new EventEntity
        {
            Id = occurrenceId,
            CalendarId = seed.CalendarId,
            Uid = "ical-series",
            Title = "Legacy occurrence",
            DtStart = new DateTimeOffset(2026, 11, 30, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook",
            Status = "CONFIRMED",
            IsException = false,
            IsSeriesMaster = false,
            SeriesMasterId = masterId,
            RecurrenceId = recurrenceId,
            OutlookEventId = "graph-occ-real-1",
            OutlookCalendarBindingId = seed.BindingId,
            OutlookConnectionId = connectionId,
            OutlookEventType = "occurrence",
            OutlookSeriesMasterId = "graph-series-1"
        });
        await temp.Db.SaveChangesAsync();
        return occurrenceId;
    }

    private static async Task<HashSet<string>> ReadIndexNamesAsync(TempMigrationDatabase temp)
    {
        await using var command = temp.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'outlook_writeback_operations'";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static StringContent Json(string body)
        => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private sealed record Seed(Guid UserId, Guid CalendarId, Guid BindingId);

    private sealed class E2ETokenProvider : IOutlookAccessTokenProvider
    {
        public Task<string> AcquireAccessTokenAsync(
            Guid connectionId, bool forceRefresh, CancellationToken ct)
            => Task.FromResult("e2e-access-token");
    }

    private sealed record GraphRequest(string Method, string Uri, string? IfMatch);

    private sealed class GraphQueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
        private readonly List<GraphRequest> _requests = [];

        public bool EnforceIfMatch { get; set; }
        public string? CurrentEtag { get; set; }
        public Func<GraphRequest, Task>? AfterResponse { get; set; }

        public IReadOnlyList<GraphRequest> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToList();
            }
        }

        public IReadOnlyList<GraphRequest> CoreRequests =>
            Requests.Where(r => !r.Uri.Contains("/attachments", StringComparison.Ordinal)).ToList();

        public void Enqueue(
            HttpStatusCode status, string? body = null, string? retryAfter = null)
        {
            lock (_queue)
            {
                _queue.Enqueue(request =>
                {
                    var response = new HttpResponseMessage(status);
                    if (body is not null)
                        response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (retryAfter is not null)
                        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
                    return response;
                });
            }
        }

        public void EnqueueException(Exception exception)
        {
            lock (_queue)
                _queue.Enqueue(_ => throw exception);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new GraphRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.IfMatch.ToString());
            lock (_requests)
                _requests.Add(snapshot);

            if (EnforceIfMatch
                && CurrentEtag is not null
                && request.Method != HttpMethod.Get
                && !string.Equals(
                    request.Headers.IfMatch.ToString(),
                    CurrentEtag,
                    StringComparison.Ordinal))
            {
                var precondition = new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                if (AfterResponse is not null)
                    await AfterResponse(snapshot);
                return precondition;
            }

            Func<HttpRequestMessage, HttpResponseMessage>? script;
            lock (_queue)
                script = _queue.Count > 0 ? _queue.Dequeue() : null;
            if (script is null)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/attachments", StringComparison.Ordinal))
                {
                    var attachments = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"value":[]}""", Encoding.UTF8, "application/json")
                    };
                    if (AfterResponse is not null)
                        await AfterResponse(snapshot);
                    return attachments;
                }
                throw new InvalidOperationException(
                    $"No fake Graph response queued for {request.Method} {request.RequestUri}");
            }
            try
            {
                var response = script(request);
                if (AfterResponse is not null)
                    await AfterResponse(snapshot);
                return response;
            }
            catch
            {
                throw;
            }
        }
    }
}
