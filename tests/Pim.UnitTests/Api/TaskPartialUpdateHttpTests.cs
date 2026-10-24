using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// api#465：任务更新的三态语义（HTTP 真实路径）。
/// 字段缺失 = 保留原值；显式 null = 清空可清空字段；显式 0 = 写 0；
/// title 显式 null/空白 → 400（02061），不是 500；非法合并日期 → 400 且不落库；
/// 他人任务不可见也不可改。
/// </summary>
public sealed class TaskPartialUpdateHttpTests : IClassFixture<TaskPartialUpdateHttpTests.SharedHostFixture>
{
    private const string Root = "/api/v1/calendar";

    /// <summary>
    /// 全类共享一个宿主 + 一个已登录用户：每个用例建独立任务保证隔离，
    /// 避免 ~25 个 WebApplicationFactory 各自冷启动（每例 10s+）。
    /// IAsyncLifetime 避免 xUnit 构造器里同步等待异步初始化造成上下文死锁。
    /// </summary>
    public sealed class SharedHostFixture : IAsyncLifetime
    {
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;

        public Task InitializeAsync()
        {
            Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("task-partial-update"));
            return SeedAsync();
        }

        private async Task SeedAsync()
        {
            var session = await AuthRegistrationTestHost.SeedAndLoginAsync(Factory, "task-partial-user");
            Client = AuthRegistrationTestHost.Authed(Factory, session.AccessToken);
        }

        public Task DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            return Task.CompletedTask;
        }
    }

    private readonly SharedHostFixture _host;

    public TaskPartialUpdateHttpTests(SharedHostFixture host) => _host = host;

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ReadTaskAsync(HttpClient client, Guid taskId)
    {
        var tasks = await ReadDataAsync(await client.GetAsync($"{Root}/tasks"));
        return tasks.EnumerateArray().Single(task => task.GetProperty("id").GetGuid() == taskId).Clone();
    }

    private static async Task<(Guid Id, JsonElement Snapshot)> CreateTaskAsync(HttpClient client, Guid? calendarId = null, Guid? taskBookId = null)
    {
        var created = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "Original title",
            description = "Original description",
            priority = 2,
            estimatedDuration = "PT30M",
            minimumSegment = "PT10M",
            due = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero),
            dtStart = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero),
            plannedEnd = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
            percentComplete = 25,
            calendarId,
            taskBookId,
        }));
        var taskId = created.GetProperty("id").GetGuid();
        return (taskId, await ReadTaskAsync(client, taskId));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutRawAsync(
        HttpClient client, Guid taskId, string json)
    {
        var response = await client.PutAsync(
            $"{Root}/tasks/{taskId}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task DueOnlyUpdate_PreservesEveryOtherField()
    {
        var client = _host.Client;
        var calendar = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/calendars", new { name = $"Preserved-{Guid.NewGuid():N}", kind = "task" }));
        var book = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/task-books", new { name = $"Preserved-{Guid.NewGuid():N}" }));
        var (taskId, snapshot) = await CreateTaskAsync(client, calendar.GetProperty("id").GetGuid(), book.GetProperty("id").GetGuid());

        var (status, _) = await PutRawAsync(client, taskId, """{"due":"2026-11-05T08:00:00Z"}""");
        Assert.Equal(HttpStatusCode.OK, status);

        var edited = await ReadTaskAsync(client, taskId);
        foreach (var field in new[] { "title", "description", "priority", "estimatedDuration", "minimumSegment", "dtStart", "plannedEnd", "percentComplete", "status", "isInbox", "calendarId", "taskBookId" })
            Assert.Equal(snapshot.GetProperty(field).GetRawText(), edited.GetProperty(field).GetRawText());
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), edited.GetProperty("due").GetDateTimeOffset());
    }

    [Fact]
    public async Task ExplicitNull_ClearsClearableFields()
    {
        var client = _host.Client;
        var (taskId, _) = await CreateTaskAsync(client);

        var (status, _) = await PutRawAsync(
            client, taskId,
            """{"description":null,"due":null,"dtStart":null,"plannedEnd":null,"estimatedDuration":null,"minimumSegment":null}""");
        Assert.Equal(HttpStatusCode.OK, status);

        var edited = await ReadTaskAsync(client, taskId);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("due").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("dtStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("plannedEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("estimatedDuration").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("minimumSegment").ValueKind);
        Assert.Equal("Original title", edited.GetProperty("title").GetString());
        Assert.Equal(2, edited.GetProperty("priority").GetInt32());
    }

    [Theory]
    [InlineData("""{"title":null}""")]
    [InlineData("""{"title":"   "}""")]
    public async Task ExplicitNullOrBlankTitle_Returns400_AndChangesNothing(string json)
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        var (status, body) = await PutRawAsync(client, taskId, json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02061, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }

    [Fact]
    public async Task ExplicitNullPriority_Returns400_AndChangesNothing()
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        var (status, body) = await PutRawAsync(client, taskId, """{"priority":null}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02061, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }

    [Fact]
    public async Task ExplicitZero_WritesZeroForPriorityAndPercentComplete()
    {
        var client = _host.Client;
        var (taskId, _) = await CreateTaskAsync(client);

        var (status, _) = await PutRawAsync(client, taskId, """{"priority":0,"percentComplete":0}""");
        Assert.Equal(HttpStatusCode.OK, status);

        var edited = await ReadTaskAsync(client, taskId);
        Assert.Equal(0, edited.GetProperty("priority").GetInt32());
        Assert.Equal(0, edited.GetProperty("percentComplete").GetInt32());
        Assert.Equal("Original title", edited.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MergedDateRangeInvalid_Returns400_AndChangesNothing()
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        // dtStart 未传保留 11-01 09:00；plannedEnd 显式改成 11-01 08:00（早于保留值）。
        var (status, body) = await PutRawAsync(client, taskId, """{"plannedEnd":"2026-11-01T08:00:00Z"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02010, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }

    [Fact]
    public async Task OtherUsersTask_Returns02004_AndDoesNotModify()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("task-partial-cross"));
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "task-owner");
        var attacker = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "task-attacker");
        using var ownerClient = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        using var attackerClient = AuthRegistrationTestHost.Authed(factory, attacker.AccessToken);
        var (taskId, snapshot) = await CreateTaskAsync(ownerClient);

        var (status, body) = await PutRawAsync(attackerClient, taskId, """{"due":"2026-12-01T00:00:00Z"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02004, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(ownerClient, taskId)).GetRawText());
    }

    [Fact]
    public async Task ExplicitNullTaskBookId_ClearsBook()
    {
        var client = _host.Client;
        var book = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/task-books", new { name = $"Book-{Guid.NewGuid():N}" }));
        var created = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "Booked",
            priority = 1,
            taskBookId = book.GetProperty("id").GetGuid(),
        }));
        var taskId = created.GetProperty("id").GetGuid();

        var (status, _) = await PutRawAsync(client, taskId, """{"taskBookId":null}""");
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(JsonValueKind.Null, (await ReadTaskAsync(client, taskId)).GetProperty("taskBookId").ValueKind);
    }

    [Theory]
    [InlineData("""{"due":"not-a-date"}""")]
    [InlineData("""{"dtStart":123}""")]
    [InlineData("""{"plannedEnd":true}""")]
    [InlineData("""{"priority":"high"}""")]
    [InlineData("""{"priority":2.5}""")]
    [InlineData("""{"percentComplete":"twenty-five"}""")]
    [InlineData("""{"calendarId":"not-a-guid"}""")]
    [InlineData("""{"taskBookId":42}""")]
    [InlineData("""{"title":123}""")]
    [InlineData("""{"description":{}}""")]
    public async Task MalformedFieldType_Returns400_AndChangesNothing(string json)
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        var (status, _) = await PutRawAsync(client, taskId, json);
        Assert.Equal(HttpStatusCode.BadRequest, status);

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }

    [Fact]
    public async Task ExplicitNullPercentComplete_Returns400_AndChangesNothing()
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        var (status, body) = await PutRawAsync(client, taskId, """{"percentComplete":null}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02061, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }

    [Fact]
    public async Task ExplicitNullCalendarId_ClearsCalendar()
    {
        var client = _host.Client;
        var calendar = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/calendars", new { name = $"Update target-{Guid.NewGuid():N}", kind = "task" }));
        var created = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "Calendar bound",
            priority = 1,
            calendarId = calendar.GetProperty("id").GetGuid(),
        }));
        var taskId = created.GetProperty("id").GetGuid();

        var (status, _) = await PutRawAsync(client, taskId, """{"calendarId":null}""");
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(JsonValueKind.Null, (await ReadTaskAsync(client, taskId)).GetProperty("calendarId").ValueKind);
    }

    [Fact]
    public async Task ExplicitCalendarId_MoveToAnotherCalendar()
    {
        var client = _host.Client;
        var first = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/calendars", new { name = $"First-{Guid.NewGuid():N}", kind = "task" }));
        var second = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/calendars", new { name = $"Second-{Guid.NewGuid():N}", kind = "task" }));
        var created = await ReadDataAsync(await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "Movable",
            priority = 1,
            calendarId = first.GetProperty("id").GetGuid(),
        }));
        var taskId = created.GetProperty("id").GetGuid();

        var (status, _) = await PutRawAsync(client, taskId, $"{{\"calendarId\":\"{second.GetProperty("id").GetGuid()}\"}}");
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(second.GetProperty("id").GetGuid(), (await ReadTaskAsync(client, taskId)).GetProperty("calendarId").GetGuid());
    }

    [Fact]
    public async Task NullTitleWithOtherFieldInSameRequest_IsAtomicallyRejected()
    {
        var client = _host.Client;
        var (taskId, snapshot) = await CreateTaskAsync(client);

        var (status, body) = await PutRawAsync(client, taskId, """{"title":null,"description":"legit edit"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(02061, body.GetProperty("code").GetInt32());

        Assert.Equal(snapshot.GetRawText(), (await ReadTaskAsync(client, taskId)).GetRawText());
    }
}
