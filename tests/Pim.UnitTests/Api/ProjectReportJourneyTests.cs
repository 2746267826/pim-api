using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

public sealed class ProjectReportJourneyTests(ITestOutputHelper output)
{
    private const string Root = "/api/v1/calendar";
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GenerateThenReopen_UsesSelectedProjectFactsAndKeepsAllMetrics()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("project-report"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "project-report-user");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var project = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/projects", new { name = "Reading plan" }));
        var otherProject = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/projects", new { name = "Other plan" }));
        var projectId = project.GetProperty("id").GetGuid();
        var otherProjectId = otherProject.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var first = Task(session.UserId, projectId, "Read", Start, 30, 0);
            var second = Task(session.UserId, projectId, "Practice", Start.AddHours(1), 45, 1);
            second.Status = "COMPLETED";
            second.CompletedAt = Start.AddHours(2);
            second.Due = Start.AddHours(2);
            var outsider = Task(session.UserId, otherProjectId, "Unrelated", Start, 300, 0);
            db.AddRange(first, second, outsider);
            db.AddRange(Segment(first, 10), Segment(second, 20), Segment(outsider, 180));
            var planned = Segment(first, 60);
            planned.Status = "planned";
            db.Add(planned);
            await db.SaveChangesAsync();
        }

        var generated = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/reports/generate", new
        {
            kind = "Project", date = "2026-10-24", projectId
        }));
        Assert.Equal(projectId, generated.GetProperty("projectId").GetGuid());
        using var metrics = JsonDocument.Parse(generated.GetProperty("metricsJson").GetString()!);
        var values = metrics.RootElement;
        Assert.Equal(14, values.EnumerateObject().Count());
        Assert.Equal(2, values.GetProperty("tasks").GetInt32());
        Assert.Equal(75, values.GetProperty("plannedEstimatedMinutes").GetInt32());
        Assert.Equal(75, values.GetProperty("plannedScheduledMinutes").GetInt32());
        Assert.Equal(30, values.GetProperty("actualExecutedMinutes").GetInt32());
        Assert.Equal(2, values.GetProperty("tasksCreated").GetInt32());
        Assert.Equal(1, values.GetProperty("tasksCompleted").GetInt32());
        Assert.Equal(0, values.GetProperty("tasksOverdue").GetInt32());
        Assert.Equal("本周期无数据", values.GetProperty("calendarOccupiedMinutes").GetString());
        Assert.Contains("Reading plan", generated.GetProperty("contentMarkdown").GetString());

        using var rereader = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var id = generated.GetProperty("id").GetGuid();
        var reopened = await ReadSuccessAsync(await rereader.GetAsync($"{Root}/reports/{id}"));
        Assert.Equal(generated.GetRawText(), reopened.GetRawText());
        var reports = await ReadSuccessAsync(await rereader.GetAsync($"{Root}/reports"));
        Assert.Equal(id, Assert.Single(reports.EnumerateArray()).GetProperty("id").GetGuid());

        output.WriteLine("Selected project: estimates 30 + 45 = 75 min; execution 10 + 20 = 30 min. Other project's 300/180 min and a 60 min unstarted plan excluded.");
        output.WriteLine("Both tasks created, final task completed exactly at project endpoint: created=2, completed=1, overdue=0. All 14 metrics persisted and identical on a new HTTP read.");
    }

    [Fact]
    public async Task UnscheduledProjectTask_CreatedAndCompletedAtFinalEndpoint_IsNotLost()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("project-report-point"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "project-report-point-user");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var projectId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new DomainProjectEntity { Id = projectId, UserId = session.UserId, Name = "Unscheduled" });
            db.Add(new TaskEntity
            {
                UserId = session.UserId, DomainProjectId = projectId, Uid = "unscheduled",
                Title = "Unscheduled task", CreatedAt = Start, CompletedAt = Start.AddMinutes(10), Status = "COMPLETED"
            });
            await db.SaveChangesAsync();
        }

        var report = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/reports/generate", new
        {
            kind = "Project", date = "2026-10-24", projectId
        }));
        using var metrics = JsonDocument.Parse(report.GetProperty("metricsJson").GetString()!);
        Assert.Equal(1, metrics.RootElement.GetProperty("tasksCreated").GetInt32());
        Assert.Equal(1, metrics.RootElement.GetProperty("tasksCompleted").GetInt32());
        Assert.Equal("本周期无数据", metrics.RootElement.GetProperty("actualExecutedMinutes").GetString());
        output.WriteLine("An unscheduled task completed at the latest project timestamp still counts as one completion; missing execution is not converted to zero.");
    }

    [Fact]
    public async Task CancelledExecution_StillMatchesTaskTimerActualDuration()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("project-report-cancelled"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "project-report-cancelled-user");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var projectId = Guid.NewGuid();
        Guid taskId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new DomainProjectEntity { Id = projectId, UserId = session.UserId, Name = "Cancelled work" });
            var task = Task(session.UserId, projectId, "Worked before cancellation", Start, 30, 0);
            task.Status = "CANCELLED";
            taskId = task.Id;
            db.Add(task);
            var segment = Segment(task, 10);
            segment.Status = "cancelled";
            db.Add(segment);
            await db.SaveChangesAsync();
        }

        var timer = await ReadSuccessAsync(await client.GetAsync($"{Root}/tasks/{taskId}/timer"));
        Assert.Equal(600, timer.GetProperty("accumulatedSeconds").GetInt64());
        var report = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/reports/generate", new
        {
            kind = "Project", date = "2026-10-24", projectId
        }));
        using var metrics = JsonDocument.Parse(report.GetProperty("metricsJson").GetString()!);
        Assert.Equal(10, metrics.RootElement.GetProperty("actualExecutedMinutes").GetInt32());
        output.WriteLine("Ten minutes already worked before cancellation remain 600 seconds in the task timer and 10 minutes in the report.");
    }

    [Fact]
    public async Task InvalidOrEmptyProject_FailsExplicitlyWithoutCreatingReports()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("project-report-errors"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "project-report-errors-user");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var empty = await ReadSuccessAsync(await client.PostAsJsonAsync($"{Root}/projects", new { name = "No tasks" }));
        var otherUserId = await AuthRegistrationTestHost.SeedUserAsync(factory, "other-report-user");
        var foreignProjectId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new DomainProjectEntity { Id = foreignProjectId, UserId = otherUserId, Name = "Private project" });
            await db.SaveChangesAsync();
        }

        foreach (var (projectId, expectedCode) in new (Guid?, int)[]
        {
            (null, 2046), (Guid.Empty, 2046), (Guid.NewGuid(), 2047),
            (foreignProjectId, 2047), (empty.GetProperty("id").GetGuid(), 2048)
        })
        {
            var response = await client.PostAsJsonAsync($"{Root}/reports/generate", new
            {
                kind = "Project", date = "2026-10-24", projectId
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(expectedCode, (await AuthRegistrationTestHost.ReadErrorAsync(response)).Code);
        }
        var reports = await ReadSuccessAsync(await client.GetAsync($"{Root}/reports"));
        Assert.Empty(reports.EnumerateArray());
        output.WriteLine("Missing selection=02046; unknown or another user's project=02047; empty project=02048. No failed attempt saved a report.");
    }

    private static TaskEntity Task(Guid userId, Guid projectId, string title, DateTimeOffset start, int minutes, int createdOffsetHours)
        => new()
        {
            UserId = userId, DomainProjectId = projectId, Title = title, Uid = Guid.NewGuid().ToString("N"),
            DtStart = start, PlannedEnd = start.AddMinutes(minutes), EstimatedDuration = TimeSpan.FromMinutes(minutes),
            CreatedAt = Start.AddHours(createdOffsetHours)
        };

    private static TaskExecutionSegmentEntity Segment(TaskEntity task, int minutes)
        => new()
        {
            TaskId = task.Id, UserId = task.UserId, StartsAt = task.DtStart!.Value,
            EndsAt = task.DtStart.Value.AddMinutes(minutes), Status = "completed"
        };

    private static async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
        return document.RootElement.GetProperty("data").Clone();
    }
}
