using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pim.Core.Modules;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Search;

public sealed class SearchEndpointTests
{
    [Fact]
    public async Task BlankQueryReturnsEmptySuccessWithoutCallingProviders()
    {
        var note = new StubProvider("note", _ => Response("note"));
        using var factory = CreateHost(note);
        using var client = await AuthedClientAsync(factory, "search-blank");

        using var response = await client.GetAsync("/api/v1/search?q=%20%20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, note.Calls);
    }

    [Fact]
    public async Task NoMatchesReturnsEmptyGroupsWithStableEnvelope()
    {
        var note = new StubProvider("note", _ => Response("note"));
        var task = new StubProvider("task", _ => Response("task"));
        var @event = new StubProvider("event", _ => Response("event"));
        using var factory = CreateHost(note, task, @event);
        using var client = await AuthedClientAsync(factory, "search-no-match");

        using var response = await client.GetAsync("/api/v1/search?q=%E4%B8%8D%E5%AD%98%E5%9C%A8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, data.GetProperty("totalPages").GetInt32());
        Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal(3, data.GetProperty("groups").GetArrayLength());
        Assert.All(data.GetProperty("groups").EnumerateArray(), group =>
        {
            Assert.Equal(0, group.GetProperty("totalCount").GetInt32());
            Assert.Empty(group.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, group.GetProperty("failure").ValueKind);
        });
        Assert.Equal(1, note.Calls);
        Assert.Equal(1, task.Calls);
        Assert.Equal(1, @event.Calls);
    }

    [Fact]
    public async Task AggregatesThreeDomainsWithGroupedHitsAndLegacyFields()
    {
        var note = new StubProvider("note", _ => Response("note",
            Hit("note", Guid.NewGuid().ToString(), "研究方法", 60)));
        var task = new StubProvider("task", _ => Response("task",
            Hit("task", Guid.NewGuid().ToString(), "研究方法", 90)));
        var @event = new StubProvider("event", _ => Response("event",
            Hit("event", Guid.NewGuid().ToString(), "研究方法", 75)));
        using var factory = CreateHost(note, task, @event);
        using var client = await AuthedClientAsync(factory, "search-cross-domain");

        using var response = await client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString("研究方法")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        var items = data.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "task", "event", "note" }, items.Select(x => x.GetProperty("domain").GetString()).ToArray());
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("moduleName").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("type").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("snippet").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("url").GetString()));
            Assert.Equal("server", item.GetProperty("sourceState").GetString());
            Assert.NotEmpty(item.GetProperty("matchedFields").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sortKey").GetString()));
        });
        var groups = data.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(new[] { "note", "task", "event" }, groups.Select(x => x.GetProperty("domain").GetString()).ToArray());
        Assert.All(groups, group =>
        {
            Assert.Equal(1, group.GetProperty("totalCount").GetInt32());
            Assert.Single(group.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, group.GetProperty("failure").ValueKind);
        });
    }

    [Fact]
    public async Task ProviderFailureIsIsolatedAndRetryRecoversOnlyThatDomain()
    {
        var failNote = true;
        var note = new StubProvider("note", _ => failNote
            ? throw new InvalidOperationException("synthetic note storage failure")
            : Response("note", Hit("note", Guid.NewGuid().ToString(), "研究方法", 60)));
        var task = new StubProvider("task", _ => Response("task",
            Hit("task", Guid.NewGuid().ToString(), "研究方法", 90)));
        var @event = new StubProvider("event", _ => Response("event",
            Hit("event", Guid.NewGuid().ToString(), "研究方法", 75)));
        using var factory = CreateHost(note, task, @event);
        using var client = await AuthedClientAsync(factory, "search-partial-failure");
        var url = $"/api/v1/search?q={Uri.EscapeDataString("研究方法")}";

        using var failed = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var correlation = failed.Headers.GetValues("X-Correlation-Id").Single();
        var failedData = await ReadDataAsync(failed);
        Assert.Equal(2, failedData.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, failedData.GetProperty("items").GetArrayLength());
        var noteGroup = failedData.GetProperty("groups").EnumerateArray()
            .Single(x => x.GetProperty("domain").GetString() == "note");
        var failure = noteGroup.GetProperty("failure");
        Assert.Equal("50001", failure.GetProperty("code").GetString());
        Assert.True(failure.GetProperty("retryable").GetBoolean());
        Assert.Equal(correlation, failure.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("synthetic note storage failure", failure.GetProperty("message").GetString());

        failNote = false;
        using var recovered = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var recoveredData = await ReadDataAsync(recovered);
        Assert.Equal(3, recoveredData.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, recoveredData.GetProperty("items").GetArrayLength());
        var recoveredNote = recoveredData.GetProperty("groups").EnumerateArray()
            .Single(x => x.GetProperty("domain").GetString() == "note");
        Assert.Equal(JsonValueKind.Null, recoveredNote.GetProperty("failure").ValueKind);
    }

    [Fact]
    public async Task UnifiedOrderingPaginatesWithStableSortKeys()
    {
        var noteId = Guid.NewGuid().ToString();
        var taskId = Guid.NewGuid().ToString();
        var eventId = Guid.NewGuid().ToString();
        var note = new StubProvider("note", _ => Response("note",
            Hit("note", noteId, "低分", 10, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))));
        var task = new StubProvider("task", _ => Response("task",
            Hit("task", taskId, "高分", 30, DateTimeOffset.Parse("2026-01-02T00:00:00Z"))));
        var @event = new StubProvider("event", _ => Response("event",
            Hit("event", eventId, "中分", 20, DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
                recurrenceId: "2026-01-03T09:00:00Z")));
        using var factory = CreateHost(note, task, @event);
        using var client = await AuthedClientAsync(factory, "search-pagination");

        using var relevance = await client.GetAsync(
            $"/api/v1/search?q=%E7%A0%94%E7%A9%B6&page=2&pageSize=2");
        var relevanceData = await ReadDataAsync(relevance);
        Assert.Equal(3, relevanceData.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, relevanceData.GetProperty("totalPages").GetInt32());
        var remaining = Assert.Single(relevanceData.GetProperty("items").EnumerateArray());
        Assert.Equal(noteId, remaining.GetProperty("id").GetString());
        Assert.Contains("note", remaining.GetProperty("sortKey").GetString()!);

        using var recent = await client.GetAsync(
            $"/api/v1/search?q=%E7%A0%94%E7%A9%B6&page=1&pageSize=10&sort=recent_edited");
        var recentData = await ReadDataAsync(recent);
        Assert.Equal(new[] { eventId, taskId, noteId },
            recentData.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetString()).ToArray());

        using var created = await client.GetAsync(
            $"/api/v1/search?q=%E7%A0%94%E7%A9%B6&page=1&pageSize=10&sort=recent_created");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdData = await ReadDataAsync(created);
        Assert.Equal(new[] { eventId, taskId, noteId },
            createdData.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task ResultsBeyondOneThousandRemainPageableWithoutSilentTruncation()
    {
        var hits = Enumerable.Range(0, 1_005)
            .Select(_ => Hit("note", Guid.NewGuid().ToString("D"), "研究方法", 50))
            .ToArray();
        var note = new StubProvider("note", _ => Response("note", hits));
        using var factory = CreateHost(note);
        using var client = await AuthedClientAsync(factory, "search-large-result");

        using var response = await client.GetAsync(
            "/api/v1/search?q=%E7%A0%94%E7%A9%B6&scope=note&page=11&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(1_005, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(11, data.GetProperty("totalPages").GetInt32());
        Assert.Equal(5, data.GetProperty("items").GetArrayLength());
        var group = Assert.Single(data.GetProperty("groups").EnumerateArray());
        Assert.Equal(1_005, group.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, group.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task MatchModeAndTermsAreForwardedToTheSelectedDomain()
    {
        var note = new StubProvider("note", _ => Response("note"));
        var task = new StubProvider("task", _ => Response("task"));
        using var factory = CreateHost(note, task);
        using var client = await AuthedClientAsync(factory, "search-match");

        using var response = await client.GetAsync(
            "/api/v1/search?q=alpha%20%20beta%20alpha&match=any&scope=note");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SearchMatchMode.Any, note.LastQuery!.Match);
        Assert.Equal(new[] { "alpha", "beta" }, note.LastQuery.Terms);
        Assert.Equal(1, note.Calls);
        Assert.Equal(0, task.Calls);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("limit=0", "limit")]
    [InlineData("sort=unknown", "sort")]
    [InlineData("match=unknown", "match")]
    [InlineData("scope=file", "scope")]
    [InlineData("scope=unknown", "scope")]
    public async Task InvalidQueryRulesReturn400WithStableCode(string query, string _)
    {
        using var factory = CreateHost(new StubProvider("note", _ => Response("note")));
        using var client = await AuthedClientAsync(factory, "search-validation-" + Guid.NewGuid().ToString("N"));

        using var response = await client.GetAsync($"/api/v1/search?q=x&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40001, error.Code);
    }

    [Fact]
    public async Task OverlongAndTermHeavyQueriesReturn400()
    {
        using var factory = CreateHost(new StubProvider("note", _ => Response("note")));
        using var client = await AuthedClientAsync(factory, "search-length");

        using var tooLong = await client.GetAsync(
            $"/api/v1/search?q={Uri.EscapeDataString(new string('长', 201))}");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(40001, (await AuthRegistrationTestHost.ReadErrorAsync(tooLong)).Code);

        var terms = string.Join(' ', Enumerable.Range(0, 11).Select(i => $"t{i}"));
        using var tooManyTerms = await client.GetAsync(
            $"/api/v1/search?q={Uri.EscapeDataString(terms)}");
        Assert.Equal(HttpStatusCode.BadRequest, tooManyTerms.StatusCode);
        Assert.Equal(40001, (await AuthRegistrationTestHost.ReadErrorAsync(tooManyTerms)).Code);
    }

    [Fact]
    public async Task LegacyTypeAndLimitParametersKeepWorkingAndDoNotFanOut()
    {
        var task = new StubProvider("task", _ => Response("task",
            Hit("task", Guid.NewGuid().ToString(), "任务一", 10),
            Hit("task", Guid.NewGuid().ToString(), "任务二", 5)));
        var @event = new StubProvider("event", _ => Response("event",
            Hit("event", Guid.NewGuid().ToString(), "日程一", 10)));
        using var factory = CreateHost(task, @event);
        using var client = await AuthedClientAsync(factory, "search-legacy");

        using var response = await client.GetAsync("/api/v1/search?q=%E4%BB%BB%E5%8A%A1&type=task&limit=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, data.GetProperty("totalPages").GetInt32());
        Assert.Equal("task", Assert.Single(data.GetProperty("items").EnumerateArray())
            .GetProperty("type").GetString());
        Assert.Equal(0, @event.Calls);
        Assert.Equal(1, task.Calls);

        using var capped = await client.GetAsync("/api/v1/search?q=%E4%BB%BB%E5%8A%A1&type=task&limit=500");
        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);
        Assert.Equal(100, (await ReadDataAsync(capped)).GetProperty("pageSize").GetInt32());

        using var calendar = await client.GetAsync(
            "/api/v1/search?q=%E4%BB%BB%E5%8A%A1&type=calendar&limit=20");
        Assert.Equal(HttpStatusCode.OK, calendar.StatusCode);
        var calendarData = await ReadDataAsync(calendar);
        var calendarItems = calendarData.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, calendarItems.Length);
        Assert.Contains(calendarItems, x => x.GetProperty("domain").GetString() == "task");
        Assert.Contains(calendarItems, x => x.GetProperty("domain").GetString() == "event");
        Assert.DoesNotContain(calendarItems, x => x.GetProperty("domain").GetString() == "note");
    }

    [Fact]
    public async Task LegacyFileTypeStaysSuccessfulAndEmpty()
    {
        var note = new StubProvider("note", _ => Response("note",
            Hit("note", Guid.NewGuid().ToString(), "文件类", 1)));
        using var factory = CreateHost(note);
        using var client = await AuthedClientAsync(factory, "search-legacy-file");

        using var response = await client.GetAsync("/api/v1/search?q=x&type=file&limit=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, note.Calls);
    }

    [Fact]
    public async Task AnonymousSearchIsUnauthorized()
    {
        using var factory = CreateHost(new StubProvider("note", _ => Response("note")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/search?q=x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProviderItemsWithoutExplicitDomainAreNormalizedIntoTheirGroup()
    {
        var eventId = Guid.NewGuid().ToString("D");
        var @event = new StubProvider("event", _ => Response("event", new SearchResult(
            "calendar", "event", eventId, "未填领域", "摘要", $"/calendar?event={eventId}",
            Score: 10, MatchedFields: new[] { "标题" })));
        using var factory = CreateHost(@event);
        using var client = await AuthedClientAsync(factory, "search-normalize");

        using var response = await client.GetAsync("/api/v1/search?q=x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("event", item.GetProperty("domain").GetString());
        Assert.Equal("server", item.GetProperty("sourceState").GetString());
        var eventGroup = data.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("domain").GetString() == "event");
        Assert.Single(eventGroup.GetProperty("items").EnumerateArray());
    }

    private static WebApplicationFactory<Program> CreateHost(params ISearchProvider[] providers)
        => AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("search-endpoint"))
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ISearchProvider>();
                    foreach (var provider in providers) services.AddSingleton(provider);
                });
            });

    private static async Task<HttpClient> AuthedClientAsync(
        WebApplicationFactory<Program> factory, string username)
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            factory, username + "-" + Guid.NewGuid().ToString("N"));
        return AuthRegistrationTestHost.Authed(factory, session.AccessToken);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
        => (await AuthRegistrationTestHost.ReadJsonAsync(response)).GetProperty("data");

    private static SearchProviderResponse Response(string domain, params SearchResult[] items)
        => new(domain, items.Length, items);

    private static SearchResult Hit(
        string domain,
        string id,
        string title,
        double score,
        DateTimeOffset? updated = null,
        string? recurrenceId = null)
    {
        var at = updated ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return new SearchResult(
            domain == "note" ? "notes" : "calendar",
            domain,
            id,
            title,
            "摘要 " + title,
            domain == "note" ? $"/native-notes?note={id}" : domain == "task"
                ? $"/tasks?task={id}"
                : $"/calendar?event={id}",
            Domain: domain,
            Score: score,
            MatchedFields: new[] { "title" },
            FieldLocations: new[] { new SearchFieldLocation("title", "标题") },
            EventOccurrence: domain == "event"
                ? new SearchEventOccurrence(Guid.Parse(id), recurrenceId, null, at)
                : null,
            SourceState: "server",
            UpdatedAt: at,
            CreatedAt: at.AddDays(-1),
            SortKey: $"{domain}|{id}|{recurrenceId ?? string.Empty}");
    }

    private sealed class StubProvider(
        string domain,
        Func<SearchProviderQuery, SearchProviderResponse> respond) : ISearchProvider
    {
        public string Domain => domain;
        public int Calls { get; private set; }
        public SearchProviderQuery? LastQuery { get; private set; }

        public Task<SearchProviderResponse> SearchAsync(SearchProviderQuery query, CancellationToken ct)
        {
            Calls++;
            LastQuery = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(query));
        }
    }
}
