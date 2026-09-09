using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 管理员体系端到端测试：注册角色判定、admin 端点鉴权、角色/状态管理、停用账号拦截。
/// 使用 WebApplicationFactory + InMemory 数据库替换，覆盖完整 HTTP 链路。
/// </summary>
public class AdminEndpointsE2ETests
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

    private static async Task<(HttpStatusCode Status, string? Role, string? AccessToken, string? RefreshToken)> RegisterAsync(
        HttpClient client, string username)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username
        });
        if (resp.StatusCode != HttpStatusCode.Created)
            return (resp.StatusCode, null, null, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var user = data.GetProperty("user");
        return (resp.StatusCode,
            user.GetProperty("role").GetString(),
            data.GetProperty("accessToken").GetString(),
            data.GetProperty("refreshToken").GetString());
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task FullAdminLifecycle_FirstUserAdmin_ManageUsers_DisabledBlocked()
    {
        using var factory = CreateFactory($"admin-e2e-{Guid.NewGuid()}");
        var anon = factory.CreateClient();

        // 1. 首个注册用户成为 admin
        var r1 = await RegisterAsync(anon, "alice");
        Assert.Equal(HttpStatusCode.Created, r1.Status);
        Assert.Equal("admin", r1.Role);

        // 2. 后续注册用户为普通 user
        var r2 = await RegisterAsync(anon, "bob");
        Assert.Equal(HttpStatusCode.Created, r2.Status);
        Assert.Equal("user", r2.Role);

        // 3. admin 可以列出用户
        var alice = Authed(factory, r1.AccessToken!);
        var listResp = await alice.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await ReadDataAsync(listResp);
        Assert.Equal(2, list.GetProperty("data").GetArrayLength());

        // 4. 普通用户访问管理端点 → 403
        var bob = Authed(factory, r2.AccessToken!);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/v1/admin/users")).StatusCode);

        // 5. 匿名访问管理端点 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/users")).StatusCode);

        // 6. 降级最后一名管理员 → 400（保护）
        var demoteResp = await alice.PostAsJsonAsync($"/api/v1/admin/users/{GetUserId(list, "alice")}/role", new { role = "user" });
        Assert.Equal(HttpStatusCode.BadRequest, demoteResp.StatusCode);
        var demoteBody = await ReadDataAsync(demoteResp);
        Assert.Equal(40042, demoteBody.GetProperty("code").GetInt32());

        // 7. 先提升 bob 为 admin，再降级 alice → 成功
        var promoteResp = await alice.PostAsJsonAsync($"/api/v1/admin/users/{GetUserId(list, "bob")}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.OK, promoteResp.StatusCode);
        var demoteResp2 = await alice.PostAsJsonAsync($"/api/v1/admin/users/{GetUserId(list, "alice")}/role", new { role = "user" });
        Assert.Equal(HttpStatusCode.OK, demoteResp2.StatusCode);

        // 7b. bob 重新登录获取带 admin 角色的新令牌（旧令牌仍是 user 角色）
        var bobLogin = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "bob", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, bobLogin.StatusCode);
        using var bobLoginDoc = JsonDocument.Parse(await bobLogin.Content.ReadAsStringAsync());
        var bobAdminToken = bobLoginDoc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
        var bobAdmin = Authed(factory, bobAdminToken);

        // 8. bob（现为 admin）停用 alice
        var disableResp = await bobAdmin.PostAsJsonAsync($"/api/v1/admin/users/{GetUserId(list, "alice")}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        // 9. 已停用账号登录 → 403 + 40030
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "alice", password = "password123" });
        Assert.Equal(HttpStatusCode.Forbidden, loginResp.StatusCode);
        var loginBody = await ReadDataAsync(loginResp);
        Assert.Equal(40030, loginBody.GetProperty("code").GetInt32());

        // 10. 已停用账号刷新令牌 → 401
        var refreshResp = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = r1.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);

        // 11. /me 返回当前用户角色
        var meResp = await bobAdmin.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var meBody = await ReadDataAsync(meResp);
        Assert.Equal("admin", meBody.GetProperty("data").GetProperty("role").GetString());
    }

    [Fact]
    public async Task AdminUsers_Anonymous_Returns401()
    {
        using var factory = CreateFactory($"admin-authz-{Guid.NewGuid()}");
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task AdminUsers_NonAdminJwt_Returns403()
    {
        using var factory = CreateFactory($"admin-authz-{Guid.NewGuid()}");
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var token = jwt.GenerateAccessToken(Guid.NewGuid(), "someone", "user");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Me_Anonymous_Returns401()
    {
        using var factory = CreateFactory($"admin-authz-{Guid.NewGuid()}");
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ChangeRole_InvalidRole_Returns400()
    {
        using var factory = CreateFactory($"admin-e2e-{Guid.NewGuid()}");
        var anon = factory.CreateClient();
        var r1 = await RegisterAsync(anon, "alice");
        var r2 = await RegisterAsync(anon, "bob");
        var alice = Authed(factory, r1.AccessToken!);
        var list = await ReadDataAsync(await alice.GetAsync("/api/v1/admin/users"));

        var resp = await alice.PostAsJsonAsync($"/api/v1/admin/users/{GetUserId(list, "bob")}/role", new { role = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadDataAsync(resp);
        Assert.Equal(40041, body.GetProperty("code").GetInt32());
    }

    private static string GetUserId(JsonElement listResponse, string username)
    {
        foreach (var u in listResponse.GetProperty("data").EnumerateArray())
        {
            if (u.GetProperty("username").GetString() == username)
                return u.GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"user {username} not found");
    }
}
