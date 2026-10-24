using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

public sealed class TaskEditingAndTimingJourneyTests(ITestOutputHelper output)
{
    private const string Root = "/api/v1/calendar";

    [Fact]
    public async Task EditDateThenTimeTask_PreservesDetailsAndCountsOnlyExecutedSeconds()
    {
        var clock = new JourneyClock();
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("task-journey"))
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "task-journey-user");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var book = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/task-books", new { name = "Journey book" }));
        var initial = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "Read chapter 3",
            description = "Keep these requirements when editing the deadline.",
            priority = 2,
            estimatedDuration = "PT25M",
            minimumSegment = "PT5M",
            dtStart = clock.GetUtcNow().AddDays(1),
            plannedEnd = clock.GetUtcNow().AddDays(1).AddHours(1),
            due = clock.GetUtcNow().AddDays(2),
            taskBookId = book.GetProperty("id").GetGuid(),
            percentComplete = 25
        }));
        var taskId = initial.GetProperty("id").GetGuid();
        var newDue = clock.GetUtcNow().AddDays(3);

        await ReadSuccessAsync(await client.PutAsJsonAsync($"{Root}/tasks/{taskId}", new { due = newDue }));
        var edited = await ReadTaskAsync(client, taskId);
        foreach (var field in new[] { "title", "description", "priority", "estimatedDuration", "minimumSegment", "dtStart", "plannedEnd", "taskBookId", "percentComplete", "status", "isInbox" })
            Assert.Equal(initial.GetProperty(field).GetRawText(), edited.GetProperty(field).GetRawText());
        Assert.Equal(newDue, edited.GetProperty("due").GetDateTimeOffset());
        output.WriteLine("Only due changed. Persisted title/description/priority/planning/book/progress/status/inbox matched the original task.");

        // A one-hour plan must not turn into an hour of work merely by being present.
        await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/segments", new
        {
            startsAt = clock.GetUtcNow().AddHours(3),
            endsAt = clock.GetUtcNow().AddHours(4),
            status = "planned",
            source = "manual"
        }));
        await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/timer/start", new { }));
        clock.Advance(TimeSpan.FromSeconds(20));
        await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/timer/pause", new { }));
        clock.Advance(TimeSpan.FromHours(1));
        var paused = await ReadSuccessAsync(await client.GetAsync($"{Root}/tasks/{taskId}/timer"));
        Assert.Equal(20, paused.GetProperty("accumulatedSeconds").GetInt64());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("runningSegmentId").ValueKind);

        await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/timer/start", new { }));
        clock.Advance(TimeSpan.FromSeconds(30));
        await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/timer/stop", new { }));

        // New HTTP reads exercise storage, not just the mutation response or a UI counter.
        using var rereader = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var timer = await ReadSuccessAsync(await rereader.GetAsync($"{Root}/tasks/{taskId}/timer"));
        Assert.Equal(50, timer.GetProperty("accumulatedSeconds").GetInt64());
        Assert.Equal(JsonValueKind.Null, timer.GetProperty("runningSegmentId").ValueKind);
        var segments = await ReadSuccessAsync(await rereader.GetAsync($"{Root}/tasks/{taskId}/segments"));
        var executed = segments.EnumerateArray().Where(s => s.GetProperty("status").GetString() != "planned").ToList();
        Assert.Equal(2, executed.Count);
        Assert.All(executed, s => Assert.NotEqual(JsonValueKind.Null, s.GetProperty("endsAt").ValueKind));
        Assert.Equal(50d, executed.Sum(s => (s.GetProperty("endsAt").GetDateTimeOffset() - s.GetProperty("startsAt").GetDateTimeOffset()).TotalSeconds));
        var finalTask = await ReadTaskAsync(rereader, taskId);
        Assert.Equal(edited.GetProperty("description").GetString(), finalTask.GetProperty("description").GetString());
        Assert.Equal(2, finalTask.GetProperty("priority").GetInt32());
        Assert.Equal("IN-PROCESS", finalTask.GetProperty("status").GetString());
        output.WriteLine("Stored execution segments: 20s + 30s = 50s. The 1h pause and 1h planned segment were excluded. Stop timing did not mark the task completed.");
    }

    private static async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ReadTaskAsync(HttpClient client, Guid taskId)
    {
        var tasks = await ReadSuccessAsync(await client.GetAsync($"{Root}/tasks"));
        return tasks.EnumerateArray().Single(task => task.GetProperty("id").GetGuid() == taskId).Clone();
    }

    private sealed class JourneyClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
