using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
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
            var latest = conflictDoc.RootElement.GetProperty("data").GetProperty("latestEvent");
            Assert.Equal("Latest Remote Subject", latest.GetProperty("title").GetString());
        }
        temp.Db.ChangeTracker.Clear();
        Assert.Equal("Updated Subject",
            (await temp.Db.Set<EventEntity>().AsNoTracking().SingleAsync(e => e.Id == eventId)).Title);

        var deleteOperationId = Guid.NewGuid();
        graph.Enqueue(HttpStatusCode.NoContent);
        var delete = await client.DeleteAsync(
            $"/api/v1/calendar/events/{eventId}?clientOperationId={deleteOperationId:D}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Contains(deleteOperationId.ToString("D"), await delete.Content.ReadAsStringAsync());
        temp.Db.ChangeTracker.Clear();
        Assert.NotNull(await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.Id == eventId).Select(e => e.DeletedAt).SingleAsync());

        var deleteReplay = await client.DeleteAsync(
            $"/api/v1/calendar/events/{eventId}?clientOperationId={deleteOperationId:D}");
        Assert.Equal(HttpStatusCode.OK, deleteReplay.StatusCode);
        Assert.Contains("clientOperationId=", await deleteReplay.Content.ReadAsStringAsync());
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
        var syntheticOccurrenceId = Guid.NewGuid();
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

    private static async Task<Seed> SeedAsync(TempMigrationDatabase temp)
    {
        var userId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
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

        public void Enqueue(HttpStatusCode status, string? body = null)
        {
            lock (_queue)
            {
                _queue.Enqueue(request =>
                {
                    var response = new HttpResponseMessage(status);
                    if (body is not null)
                        response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    return response;
                });
            }
        }

        public void EnqueueException(Exception exception)
        {
            lock (_queue)
                _queue.Enqueue(_ => throw exception);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(new GraphRequest(
                    request.Method.Method,
                    request.RequestUri!.ToString(),
                    request.Headers.IfMatch.ToString()));
            }

            Func<HttpRequestMessage, HttpResponseMessage>? script;
            lock (_queue)
                script = _queue.Count > 0 ? _queue.Dequeue() : null;
            if (script is null)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/attachments", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"value":[]}""", Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromException<HttpResponseMessage>(
                    new InvalidOperationException(
                        $"No fake Graph response queued for {request.Method} {request.RequestUri}"));
            }
            try
            {
                return Task.FromResult(script(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
