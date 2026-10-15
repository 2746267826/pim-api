using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 阶段一共用测试宿主。
///
/// 与既有 E2E 同构：真实宿主装配（真实路由 / 真实鉴权 / 真实服务层）+ 隔离 InMemory 库。
/// 额外提供两件事，避免每个用例重复样板：
///   1. <c>Auth:AllowPublicRegistration</c> 可显式注入 <c>true</c> / <c>false</c>，
///      也可以**完全不注入**（用于验证「未配置 == 显式 false」的缺省语义）；
///   2. 注册 / 登录 / 建种子用户 / 数表行数 的常用助手。
/// </summary>
internal static class AuthRegistrationTestHost
{
    /// <summary>公开注册开关的配置键（决策记录 §2，已冻结）。</summary>
    public const string AllowPublicRegistrationKey = "Auth:AllowPublicRegistration";

    public const string DefaultPassword = "password123";

    /// <summary>公开注册关闭时的错误码与提示语（决策记录 §3.1，已冻结，逐字比对）。</summary>
    public const int RegistrationClosedCode = 40304;
    public const string RegistrationClosedMessage = "公开注册已关闭，请联系管理员创建账号";

    public static WebApplicationFactory<Program> Create(
        string dbName,
        bool? allowPublicRegistration = null,
        IAuditLogService? auditLog = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            if (allowPublicRegistration is bool allow)
                b.UseSetting(AllowPublicRegistrationKey, allow ? "true" : "false");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(dbName));

                // 故障注入：替换审计服务，用来证明「审计写失败 ⇒ 业务改动不落库」
                if (auditLog is not null)
                {
                    var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IAuditLogService));
                    if (existing is not null) services.Remove(existing);
                    services.AddSingleton(auditLog);
                }
            });
        });

    public static string NewDbName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // ===== HTTP =====

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return ParseJson(body);
    }

    private static JsonElement ParseJson(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>读取 ApiResponse 信封里的 code / message。</summary>
    public static async Task<(int Code, string? Message)> ReadErrorAsync(HttpResponseMessage response)
    {
        var root = await ReadJsonAsync(response);
        return (
            root.GetProperty("code").GetInt32(),
            root.TryGetProperty("message", out var message) ? message.GetString() : null);
    }

    public static HttpClient Authed(WebApplicationFactory<Program> factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string username,
        string? email = null,
        string? password = null,
        string? displayName = null)
        => client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = email ?? $"{username}@example.com",
            password = password ?? DefaultPassword,
            displayName
        });

    /// <summary>匿名注册并断言 201，返回会话。</summary>
    public static async Task<Session> RegisterOkAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string? email = null,
        string? password = null)
    {
        var client = factory.CreateClient();
        var response = await RegisterAsync(client, username, email, password);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Session.FromAuthResponse(await ReadJsonAsync(response));
    }

    /// <summary>
    /// 登录。失败时服务端返回 <c>401</c> 且**响应体为空**（<c>Results.Unauthorized()</c>），
    /// 所以这里对空体宽容：<c>Root</c> 为 null 表示没有可解析的信封。
    /// </summary>
    public static async Task<(HttpStatusCode Status, JsonElement? Root)> LoginAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(body) ? null : ParseJson(body));
    }

    public static async Task<Session> LoginOkAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password = DefaultPassword)
    {
        var (status, root) = await LoginAsync(factory, username, password);
        Assert.Equal(HttpStatusCode.OK, status);
        return Session.FromAuthResponse(root!.Value);
    }

    public static Task<HttpResponseMessage> RefreshAsync(WebApplicationFactory<Program> factory, string refreshToken)
        => factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    // ===== 数据 =====

    /// <summary>直接落一个用户（绕过注册），用于构造「库非空」「已有管理员」等前态。</summary>
    public static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string role = "user",
        string password = DefaultPassword,
        bool isActive = true,
        string? email = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var user = new UserEntity
        {
            Username = username,
            Email = (email ?? $"{username}@example.com").ToLowerInvariant(),
            PasswordHash = PasswordHasher.Hash(password),
            DisplayName = username,
            Role = role,
            IsActive = isActive
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>先落一个用户再登录，拿到该用户的完整会话（含当前设备的 refresh token）。</summary>
    public static async Task<Session> SeedAndLoginAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string role = "user",
        string password = DefaultPassword)
    {
        var userId = await SeedUserAsync(factory, username, role, password);
        var session = await LoginOkAsync(factory, username, password);
        Assert.Equal(userId, session.UserId);
        return session;
    }

    /// <summary>把用户置为软删除（`deleted_at` 非空），用于验证「软删除 != 表为空」。</summary>
    public static async Task SoftDeleteUserAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 直接落一条 refresh token 行（可指定过期时间/撤销时间），
    /// 用于构造「本人已过期但未撤销」这类真实分支。
    /// </summary>
    public static async Task SeedRefreshTokenAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string plaintext,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            UserId = userId,
            TokenHash = RefreshTokenSessions.HashSecret(plaintext),
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        });
        await db.SaveChangesAsync();
    }

    /// <summary>表行数（含软删除行；AC-1.3 / AC-3.7 / AC-3.8 的「无写入」判定基准）。</summary>
    public static async Task<(int Users, int RefreshTokens, int AuditLogs)> TableCountsAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return (
            await db.Users.IgnoreQueryFilters().CountAsync(),
            await db.RefreshTokens.IgnoreQueryFilters().CountAsync(),
            await db.AuditLogs.IgnoreQueryFilters().CountAsync());
    }

    public static async Task<UserEntity?> FindUserAsync(WebApplicationFactory<Program> factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username);
    }

    public static async Task<List<AuditLogEntity>> AuditLogsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    /// <summary>该用户所有「未撤销」refresh token 的条数。</summary>
    public static async Task<int> ActiveRefreshTokenCountAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.RefreshTokens.IgnoreQueryFilters()
            .CountAsync(t => t.UserId == userId && t.RevokedAt == null);
    }

    public sealed record Session(Guid UserId, string AccessToken, string RefreshToken)
    {
        public static Session FromAuthResponse(JsonElement root)
        {
            var data = root.GetProperty("data");
            var user = data.GetProperty("user");
            return new Session(
                Guid.Parse(user.GetProperty("id").GetString()!),
                data.GetProperty("accessToken").GetString()!,
                data.GetProperty("refreshToken").GetString()!);
        }
    }
}
