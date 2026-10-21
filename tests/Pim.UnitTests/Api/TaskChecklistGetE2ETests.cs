using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// api#443：任务清单只有增/改/删，**没有读取路由**。
/// 客户端（Web / Android / Windows / MCP）写进去的清单读不回来，MCP 的 get_task_checklist
/// 只能返回 "checklist endpoint not available"。
///
/// 本组用例锁定新增的 <c>GET /api/v1/calendar/tasks/{id}/checklist</c> 的行为：
///   1. 按 SortOrder 升序返回；
///   2. 空清单返回空数组（不是 404）；
///   3. 已软删除的条目不返回；
///   4. 任务不存在 → 02004（与其它任务子资源一致）；
///   5. 别人的任务 → 同样 02004（不泄露存在性）。
/// </summary>
public sealed class TaskChecklistGetE2ETests
{
    private const string Root = "/api/v1/calendar";

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response)
    {
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        return root.GetProperty("code").GetInt32();
    }

    /// <summary>建一个任务本 + 一个任务，返回任务 id（不建清单项）。</summary>
    private static async Task<Guid> CreateTaskAsync(HttpClient client)
    {
        var bookResp = await client.PostAsJsonAsync($"{Root}/task-books", new { name = "清单本" });
        bookResp.EnsureSuccessStatusCode();
        var book = (await AuthRegistrationTestHost.ReadJsonAsync(bookResp)).GetProperty("data");
        var createTask = await client.PostAsJsonAsync($"{Root}/tasks", new
        {
            title = "带清单的任务",
            priority = 0,
            taskBookId = book.GetProperty("id").GetString()
        });
        createTask.EnsureSuccessStatusCode();
        return (await AuthRegistrationTestHost.ReadJsonAsync(createTask))
            .GetProperty("data").GetProperty("id").GetGuid();
    }

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, Guid TaskId)> ArrangeAsync(
        params (string Title, int? SortOrder)[] items)
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("checklist-get"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "checklist-user");
        var client = Authed(factory, session.AccessToken);
        var taskId = await CreateTaskAsync(client);

        foreach (var (title, sortOrder) in items)
        {
            var body = sortOrder is null ? (object)new { title } : new { title, sortOrder };
            var resp = await client.PostAsJsonAsync($"{Root}/tasks/{taskId}/checklist", body);
            resp.EnsureSuccessStatusCode();
        }

        return (factory, client, taskId);
    }

    private static async Task<string?[]> TitlesAsync(HttpClient client, Guid taskId)
    {
        var response = await client.GetAsync($"{Root}/tasks/{taskId}/checklist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()).ToArray();
    }

    [Fact]
    public async Task GetChecklist_ReturnsItemsOrderedBySortOrder()
    {
        var (factory, client, taskId) = await ArrangeAsync(("第二项", 2), ("第一项", 0), ("第三项", 1));
        using var factoryScope = factory;

        var response = await client.GetAsync($"{Root}/tasks/{taskId}/checklist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var items = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(new[] { "第一项", "第三项", "第二项" },
            items.Select(i => i.GetProperty("title").GetString()).ToArray());
        Assert.All(items, i => Assert.False(i.GetProperty("isDone").GetBoolean()));
        Assert.All(items, i => Assert.Equal(taskId, i.GetProperty("taskId").GetGuid()));
        Assert.All(items, i => Assert.NotEqual(Guid.Empty, i.GetProperty("id").GetGuid()));
        Assert.All(items, i => Assert.True(i.TryGetProperty("sortOrder", out _)));
    }

    [Fact]
    public async Task GetChecklist_EmptyTask_ReturnsEmptyArray()
    {
        var (factory, client, taskId) = await ArrangeAsync();
        using var factoryScope = factory;

        var response = await client.GetAsync($"{Root}/tasks/{taskId}/checklist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal(0, root.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task GetChecklist_ExcludesSoftDeletedItems()
    {
        var (factory, client, taskId) = await ArrangeAsync(("保留", 0), ("删除", 1));
        using var factoryScope = factory;

        var list = await AuthRegistrationTestHost.ReadJsonAsync(await client.GetAsync($"{Root}/tasks/{taskId}/checklist"));
        var toDelete = list.GetProperty("data").EnumerateArray()
            .First(i => i.GetProperty("title").GetString() == "删除").GetProperty("id").GetGuid();
        (await client.DeleteAsync($"{Root}/tasks/{taskId}/checklist/{toDelete}")).EnsureSuccessStatusCode();

        Assert.Equal(new string?[] { "保留" }, await TitlesAsync(client, taskId));
    }

    [Fact]
    public async Task GetChecklist_SameSortOrderAndCreatedAt_BreaksTieById()
    {
        // 批量导入常见「同 SortOrder + 同 CreatedAt（同一事务里写入）」：三者都相同时只剩 Id 兜底。
        // 这里直接落库同时构造出这三个条件，并**按 Id 逆序插入**——
        // 若把 .ThenBy(i => i.Id) 去掉、顺序退化为按插入顺序（InMemory 稳定排序即如此），本用例必失败。
        // Id 取低值且首段为小整数，使 .NET Guid 比较、字符串比较、PostgreSQL uuid 字节序三者一致。
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("checklist-get-tie"));
        using var factoryScope = factory;
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "checklist-tie");
        var client = Authed(factory, session.AccessToken);
        var taskId = await CreateTaskAsync(client);

        var ids = new[]
        {
            Guid.Parse("00000001-0000-0000-0000-000000000001"),
            Guid.Parse("00000002-0000-0000-0000-000000000002"),
            Guid.Parse("00000003-0000-0000-0000-000000000003")
        };
        var createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            for (var i = ids.Length - 1; i >= 0; i--)
            {
                db.Set<TaskChecklistItemEntity>().Add(new TaskChecklistItemEntity
                {
                    Id = ids[i],
                    TaskId = taskId,
                    UserId = session.UserId,
                    Title = $"第{i + 1}项",
                    SortOrder = 7,
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"{Root}/tasks/{taskId}/checklist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = (await AuthRegistrationTestHost.ReadJsonAsync(response))
            .GetProperty("data").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal(ids, returned);
    }

    [Fact]
    public async Task GetChecklist_OnlyReturnsItemsOfTheRequestedTask()
    {
        // 同一用户的另一个任务：`i.TaskId == task.Id` 过滤必须生效（同 UserId 不足以区分）。
        var (factory, client, taskA) = await ArrangeAsync(("A-1", 0), ("A-2", 1));
        using var factoryScope = factory;
        var taskB = await CreateTaskAsync(client);
        (await client.PostAsJsonAsync($"{Root}/tasks/{taskB}/checklist", new { title = "B-1" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(new string?[] { "A-1", "A-2" }, await TitlesAsync(client, taskA));
        Assert.Equal(new string?[] { "B-1" }, await TitlesAsync(client, taskB));
    }

    [Fact]
    public async Task GetChecklist_WithoutToken_Returns401()
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("checklist-get-401"));
        using var factoryScope = factory;
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{Root}/tasks/{Guid.NewGuid()}/checklist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetChecklist_UnknownTask_Returns02004()
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("checklist-get-404"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "checklist-404");
        using var factoryScope = factory;
        var client = Authed(factory, session.AccessToken);

        var response = await client.GetAsync($"{Root}/tasks/{Guid.NewGuid()}/checklist");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2004, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task GetChecklist_OtherUsersTask_IsNotVisible()
    {
        var (factory, ownerClient, taskId) = await ArrangeAsync(("别人的", 0));
        using var factoryScope = factory;
        var intruder = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "checklist-intruder");
        var intruderClient = Authed(factory, intruder.AccessToken);

        var response = await intruderClient.GetAsync($"{Root}/tasks/{taskId}/checklist");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2004, await ReadCodeAsync(response));
        Assert.Equal(new string?[] { "别人的" }, await TitlesAsync(ownerClient, taskId));
    }
}
