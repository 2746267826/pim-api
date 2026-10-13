using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// issue #419 的真库链路：真实宿主装配 + 真实路由 + 真实鉴权 + **真实 PostgreSQL**。
///
/// 为什么单独一条：<c>PcTrackerService.UpsertKeystatsAsync</c> 走显式事务（同一天的
/// 上报要先删旧的 key_counts/app_breakdowns 再重插），内存库上事务会退化成 no-op，
/// 只有真库能证明 SQL / 唯一索引 / 级联删除这一整条链路。
/// 内存库版本见 <see cref="PcKeystatsUploadValidationE2ETests"/>（校验用例在那里跑得更快）。
/// 无库环境（PIM_TEST_CONN 未设置或不可达）显式 Skip，不是静默通过。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class PcKeystatsRealDbE2ETests
{
    [SkippableFact]
    public async Task Upload_FullPayload_PersistsAndReplacesTheSameDaySnapshot()
    {
        var connectionString = RealDbTestConnection.Require();
        var deviceId = $"realdb-{Guid.NewGuid():N}";
        try
        {
            using var factory = CreateFactory(connectionString);
            var client = await CreateAuthedClientAsync(factory);

            var body = UploadBody(deviceId, keyPresses: 1234, peakKps: 7, peakCps: 8);
            var (firstStatus, _) = await PostRawAsync(client, "/api/v1/pc/keystats/upload", body);
            Assert.Equal(HttpStatusCode.OK, firstStatus);

            // 1) 真库读回（设备/日期唯一索引命中同一行）
            Assert.Equal((1234, 7, 8, 2), await ReadUploadAsync(connectionString, deviceId));

            // 2) 同一天再报一次 → 走事务里的「删子表 + 重插」，仍然是同一行、没有孤儿子行
            var second = UploadBody(deviceId, keyPresses: 4321, peakKps: 17, peakCps: 18);
            second["keyPressCounts"] = new Dictionary<string, int> { ["C"] = 4321 };
            var (secondStatus, _) = await PostRawAsync(client, "/api/v1/pc/keystats/upload", second);
            Assert.Equal(HttpStatusCode.OK, secondStatus);

            Assert.Equal((4321, 17, 18, 1), await ReadUploadAsync(connectionString, deviceId));

            // 3) 用户可见路径读回同一份数据
            var summary = await GetJsonAsync(client, "/api/v1/pc/summary?date=2026-10-01");
            var keystats = summary.GetProperty("data").GetProperty("keystats");
            Assert.Equal(4321, keystats.GetProperty("keyPresses").GetInt32());
            Assert.Equal(17, keystats.GetProperty("peakKps").GetInt32());
            Assert.Equal(18, keystats.GetProperty("peakCps").GetInt32());
        }
        finally
        {
            await CleanupAsync(connectionString, $"DELETE FROM pc_keystats_daily WHERE device_id = '{deviceId}'");
        }
    }

    [SkippableFact]
    public async Task Samples_LegacyUppercasePeakSpelling_IsStillAcceptedOnRealDatabase()
    {
        var connectionString = RealDbTestConnection.Require();
        var deviceId = $"realdb-sample-{Guid.NewGuid():N}";
        try
        {
            using var factory = CreateFactory(connectionString);
            var client = await CreateAuthedClientAsync(factory);

            // 旧客户端（pim-windows）发的仍是 peakKPS/peakCPS；A2 不留兼容期的前提就是
            // 「请求体字段名大小写不敏感」，这里在真库链路上再证一次。
            var body = SampleBody(deviceId);
            body.Remove("peakKps");
            body.Remove("peakCps");
            body["peakKPS"] = 9;
            body["peakCPS"] = 10;

            var (status, _) = await PostRawAsync(client, "/api/v1/pc/keystats/samples", body);
            Assert.Equal(HttpStatusCode.OK, status);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT key_presses, peak_kps, peak_cps FROM pc_keystats_samples WHERE pim_device_id = @device", connection);
            command.Parameters.AddWithValue("device", deviceId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "样本没有落库");
            Assert.Equal(321, reader.GetInt32(0));
            Assert.Equal(9, reader.GetInt32(1));
            Assert.Equal(10, reader.GetInt32(2));
        }
        finally
        {
            await CleanupAsync(connectionString, $"DELETE FROM pc_keystats_samples WHERE pim_device_id = '{deviceId}'");
        }
    }

    [SkippableFact]
    public async Task Upload_MissingRequiredField_Returns400BeforeTouchingTheDatabase()
    {
        var connectionString = RealDbTestConnection.Require();
        var deviceId = $"realdb-reject-{Guid.NewGuid():N}";
        try
        {
            using var factory = CreateFactory(connectionString);
            var client = await CreateAuthedClientAsync(factory);

            var body = UploadBody(deviceId, keyPresses: 1, peakKps: 1, peakCps: 1);
            body.Remove("mouseDistance");

            var (status, document) = await PostRawAsync(client, "/api/v1/pc/keystats/upload", body);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(
                "mouseDistance",
                document.RootElement.GetProperty("message").GetString()!,
                StringComparison.Ordinal);

            // 被拒的请求不能留下任何行（此前是 200 + 静默存 0）
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pc_keystats_daily WHERE device_id = @device", connection);
            command.Parameters.AddWithValue("device", deviceId);
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanupAsync(connectionString, $"DELETE FROM pc_keystats_daily WHERE device_id = '{deviceId}'");
        }
    }

    // ===== helpers =====

    private static async Task<(int KeyPresses, int PeakKps, int PeakCps, long KeyCountRows)> ReadUploadAsync(
        string connectionString, string deviceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT d.key_presses, d.peak_kps, d.peak_cps,
                   (SELECT count(*) FROM pc_keystats_key_counts k WHERE k.daily_snapshot_id = d.id)
            FROM pc_keystats_daily d
            WHERE d.device_id = @device
            """, connection);
        command.Parameters.AddWithValue("device", deviceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "上传没有落库");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    private static async Task CleanupAsync(string connectionString, string sql)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // 清理失败不能把用例结果改写成"通过"：写出来，方便在 CI 日志里发现脏数据。
            Console.WriteLine($"[PcKeystatsRealDbE2ETests] cleanup failed: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> UploadBody(string deviceId, int keyPresses, int peakKps, int peakCps) => new()
    {
        ["deviceId"] = deviceId,
        ["date"] = "2026-10-01",
        ["keyPresses"] = keyPresses,
        ["keyPressCounts"] = new Dictionary<string, int> { ["A"] = 900, ["B"] = 334 },
        ["leftClicks"] = 11,
        ["rightClicks"] = 12,
        ["middleClicks"] = 13,
        ["sideBackClicks"] = 14,
        ["sideForwardClicks"] = 15,
        ["mouseDistance"] = 1234.5,
        ["scrollDistance"] = 678.5,
        ["peakKps"] = peakKps,
        ["peakCps"] = peakCps
    };

    private static Dictionary<string, object?> SampleBody(string deviceId) => new()
    {
        ["pimDeviceId"] = deviceId,
        // 固定时间戳（不用 UtcNow）：设备 id 每次都唯一，不会撞 (pim_device_id, sampled_at_utc) 唯一索引，
        // 断言因此与运行时刻无关。
        ["sampledAt"] = "2026-10-01T08:00:00Z",
        ["date"] = "2026-10-01",
        ["keyPresses"] = 321,
        ["leftClicks"] = 1,
        ["rightClicks"] = 2,
        ["middleClicks"] = 3,
        ["sideBackClicks"] = 4,
        ["sideForwardClicks"] = 5,
        ["mouseDistance"] = 11.25,
        ["scrollDistance"] = 22.5,
        ["peakKps"] = 9,
        ["peakCps"] = 10
    };

    private static async Task<(HttpStatusCode Status, JsonDocument Document)> PostRawAsync(
        HttpClient client, string url, Dictionary<string, object?> body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<HttpClient> CreateAuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"ks-realdb-{Guid.NewGuid():N}"[..24];
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = "ks-realdb"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

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
}
