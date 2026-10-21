using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 幂等（契约语义 §2）在**真实 PostgreSQL** 上的行为：唯一索引是「并发同 key 只执行一次」的
/// 唯一保证，而 EF InMemory 根本不执行索引 —— 只靠 InMemory 测试等于没测。
///
/// <para>
/// 三个关键事实：
/// 1. 并发两个同 key 请求 → 业务数据只产生一份；
/// 2. 唯一索引冲突**只**来自并发（23505），不会被误当成其它数据库错误；
/// 3. 过期的占位/完成记录不会让同一个 key 永久 409（接管过期行）。
/// </para>
///
/// 无库环境（<c>PIM_TEST_CONN</c> 未设置或不可达）显式 Skip，不是静默通过。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class IdempotencyRealDbTests
{
    private const string Tasks = "/api/v1/calendar/tasks";
    private const string KeyHeader = "Idempotency-Key";

    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.UseSetting("Auth:AllowPublicRegistration", "true");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseNpgsql(connectionString));
            });
        });

    private static async Task<(Guid UserId, string AccessToken)> RegisterAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        return (
            Guid.Parse(data.GetProperty("user").GetProperty("id").GetString()!),
            data.GetProperty("accessToken").GetString()!);
    }

    private static HttpRequestMessage Post(object body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Tasks) { Content = JsonContent.Create(body) };
        request.Headers.Add(KeyHeader, key);
        return request;
    }

    private static async Task<int> CountTasksAsync(HttpClient client)
    {
        var root = JsonDocument.Parse(await client.GetStringAsync(Tasks + "?page=1&pageSize=100")).RootElement;
        return root.GetProperty("data").GetProperty("totalCount").GetInt32();
    }

    /// <summary>直接插一条（可指定已过期/执行中）记录，模拟另一进程遗留的行。</summary>
    private static async Task SeedRecordAsync(
        string connectionString, Guid userId, string key, string state, DateTimeOffset expiresAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO idempotency_records
                (id, user_id, method, route, idempotency_key, state, status_code, content_type,
                 response_body, response_headers, created_at, expires_at)
            VALUES (@id, @uid, 'POST', @route, @key, @state, NULL, NULL, NULL, NULL, now(), @expires)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("uid", userId);
        command.Parameters.AddWithValue("route", Tasks);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("expires", expiresAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountRecordsAsync(string connectionString, string key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM idempotency_records WHERE idempotency_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// 并发两个同 key 请求：唯一索引必须让其中一个拿到「首次」，另一个要么重放、要么 409；
    /// 无论哪种，业务数据只能有一份。这是「并发只执行一次」唯一有说服力的证据。
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentSameKey_ExecutesBusinessLogicOnce()
    {
        RealDbTestConnection.SkipIfUnavailable();
        await using var temp = await TempMigrationDatabase.CreateAsync("idem-race");
        using var factory = CreateFactory(temp.ConnectionString);
        var client = factory.CreateClient();
        var session = await RegisterAsync(client, "idem-race");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var key = Guid.NewGuid().ToString();
        var body = new { title = "并发幂等", priority = 0 };

        var responses = await Task.WhenAll(
            client.SendAsync(Post(body, key)),
            client.SendAsync(Post(body, key)));

        var statuses = responses.Select(r => r.StatusCode).ToArray();
        Assert.All(statuses, s => Assert.True(
            s is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"意外状态码：{(int)s}"));

        Assert.Equal(1, await CountTasksAsync(client));
        Assert.Equal(1, await CountRecordsAsync(temp.ConnectionString, key));

        var created = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.Created);
        Assert.NotNull(created);
        using var doc = JsonDocument.Parse(await created!.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// 过期记录占着唯一索引时，同一个 key 必须还能重新执行（接管过期行），
    /// 而不是永久 409。先种一条**已过期**且状态为 in_progress 的行 —— 这正是进程崩溃留下的形态。
    /// </summary>
    [SkippableFact]
    public async Task ExpiredInProgressRecord_IsTakenOver_NotPermanentConflict()
    {
        RealDbTestConnection.SkipIfUnavailable();
        await using var temp = await TempMigrationDatabase.CreateAsync("idem-expired");
        using var factory = CreateFactory(temp.ConnectionString);
        var client = factory.CreateClient();
        var session = await RegisterAsync(client, "idem-expired");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var key = Guid.NewGuid().ToString();
        await SeedRecordAsync(temp.ConnectionString, session.UserId, key, "in_progress",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        var first = await client.SendAsync(Post(new { title = "接管", priority = 0 }, key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 同 key 再来一次：这次应当重放首次响应，而不是再建一条
        var second = await client.SendAsync(Post(new { title = "接管", priority = 0 }, key));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var a = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var b = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(
            a.RootElement.GetProperty("data").GetProperty("id").GetString(),
            b.RootElement.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal(1, await CountTasksAsync(client));
    }
}
