using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 会话撤销与「读后写」端点的**并发**交错：真实宿主 + 真实 PostgreSQL。
///
/// 背景（阶段一独立 review 指出的 Important）：<c>/auth/refresh</c> 先读有效令牌、再撤销旧的
/// 并插入新的；若在它的读与写之间，改密 / 管理员重置把该用户全部会话撤销并提交，
/// refresh 仍会插入一条**从未被撤销**的新令牌 —— 被踢的设备保留登录态，REQ-6 落空。
/// 登录同理：用旧口令校验通过后、插入会话之前若发生重置，这条新会话会逃过撤销。
///
/// 本文件用**确定性**手法复现，不靠"多跑几次碰运气"：
/// 另一条连接先取得该用户的事务级 advisory lock（这正是修复所引入的串行化点），
/// 在持锁期间提交撤销/改密，然后才释放锁。
///   - 修复前：两个端点不取锁，会抢在撤销提交之前读旧值 → 返回 200（逃逸）→ 用例红；
///   - 修复后：端点阻塞在锁上，释放后**重读** → 令牌已撤销 / 口令哈希已变 → 401 → 用例绿。
/// 无库环境（PIM_TEST_CONN 未设置或不可达）显式 Skip，不是静默通过。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class AuthSessionRaceRealDbTests
{
    private const string Password = "password123";

    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseNpgsql(connectionString));
            });
        });

    private static async Task<(Guid UserId, string RefreshToken)> RegisterAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = Password
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        return (
            Guid.Parse(data.GetProperty("user").GetProperty("id").GetString()!),
            data.GetProperty("refreshToken").GetString()!);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("refreshToken").GetString()!;
    }

    /// <summary>在给定事务上取该用户的会话锁（与产品代码同一个键派生）。</summary>
    private static async Task<NpgsqlTransaction> LockUserAsync(NpgsqlConnection connection, Guid userId)
    {
        var tx = connection.BeginTransaction();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, tx);
        command.Parameters.AddWithValue("key", UserSessionLock.KeyFor(userId));
        await command.ExecuteNonQueryAsync();
        return tx;
    }

    private static async Task<int> CountLiveSessionsAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM refresh_tokens WHERE user_id = @u AND revoked_at IS NULL", connection);
        command.Parameters.AddWithValue("u", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Refresh_RacingWithSessionRevocation_MustNotEscape()
    {
        RealDbTestConnection.SkipIfUnavailable();
        await using var temp = await TempMigrationDatabase.CreateAsync("auth-race-refresh");
        using var factory = CreateFactory(temp.ConnectionString);
        var anon = factory.CreateClient();

        var user = await RegisterAsync(anon, "race-refresh");
        var deviceB = await LoginAsync(anon, "race-refresh");
        Assert.Equal(2, await CountLiveSessionsAsync(temp.ConnectionString, user.UserId));

        // 另一条连接先取该用户的会话锁，并在**未提交**的状态下撤销全部会话
        await using var blocker = new NpgsqlConnection(temp.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await LockUserAsync(blocker, user.UserId);

        // 修复前：refresh 不等待，立刻读到"仍然有效"的旧令牌
        var refreshTask = anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = deviceB });
        await Task.Delay(TimeSpan.FromSeconds(1));

        await using (var revoke = new NpgsqlCommand(
            "UPDATE refresh_tokens SET revoked_at = now() WHERE user_id = @u AND revoked_at IS NULL", blocker, tx))
        {
            revoke.Parameters.AddWithValue("u", user.UserId);
            await revoke.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync(); // 释放 advisory lock

        var response = await refreshTask;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountLiveSessionsAsync(temp.ConnectionString, user.UserId));
    }

    [SkippableFact]
    public async Task Login_VerifiedWithOldPasswordWhilePasswordChanges_MustNotIssueSession()
    {
        RealDbTestConnection.SkipIfUnavailable();
        await using var temp = await TempMigrationDatabase.CreateAsync("auth-race-login");
        using var factory = CreateFactory(temp.ConnectionString);
        var anon = factory.CreateClient();

        var user = await RegisterAsync(anon, "race-login");
        Assert.Equal(1, await CountLiveSessionsAsync(temp.ConnectionString, user.UserId));

        await using var blocker = new NpgsqlConnection(temp.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await LockUserAsync(blocker, user.UserId);

        // 修复前：登录用旧口令校验通过后立刻插入会话
        var loginTask = anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "race-login", password = Password });
        await Task.Delay(TimeSpan.FromSeconds(1));

        // 模拟"改密/重置"：把口令哈希换掉，然后提交（释放锁）
        await using (var change = new NpgsqlCommand(
            "UPDATE users SET password_hash = @h WHERE id = @u", blocker, tx))
        {
            change.Parameters.AddWithValue("h", PasswordHasher.Hash("another-password-1"));
            change.Parameters.AddWithValue("u", user.UserId);
            await change.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        var response = await loginTask;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // 原来那条会话仍在（本用例只改口令哈希，未撤销会话），但**不能**多出一条新会话
        Assert.Equal(1, await CountLiveSessionsAsync(temp.ConnectionString, user.UserId));
    }
}
