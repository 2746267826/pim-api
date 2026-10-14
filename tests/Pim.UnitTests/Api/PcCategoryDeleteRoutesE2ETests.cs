using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #418：<c>/api/v1/pc</c> 分类路由遗留。
///
/// 1. 旧的双注册（<c>writeGroup.MapDelete("/categories/{id}")</c> 与
///    <c>catWrite.MapDelete("/{id:guid}")</c>）里，带约束的那条优先级更高，
///    扁平规则的删除路径**不可达**；本文件用「树形分类能被删掉、扁平规则删不掉」
///    的反向断言证明修复后路由归属正确。
/// 2. 扁平规则新资源 <c>/app-category-rules</c> 补齐配套 DELETE。
/// </summary>
public sealed class PcCategoryDeleteRoutesE2ETests
{
    [Fact]
    public async Task AppCategoryRules_Delete_RemovesTheFlatRule()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var created = await PostJsonAsync(client, "/api/v1/pc/app-category-rules", new
        {
            appPattern = "delete-me.exe",
            categoryName = "开发",
            color = "#2563eb",
            priority = 5
        });
        var id = created.GetProperty("data").GetProperty("id").GetString()!;

        var response = await client.DeleteAsync($"/api/v1/pc/app-category-rules/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 往返：列表里读不到，库里也没有
        var list = await GetJsonAsync(client, "/api/v1/pc/categories");
        Assert.DoesNotContain(
            list.GetProperty("data").EnumerateArray(),
            rule => rule.GetProperty("appPattern").GetString() == "delete-me.exe");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.Set<AppCategoryEntity>().AnyAsync(x => x.AppPattern == "delete-me.exe"));
    }

    [Fact]
    public async Task AppCategoryRules_Delete_UnknownId_Returns404()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var response = await client.DeleteAsync($"/api/v1/pc/app-category-rules/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // 必须由端点自己答（"不存在或为内置项"），而不是全局兜底的「接口不存在: /path」——
        // 后者说明路由压根没注册，正是本条工单要修的状态。
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("不存在或为内置项", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AppCategoryRules_Delete_BuiltinRule_Returns404AndKeepsTheRow()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var builtinId = Guid.NewGuid();
        await SeedAsync(factory, db => db.Set<AppCategoryEntity>().Add(new AppCategoryEntity
        {
            Id = builtinId,
            AppPattern = "builtin.exe",
            CategoryName = "内置",
            Color = "#111111",
            Priority = 100,
            IsBuiltin = true
        }));

        var response = await client.DeleteAsync($"/api/v1/pc/app-category-rules/{builtinId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("不存在或为内置项", document.RootElement.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.True(await db.Set<AppCategoryEntity>().AnyAsync(x => x.Id == builtinId));
    }

    [Fact]
    public async Task Categories_Delete_StillHandlesTreeCategories()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        // 树形分类（pc_categories 表）——它不在扁平规则表里，
        // 因此能删掉就证明运行的是 catWrite 的树形 DELETE，而不是旧的扁平 DELETE。
        var created = await PostJsonAsync(client, "/api/v1/pc/categories", new
        {
            name = "待删除分类",
            color = "#3b82f6",
            productivity = "neutral",
            sortOrder = 7
        });
        var id = created.GetProperty("data").GetProperty("id").GetString()!;

        var response = await client.DeleteAsync($"/api/v1/pc/categories/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tree = await GetJsonAsync(client, "/api/v1/pc/categories/tree");
        Assert.DoesNotContain(
            tree.GetProperty("data").EnumerateArray(),
            node => node.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Categories_Delete_FlatRuleIdFallsThroughToTreeHandler()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        // 扁平规则 id 交给树形入口 → 树里没有这个节点，返回 400（树形语义），
        // 旧扁平入口的语义是 404「不存在或为内置项」。
        var created = await PostJsonAsync(client, "/api/v1/pc/app-category-rules", new
        {
            appPattern = "flat-only.exe",
            categoryName = "开发",
            color = "#2563eb",
            priority = 5
        });
        var flatId = created.GetProperty("data").GetProperty("id").GetString()!;

        var response = await client.DeleteAsync($"/api/v1/pc/categories/{flatId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, document.RootElement.GetProperty("code").GetInt32());

        // 扁平规则没被误删
        var list = await GetJsonAsync(client, "/api/v1/pc/categories");
        Assert.Contains(
            list.GetProperty("data").EnumerateArray(),
            rule => rule.GetProperty("appPattern").GetString() == "flat-only.exe");
    }

    [Fact]
    public async Task AppCategoryRules_Delete_RequiresAuthorization()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/v1/pc/app-category-rules/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ===== helpers =====

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, Action<PimDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> CreateAuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"cat-e2e-{Guid.NewGuid():N}"[..24];
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = "cat-e2e"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"pc-cat-e2e-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(databaseName));
            });
        });
    }
}
