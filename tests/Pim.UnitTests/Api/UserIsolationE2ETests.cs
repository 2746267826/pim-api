using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 数据隔离端到端测试：完整 HTTP 链路（JWT → 当前用户上下文 → 全局查询过滤器 → 服务层）。
/// 用户 A 创建的数据对 B 不可见（列表不可见、按 ID 访问 404）。
/// </summary>
public class UserIsolationE2ETests
{
    private static WebApplicationFactory<Program> CreateFactory(string dbName)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string username)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task QuickNotes_CrossUser_InvisibleInListAndGetById()
    {
        using var factory = CreateFactory($"iso-e2e-{Guid.NewGuid()}");
        var anon = factory.CreateClient();
        var tokenA = await RegisterAndGetTokenAsync(anon, "alice");
        var tokenB = await RegisterAndGetTokenAsync(anon, "bob");
        var alice = Authed(factory, tokenA);
        var bob = Authed(factory, tokenB);

        // alice 创建一条快速记录
        var createResp = await alice.PostAsJsonAsync("/api/v1/quick-notes",
            new { contentMarkdown = "alice 的私密记录", source = (string?)null, attachmentIds = (string[]?)null });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var noteId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        // alice 列表能看到
        var listA = await alice.GetAsync("/api/v1/quick-notes");
        Assert.Equal(HttpStatusCode.OK, listA.StatusCode);
        Assert.Contains(noteId, await listA.Content.ReadAsStringAsync());

        // bob 列表看不到 alice 的记录
        var listB = await bob.GetAsync("/api/v1/quick-notes");
        Assert.Equal(HttpStatusCode.OK, listB.StatusCode);
        Assert.DoesNotContain(noteId, await listB.Content.ReadAsStringAsync());

        // bob 按 ID 直接访问 → 404（过滤器使其不存在）
        var getB = await bob.GetAsync($"/api/v1/quick-notes/{noteId}");
        Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);

        // alice 按 ID 访问 → 200
        var getA = await alice.GetAsync($"/api/v1/quick-notes/{noteId}");
        Assert.Equal(HttpStatusCode.OK, getA.StatusCode);

        // bob 创建自己的记录后，alice 同样看不到
        var createB = await bob.PostAsJsonAsync("/api/v1/quick-notes",
            new { contentMarkdown = "bob 的记录", source = (string?)null, attachmentIds = (string[]?)null });
        Assert.Equal(HttpStatusCode.Created, createB.StatusCode);
        using var createBDoc = JsonDocument.Parse(await createB.Content.ReadAsStringAsync());
        var noteBId = createBDoc.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var listA2 = await alice.GetAsync("/api/v1/quick-notes");
        Assert.DoesNotContain(noteBId, await listA2.Content.ReadAsStringAsync());
    }
}
