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

    /// <summary>
    /// 等到「确实有请求阻塞在 advisory lock 上」再继续。
    ///
    /// 固定 <c>Task.Delay</c> 是假绿来源：请求可能还没进临界区，此时即使锁协议被移除、
    /// 端点根本没参与串行化，用例也会因为"撤销提交得晚"而偶然通过。
    /// 这里改用 <c>pg_locks</c> 里「未授予的 advisory 锁」作为**可观察**信号；
    /// 等不到就说明端点没有阻塞 → 由调用方断言失败。
    /// </summary>
    private static async Task<bool> WaitForBlockedOnAdvisoryLockAsync(
        string connectionString, Guid userId, TimeSpan timeout)
    {
        /*
         * 必须**精确**匹配这把锁，不能只数"实例上有任意 advisory 等待者"：
         * pg_locks 是整个实例级的，而本机同时有其它会话在同一实例上跑测试，
         * 别人的等待者会把我们提前放行（review 指出）。
         * bigint advisory lock 在 pg_locks 里是 classid=高 32 位、objid=低 32 位、objsubid=1，
         * 再加上 database = 当前库，即可唯一锁定"本库、本用户、正在等待"这一个事实。
         */
        var key = UserSessionLock.KeyFor(userId);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory'
                  AND granted = false
                  AND objsubid = 1
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND classid::bigint = @high
                  AND objid::bigint = @low
                """, connection);
            // pg_locks 的 classid/objid 是 oid（**无符号** 32 位）：必须按无符号拆，
            // 否则 key 为负时 classid 会算成负数而永远匹配不上（已实测 pg_locks 的取值）。
            var unsigned = (ulong)key;
            command.Parameters.AddWithValue("high", (long)(unsigned >> 32));
            command.Parameters.AddWithValue("low", (long)(unsigned & 0xFFFFFFFFUL));
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return true;
            await Task.Delay(50);
        }
        return false;
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

        var refreshTask = anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = deviceB });
        // 必须等到它**真的**阻塞在锁上，否则这条用例证明不了串行化（见辅助方法注释）
        Assert.True(
            await WaitForBlockedOnAdvisoryLockAsync(temp.ConnectionString, user.UserId, TimeSpan.FromSeconds(15)),
            "refresh 没有阻塞在该用户的 advisory lock 上：端点未参与会话串行化");

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

        var loginTask = anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "race-login", password = Password });
        Assert.True(
            await WaitForBlockedOnAdvisoryLockAsync(temp.ConnectionString, user.UserId, TimeSpan.FromSeconds(15)),
            "login 没有阻塞在该用户的 advisory lock 上：端点未参与会话串行化");

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
