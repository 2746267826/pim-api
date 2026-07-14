using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class GraphCalendarClientTests
{
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly Uri GraphBase = new("https://graph.microsoft.com/v1.0");

    [Theory]
    [MemberData(nameof(NextLinkScenarios))]
    public void IsAllowedNextLink_ValidInvalid(string url, bool expected)
        => Assert.Equal(expected, GraphCalendarClient.IsAllowedNextLink(url));

    public static TheoryData<string, bool> NextLinkScenarios => new()
    {
        { "https://graph.microsoft.com/v1.0/me/calendarGroups?s=s", true },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/1/calendars?s=s", true },
        { "https://graph.microsoft.com/v1.0/me/calendars?s=s", true },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView?s=s", true },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/events?s=s", true },
        { "https://graph.microsoft.com:443/v1.0/me/calendars?s=s", true },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView/?$skiptoken=a", true },
        { "https://graph.microsoft.com/v1.0/me/calendars/AAMkA%2Fxxx%3D%3D/calendarView?$skiptoken=a", true },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView?$skiptoken=" + new string('A', 2048), true },
        { "https://graph.microsoft.com/beta/me/calendarGroups", false },
        { "https://evil.com/v1.0/me/calendarGroups", false },
        { "https://graph.microsoft.us/v1.0/me/calendars?s=s", false },
        { "/v1.0/me/calendarGroups", false },
        { "https://graph.microsoft.com:8080/v1.0/me/calendarGroups", false },
        { "https://user:pass@graph.microsoft.com/v1.0/me/calendarGroups", false },
        { "https://graph.microsoft.com/v1.0/me/drive/root", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/1", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/1/calendars/2", false },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView/e1", false },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/events/e1", false },
        { "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView?s=s#frag", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/../me/calendarGroups?s=s", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/%2e%2e/me/calendarGroups?s=s", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/%2E%2E/me/calendarGroups?s=s", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/%252e%252e/me/calendarGroups?s=s", false },
        { "https://graph.microsoft.com/v1.0/me/calendars/./calendarView?s=s", false },
        { "https://graph.microsoft.com/v1.0/me/calendarGroups/../calendars?$skiptoken=x", false },
    };

    [Fact]
    public async Task GetMeAsync_CorrectRequest()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1","displayName":"U","userPrincipalName":"u@t"}""");

        var result = await client.GetMeAsync(ConnectionId, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me?$select=id,displayName,userPrincipalName",
            req.RequestUri!.AbsoluteUri);
        var preferValues = req.Headers.GetValues("Prefer").ToArray();
        Assert.Contains("outlook.timezone=\"UTC\"", preferValues);
        Assert.Contains("IdType=\"ImmutableId\"", preferValues);
        Assert.Equal("Bearer test-access-token", req.Headers.Authorization?.ToString());
        Assert.Equal("U", result.GetProperty("displayName").GetString());
        Assert.Equal(1, tokens.CallCount);
        Assert.False(tokens.LastForceRefresh);
    }

    [Fact]
    public async Task GetCalendarGroupsAsync_CorrectRequest()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        var _ = await CollectPages(client.GetCalendarGroupsAsync(ConnectionId, default));

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendarGroups?$select=id,name",
            req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetGroupCalendarsAsync_EscapedIdAndSelect()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CollectPages(client.GetGroupCalendarsAsync(ConnectionId, "group id/1", default));

        var req = Assert.Single(handler.Requests);
        Assert.Contains("/calendarGroups/" + Uri.EscapeDataString("group id/1") + "/calendars",
            req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetCalendarsAsync_CorrectRequest()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CollectPages(client.GetCalendarsAsync(ConnectionId, default));

        var req = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/me/calendars?$select=id,name,color,owner,isDefaultCalendar,canEdit,canViewPrivateItems",
            req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetCalendarViewAsync_UtcRangeAndSelect()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        var start = new DateTimeOffset(2026, 7, 1, 1, 0, 0, TimeSpan.FromHours(1));
        var end = new DateTimeOffset(2026, 7, 31, 2, 0, 0, TimeSpan.FromHours(2));
        await CollectPages(client.GetCalendarViewAsync(ConnectionId, "cal1", start, end, default));

        var req = handler.Requests[0];
        var uri = req.RequestUri!.AbsoluteUri;
        Assert.Contains("startDateTime=2026-07-01T00%3A00%3A00Z", uri);
        Assert.Contains("endDateTime=2026-07-31T00%3A00%3A00Z", uri);
        Assert.Contains("$select=id,subject,body,start,end,location,isAllDay,type,seriesMasterId,recurrence,iCalUId,changeKey,originalStartTimeZone,originalEndTimeZone", uri);
        Assert.Contains(req.Headers, h => h.Key == "Prefer" && h.Value.Contains("outlook.timezone=\"UTC\""));
        Assert.Contains(req.Headers, h => h.Key == "Prefer" && h.Value.Contains("IdType=\"ImmutableId\""));
    }

    [Fact]
    public async Task GetEventsAsync_CorrectRequest()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await CollectPages(client.GetEventsAsync(ConnectionId, "cal1", default));

        var req = Assert.Single(handler.Requests);
        Assert.Contains("/me/calendars/cal1/events", req.RequestUri!.AbsoluteUri);
        Assert.Contains("$select=id,subject,body,start,end,location,isAllDay,type,seriesMasterId,recurrence,iCalUId,changeKey,originalStartTimeZone,originalEndTimeZone", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetEventAsync_CorrectRequest()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"e1","subject":"S"}""");

        var result = await client.GetEventAsync(ConnectionId, "cal1", "e1", default);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("/me/calendars/cal1/events/e1", req.RequestUri!.AbsoluteUri);
        Assert.NotNull(result);
        Assert.Equal("S", result.Value.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task GetEventAsync_NotFound_ReturnsNull()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.NotFound);

        var result = await client.GetEventAsync(ConnectionId, "cal1", "missing", default);

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateEventAsync_CorrectMethodPathHeaders()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Created, """{"id":"new1","subject":"Created"}""");

        var payload = new Dictionary<string, object?>
        {
            ["subject"] = "New Event",
            ["transactionId"] = "tx-123"
        };
        var result = await client.CreateEventAsync(ConnectionId, "cal1", payload, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/me/calendars/cal1/events", req.RequestUri!.AbsoluteUri);

        var body = await req.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("New Event", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal("tx-123", doc.RootElement.GetProperty("transactionId").GetString());

        Assert.Equal("Created", result.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task UpdateEventAsync_CorrectMethodPathIfMatch()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"e1","subject":"Updated"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "Updated" };
        var result = await client.UpdateEventAsync(ConnectionId, "cal1", "e1", "\"etag-abc\"", payload, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Contains("/me/calendars/cal1/events/e1", req.RequestUri!.AbsoluteUri);
        Assert.Equal("\"etag-abc\"", req.Headers.GetValues("If-Match").Single());

        var body = await req.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Updated", doc.RootElement.GetProperty("subject").GetString());

        Assert.Equal("Updated", result.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task DeleteEventAsync_204_Success()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.NoContent);

        await client.DeleteEventAsync(ConnectionId, "cal1", "e1", "\"etag-x\"", default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("/me/calendars/cal1/events/e1", req.RequestUri!.AbsoluteUri);
        Assert.Equal("\"etag-x\"", req.Headers.GetValues("If-Match").Single());
    }

    [Fact]
    public async Task DeleteEventAsync_404_Idempotent()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.NotFound);

        await client.DeleteEventAsync(ConnectionId, "cal1", "gone", "etag-y", default);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateEventAsync_WeakETag_PreservedVerbatim()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"e1","subject":"Updated"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "Updated" };
        var result = await client.UpdateEventAsync(ConnectionId, "cal1", "e1", "W/\"etag-abc\"", payload, default);

        var req = Assert.Single(handler.Requests);
        var ifMatch = req.Headers.GetValues("If-Match").Single();
        Assert.Equal("W/\"etag-abc\"", ifMatch);
    }

    [Fact]
    public async Task DeleteEventAsync_WeakETag_PreservedVerbatim()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.NoContent);

        await client.DeleteEventAsync(ConnectionId, "cal1", "e1", "W/\"etag-xyz\"", default);

        var req = Assert.Single(handler.Requests);
        var ifMatch = req.Headers.GetValues("If-Match").Single();
        Assert.Equal("W/\"etag-xyz\"", ifMatch);
    }

    [Fact]
    public async Task UpdateEventAsync_401Replay_WeakETag_PreservedOnBoth()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.OK, """{"id":"e1","subject":"Updated"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "Updated" };
        await client.UpdateEventAsync(ConnectionId, "cal1", "e1", "W/\"etag-abc\"", payload, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("W/\"etag-abc\"", handler.Requests[0].Headers.GetValues("If-Match").Single());
        Assert.Equal("W/\"etag-abc\"", handler.Requests[1].Headers.GetValues("If-Match").Single());
    }

    [Fact]
    public async Task DeleteEventAsync_412_ThrowsGraphRequestException()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.PreconditionFailed);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.DeleteEventAsync(ConnectionId, "cal1", "e1", "stale-etag", default));

        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
    }

    [Fact]
    public async Task DeleteEventAsync_First401_ForceRefreshReplaySucceeds()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.NoContent);

        await client.DeleteEventAsync(ConnectionId, "cal1", "e1", "\"etag-x\"", default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, tokens.CallCount);
        Assert.True(tokens.LastForceRefresh);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("\"etag-x\"", handler.Requests[1].Headers.GetValues("If-Match").Single());
    }

    [Fact]
    public async Task DeleteEventAsync_Second401_ThrowsReauth()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            client.DeleteEventAsync(ConnectionId, "cal1", "e1", "etag-x", default));

        Assert.Equal("graph-unauthorized", ex.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Write_DisposesHttpRequestMessageAfterCompletion()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Created, """{"id":"new1"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        await client.CreateEventAsync(ConnectionId, "cal1", payload, default);

        var original = Assert.Single(handler.OriginalRequests);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            original.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task Write_DisposesRequestsAcross401Replay()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Created, """{"id":"new1"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        await client.CreateEventAsync(ConnectionId, "cal1", payload, default);

        Assert.Equal(2, handler.OriginalRequests.Count);
        foreach (var original in handler.OriginalRequests)
        {
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                original.Content!.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Pagination_FollowsValidNextLink()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"p1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendarGroups?$skiptoken=a"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"p2"}]}""");

        var pages = await CollectPages(client.GetCalendarGroupsAsync(ConnectionId, default));

        Assert.Equal(2, pages.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("p1", pages[0].Items[0].GetProperty("id").GetString());
        Assert.Equal("p2", pages[1].Items[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Pagination_FollowsTrailingSlashCalendarViewNextLink()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK,
            """{"value":[{"id":"e1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView/?$skiptoken=a"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"e2"}]}""");

        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var pages = await CollectPages(client.GetCalendarViewAsync(ConnectionId, "c1", start, end, default));

        Assert.Equal(2, pages.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/me/calendars/c1/calendarView/?$skiptoken=a",
            handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal("e1", pages[0].Items[0].GetProperty("id").GetString());
        Assert.Equal("e2", pages[1].Items[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Pagination_InvalidNextLink_Rejected()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"p1"}],"@odata.nextLink":"https://evil.com/v1.0/me/calendarGroups"}""");

        var pages = new List<GraphPage>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var page in client.GetCalendarGroupsAsync(ConnectionId, default))
                pages.Add(page);
        });

        Assert.Single(pages);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ClonedJsonElement_ReadableAfterEnumeration()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"e1","subject":"S1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendars/c1/events?$skiptoken=x"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"e2","subject":"S2"}]}""");

        var pages = await CollectPages(client.GetEventsAsync(ConnectionId, "c1", default));

        Assert.Equal(2, pages.Count);
        Assert.Equal("S1", pages[0].Items[0].GetProperty("subject").GetString());
        Assert.Equal("S2", pages[1].Items[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Read_Retries429_ThenSucceeds()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "2");
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1","displayName":"U","userPrincipalName":"u@t"}""");

        var result = await client.GetMeAsync(ConnectionId, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(clock.RequestedDelays);
        Assert.True(clock.RequestedDelays[0] <= TimeSpan.FromSeconds(30));
        Assert.Equal("U", result.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Write_503_FailsWithoutRetry()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.CreateEventAsync(ConnectionId, "cal1", new Dictionary<string, object?>(), default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(clock.RequestedDelays);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Read_RetriesTransient_AtMost3Sends(int statusCode)
    {
        var (client, handler, _, clock) = CreateClient();
        handler.Enqueue((HttpStatusCode)statusCode);
        handler.Enqueue((HttpStatusCode)statusCode);
        handler.Enqueue((HttpStatusCode)statusCode);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal((HttpStatusCode)statusCode, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, clock.RequestedDelays.Count);
    }

    [Fact]
    public async Task Read_RetriesNetworkError_AtMost3Sends()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.EnqueueException(new HttpRequestException("net1"));
        handler.EnqueueException(new HttpRequestException("net2"));
        handler.EnqueueException(new HttpRequestException("net3"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, clock.RequestedDelays.Count);
    }

    [Fact]
    public async Task Read_Timeout_RetriedAsTimeout()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1","displayName":"U"}""");

        var result = await client.GetMeAsync(ConnectionId, default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, clock.RequestedDelays.Count);
        Assert.Equal("U", result.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Read_RetryAfter_Bounded()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "60");
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1"}""");

        await client.GetMeAsync(ConnectionId, default);

        Assert.NotEmpty(clock.RequestedDelays);
        Assert.True(clock.RequestedDelays[0] <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Read_RetryAfterAbsoluteDate_BoundedAndClamped()
    {
        var (client, handler, _, clock) = CreateClient();
        clock.UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "Sun, 12 Jul 2026 12:00:10 GMT");
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1"}""");

        await client.GetMeAsync(ConnectionId, default);

        Assert.NotEmpty(clock.RequestedDelays);
        Assert.True(clock.RequestedDelays[0] <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Read_First401_UsesForceRefresh_ReplaySucceeds()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.OK, """{"id":"u1","displayName":"U","userPrincipalName":"u@t"}""");

        var result = await client.GetMeAsync(ConnectionId, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, tokens.CallCount);
        Assert.True(tokens.LastForceRefresh);
        Assert.Equal("U", result.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Read_Second401_ThrowsReauth()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal("graph-unauthorized", ex.Code);
    }

    [Fact]
    public async Task Read_403_ThrowsGraphRequestException()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Write_First401_UsesForceRefresh_ReplaySucceeds()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Created, """{"id":"new1","subject":"Created"}""");

        var payload = new Dictionary<string, object?> { ["subject"] = "New" };
        var result = await client.CreateEventAsync(ConnectionId, "cal1", payload, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, tokens.CallCount);
        Assert.True(tokens.LastForceRefresh);
        Assert.Equal("Created", result.GetProperty("subject").GetString());

        var body2 = await handler.Requests[1].Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body2);
        Assert.Equal("New", doc.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Write_Second401_ThrowsReauth()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        var ex = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            client.CreateEventAsync(ConnectionId, "cal1", payload, default));

        Assert.Equal("graph-unauthorized", ex.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Write_DoesNotRetryTransient(int statusCode)
    {
        var (client, handler, _, clock) = CreateClient();
        handler.Enqueue((HttpStatusCode)statusCode);

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.CreateEventAsync(ConnectionId, "cal1", payload, default));

        Assert.Equal((HttpStatusCode)statusCode, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(clock.RequestedDelays);
    }

    [Fact]
    public async Task Write_NetworkError_DoesNotRetry()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.EnqueueException(new HttpRequestException("net"));

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateEventAsync(ConnectionId, "cal1", payload, default));

        Assert.Single(handler.Requests);
        Assert.Empty(clock.RequestedDelays);
    }

    [Fact]
    public async Task Write_Timeout_DoesNotRetry()
    {
        var (client, handler, _, clock) = CreateClient();
        handler.EnqueueTimeout();

        var payload = new Dictionary<string, object?> { ["subject"] = "X" };
        try
        {
            await client.CreateEventAsync(ConnectionId, "cal1", payload, default);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Single(handler.Requests);
        Assert.Empty(clock.RequestedDelays);
    }

    [Fact]
    public async Task Write_ParsesJsonFromNonSeekableResponseStream()
    {
        var (client, handler, _, _) = CreateClient();
        var json = """{"id":"new1","subject":"Created"}""";
        handler.Enqueue(request =>
        {
            var inner = new NonSeekableReadStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StreamContent(inner)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        });

        var payload = new Dictionary<string, object?> { ["subject"] = "New" };
        var result = await client.CreateEventAsync(ConnectionId, "cal1", payload, default);

        Assert.Equal("Created", result.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Write_EmptySuccessResponse_ReturnsUndefinedResult()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Created);

        var result = await client.CreateEventAsync(
            ConnectionId,
            "cal1",
            new Dictionary<string, object?> { ["subject"] = "New" },
            default);

        Assert.Equal(JsonValueKind.Undefined, result.ValueKind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallerCancellation_Read_PropagatesWithoutRetry()
    {
        var (client, handler, _, _) = CreateClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await client.GetMeAsync(ConnectionId, cts.Token);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Read_401ReplayCountsTowardThreeSendBudget()
    {
        var (client, handler, _, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Read_First401OnFinalAttempt_ReturnsUnauthorizedWithoutFourthSend()
    {
        var (client, handler, tokens, _) = CreateClient();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            client.GetMeAsync(ConnectionId, default));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, tokens.CallCount);
        Assert.False(tokens.LastForceRefresh);
    }

    [Fact]
    public async Task CallerCancellation_DuringSend_Propagates()
    {
        var (client, handler, _, _) = CreateClient();
        using var cts = new CancellationTokenSource();

        handler.Enqueue(request =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        try
        {
            await client.GetMeAsync(ConnectionId, cts.Token);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PerAttemptTimeout_PropagatesAfterExhaustion()
    {
        var (client, handler, _, _) = CreateClient();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();

        try
        {
            await client.GetMeAsync(ConnectionId, default);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SensitiveData_NotInLogsOrExceptions()
    {
        const string sensitiveToken = "sensitive-test-token-do-not-log";
        var (client, handler, tokens, _) = CreateClient(token: sensitiveToken);

        handler.EnqueueException(new HttpRequestException("net1"));
        handler.EnqueueException(new HttpRequestException("net2"));
        handler.EnqueueException(new HttpRequestException("net3"));

        try
        {
            await client.GetMeAsync(ConnectionId, default);
            Assert.Fail("Expected HttpRequestException");
        }
        catch (HttpRequestException ex)
        {
            Assert.DoesNotContain(sensitiveToken, ex.Message);
        }
    }

    private static (GraphCalendarClient Client, ScriptedHttpMessageHandler Handler, FakeOutlookAccessTokenProvider Tokens, StubTimeProvider Clock) CreateClient(
        ScriptedHttpMessageHandler? handler = null,
        string? token = null,
        StubTimeProvider? clock = null)
    {
        handler ??= new ScriptedHttpMessageHandler();
        var tokens = new FakeOutlookAccessTokenProvider { Token = token ?? "test-access-token" };
        clock ??= new StubTimeProvider();
        var factory = new StubHttpClientFactory(handler);
        var client = new GraphCalendarClient(factory, tokens, clock);
        return (client, handler, tokens, clock);
    }

    private static async Task<List<GraphPage>> CollectPages(IAsyncEnumerable<GraphPage> pages)
    {
        var result = new List<GraphPage>();
        await foreach (var page in pages)
            result.Add(page);
        return result;
    }
}
