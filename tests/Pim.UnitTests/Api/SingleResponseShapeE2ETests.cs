using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 契约语义 §3（docs/client-contract-semantics.md）：同一路径+方法只返回一种 data 形状。
/// 移除 `GET /events` / `GET /tasks` 的「不带分页/过滤参数 → 裸数组」兼容分支后：
///   * 不带任何参数也必须返回 PagedResult（对象，含 items/totalCount/page/pageSize/totalPages）；
///   * 带 page/pageSize 仍然返回 PagedResult；
///   * 默认 page=1、pageSize=50。
/// </summary>
public sealed class SingleResponseShapeE2ETests
{
    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> ArrangeAsync(string username)
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("shape-" + username));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        // 造一点数据，确保「有数据」与「无数据」都走同一形状
        (await client.PostAsJsonAsync("/api/v1/calendar/tasks", new { title = "形状", priority = 0 })).EnsureSuccessStatusCode();
        return (factory, client);
    }

    private static async Task AssertPagedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, data.ValueKind);
        foreach (var field in new[] { "items", "totalCount", "page", "pageSize", "totalPages" })
            Assert.True(data.TryGetProperty(field, out _), $"data 缺少 {field}（形状不是 PagedResult）");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, data.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task GetTasks_WithoutParameters_ReturnsPagedResult()
    {
        var (factory, client) = await ArrangeAsync("shape-tasks-none");
        using var _ = factory;
        await AssertPagedAsync(await client.GetAsync("/api/v1/calendar/tasks"));
    }

    [Fact]
    public async Task GetTasks_OnlyInboxFilter_ReturnsPagedResult()
    {
        var (factory, client) = await ArrangeAsync("shape-tasks-inbox");
        using var _ = factory;
        await AssertPagedAsync(await client.GetAsync("/api/v1/calendar/tasks?inbox=false"));
    }

    [Fact]
    public async Task GetEvents_WithoutParameters_ReturnsPagedResult()
    {
        var (factory, client) = await ArrangeAsync("shape-events-none");
        using var _ = factory;
        await AssertPagedAsync(await client.GetAsync("/api/v1/calendar/events"));
    }

    /// <summary>
    /// 回归点（独立 review P1-6）：旧实现的「裸数组」分支**完全不看 start/end**，
    /// 所以日历视图最常见的「按时间范围拉取」以前返回数组，现在返回 PagedResult。
    /// 这是升级时最容易炸的一处，必须有测试钉住。
    /// </summary>
    [Fact]
    public async Task GetEvents_WithOnlyDateRange_ReturnsPagedResult()
    {
        var (factory, client) = await ArrangeAsync("shape-events-range");
        using var _ = factory;
        await AssertPagedAsync(await client.GetAsync("/api/v1/calendar/events?start=2026-10-01&end=2026-10-21"));
    }

    [Fact]
    public async Task GetTasks_DefaultsArePage1And50()
    {
        var (factory, client) = await ArrangeAsync("shape-defaults");
        using var _ = factory;
        var root = await AuthRegistrationTestHost.ReadJsonAsync(await client.GetAsync("/api/v1/calendar/tasks"));
        var data = root.GetProperty("data");
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        Assert.Equal(50, data.GetProperty("pageSize").GetInt32());
    }
}
