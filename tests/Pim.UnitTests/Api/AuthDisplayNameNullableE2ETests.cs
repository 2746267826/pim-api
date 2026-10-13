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
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #417 第三节 / 工单 C3：<c>users.display_name</c> 在库里可空，契约却声明非空。
///
/// 本文件证明「display_name 为 null 的用户」在真实宿主 + 真实路由 + 真实鉴权下
/// 登录 / 续期 / 取当前用户都正常：返回 <c>displayName: null</c>，
/// 而不是 500、也不是反序列化异常（契约把该字段标成非空时，严格客户端会挂）。
/// </summary>
public sealed class AuthDisplayNameNullableE2ETests
{
    [Fact]
    public async Task Login_UserWithoutDisplayName_ReturnsNullDisplayName()
    {
        using var factory = CreateFactory();
        var userId = Guid.NewGuid();
        await SeedAsync(factory, db => db.Users.Add(new UserEntity
        {
            Id = userId,
            Username = "no-display-name",
            Email = "no-display-name@example.com",
            PasswordHash = PasswordHasher.Hash("password123"),
            DisplayName = null,
            Role = "user",
            IsActive = true
        }));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "no-display-name",
            password = "password123"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("user").GetProperty("displayName").ValueKind);
        Assert.Equal("no-display-name", data.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal(userId.ToString(), data.GetProperty("user").GetProperty("id").GetString());

        // 续期：同一条记录，同样不得炸
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            refreshToken = data.GetProperty("refreshToken").GetString()
        });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        using var refreshDocument = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        var refreshedUser = refreshDocument.RootElement.GetProperty("data").GetProperty("user");
        Assert.Equal(JsonValueKind.Null, refreshedUser.GetProperty("displayName").ValueKind);

        // /me：同一份 UserInfo
        var accessToken = refreshDocument.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDocument = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, meDocument.RootElement.GetProperty("data").GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task Login_UserWithDisplayName_StillReturnsTheName()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, db => db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = "with-display-name",
            Email = "with-display-name@example.com",
            PasswordHash = PasswordHasher.Hash("password123"),
            DisplayName = "有名字",
            Role = "user",
            IsActive = true
        }));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "with-display-name",
            password = "password123"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "有名字",
            document.RootElement.GetProperty("data").GetProperty("user").GetProperty("displayName").GetString());
    }

    // ===== helpers =====

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, Action<PimDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"auth-null-e2e-{Guid.NewGuid():N}";
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
