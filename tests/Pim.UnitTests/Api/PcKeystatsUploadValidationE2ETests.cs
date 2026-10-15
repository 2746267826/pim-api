using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #419：keystats 两个上传端点的「契约说必填 → 服务端真的拒」。
///
/// 真宿主装配（WebApplicationFactory&lt;Program&gt;）+ 真实路由 + 真实鉴权 + 隔离库，
/// 逐字段证明缺字段返回 400 + <c>ApiResponse</c> 包络，字段齐全则真落库（往返读回）。
/// 另有「大小写不敏感」用例，它是 A2 决定「两端同时切换、不留兼容期」的全部依据。
/// </summary>
public sealed class PcKeystatsUploadValidationE2ETests
{
    private const string UploadPath = "/api/v1/pc/keystats/upload";
    private const string SamplesPath = "/api/v1/pc/keystats/samples";

    /// <summary>契约 <c>KeystatsUploadRequest.required</c>（12 项）。</summary>
    public static TheoryData<string> UploadRequiredFields => new(
        "deviceId", "date", "keyPresses", "leftClicks", "middleClicks", "mouseDistance",
        "peakCps", "peakKps", "rightClicks", "scrollDistance", "sideBackClicks", "sideForwardClicks");

    /// <summary>
    /// 契约 <c>KeystatsSampleUploadRequest.required</c>（13 项）。
    /// 工单写 11，但去掉大写的 JsonPropertyName 后 peakKps/peakCps 才被 required 口径看见
    /// （旧契约是漏标），已与用户确认按重导出的契约走 13。
    /// </summary>
    public static TheoryData<string> SampleRequiredFields => new(
        "pimDeviceId", "sampledAt", "date", "keyPresses", "leftClicks", "middleClicks",
        "mouseDistance", "peakCps", "peakKps", "rightClicks", "scrollDistance",
        "sideBackClicks", "sideForwardClicks");

    // ===== A3：缺任一必填字段 → 400 + 可读错误 =====

    [Theory]
    [MemberData(nameof(UploadRequiredFields))]
    public async Task Upload_MissingAnyRequiredField_Returns400NamingTheField(string missing)
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = UploadBody();
        body.Remove(missing);

        var (status, document) = await PostRawAsync(client, UploadPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, document.RootElement.GetProperty("code").GetInt32());
        var message = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains(missing, message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SampleRequiredFields))]
    public async Task Samples_MissingAnyRequiredField_Returns400NamingTheField(string missing)
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = SampleBody();
        body.Remove(missing);

        var (status, document) = await PostRawAsync(client, SamplesPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, document.RootElement.GetProperty("code").GetInt32());
        var message = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains(missing, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ExplicitNullOnRequiredField_IsTreatedAsMissing()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = UploadBody();
        body["keyPresses"] = null;

        var (status, document) = await PostRawAsync(client, UploadPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("keyPresses", document.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // ===== A3：字段齐全 → 200 且真落库 =====

    [Fact]
    public async Task Upload_AllRequiredFields_Returns200AndRoundTripsThroughTheApiAndTheDatabase()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = UploadBody();
        body["deviceId"] = "roundtrip-device";

        var (status, document) = await PostRawAsync(client, UploadPath, body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());

        // 1) 库内往返断言（EF InMemory；真实 PostgreSQL 的事务/索引链路由
        //    PcKeystatsRealDbE2ETests 覆盖）
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var row = await db.Set<KeystatsDailyEntity>()
                .Include(x => x.KeyCounts)
                .SingleAsync(x => x.DeviceId == "roundtrip-device");
            Assert.Equal(new DateTime(2026, 10, 1), row.SnapshotDate.Date);
            Assert.Equal(1234, row.KeyPresses);
            Assert.Equal(11, row.LeftClicks);
            Assert.Equal(12, row.RightClicks);
            Assert.Equal(13, row.MiddleClicks);
            Assert.Equal(14, row.SideBackClicks);
            Assert.Equal(15, row.SideForwardClicks);
            Assert.Equal(1234.5, row.MouseDistance, 3);
            Assert.Equal(678.5, row.ScrollDistance, 3);
            Assert.Equal(7, row.PeakKps);
            Assert.Equal(8, row.PeakCps);
            Assert.Contains(row.KeyCounts, k => k.KeyName == "A" && k.Count == 900);
            Assert.Contains(row.KeyCounts, k => k.KeyName == "B" && k.Count == 334);
        }

        // 2) 用户可见路径断言：/summary 读回同一份数据（不是只断言状态码）
        var summary = await GetJsonAsync(client, "/api/v1/pc/summary?date=2026-10-01");
        var keystats = summary.GetProperty("data").GetProperty("keystats");
        Assert.Equal(1234, keystats.GetProperty("keyPresses").GetInt32());
        Assert.Equal(65, keystats.GetProperty("totalClicks").GetInt32());
        Assert.Equal(1234.5, keystats.GetProperty("mouseDistance").GetDouble(), 3);
        Assert.Equal(678.5, keystats.GetProperty("scrollDistance").GetDouble(), 3);
        Assert.Equal(7, keystats.GetProperty("peakKps").GetInt32());
        Assert.Equal(8, keystats.GetProperty("peakCps").GetInt32());
        Assert.Equal(900, keystats.GetProperty("keyPressCounts").GetProperty("A").GetInt32());
    }

    [Fact]
    public async Task Samples_AllRequiredFields_Returns200AndRoundTripsThroughTheDatabase()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var (status, document) = await PostRawAsync(client, SamplesPath, SampleBody());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var row = await db.Set<KeystatsSampleEntity>().SingleAsync(x => x.PimDeviceId == "sample-device");
        Assert.Equal(321, row.KeyPresses);
        Assert.Equal(1, row.LeftClicks);
        Assert.Equal(2, row.RightClicks);
        Assert.Equal(3, row.MiddleClicks);
        Assert.Equal(4, row.SideBackClicks);
        Assert.Equal(5, row.SideForwardClicks);
        Assert.Equal(11.25, row.MouseDistance, 3);
        Assert.Equal(22.5, row.ScrollDistance, 3);
        Assert.Equal(9, row.PeakKps);
        Assert.Equal(10, row.PeakCps);
    }

    // ===== A2 上线方式：服务端大小写不敏感，旧拼写仍然收 =====

    [Theory]
    [InlineData("peakKps", "peakCps")]
    [InlineData("peakKPS", "peakCPS")]
    public async Task Upload_PeakFieldSpellingIsCaseInsensitive(string peakKps, string peakCps)
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = UploadBody();
        body["deviceId"] = $"case-{peakKps}-{peakCps}";
        body.Remove("peakKps");
        body.Remove("peakCps");
        body[peakKps] = 7;
        body[peakCps] = 8;

        var (status, _) = await PostRawAsync(client, UploadPath, body);
        Assert.Equal(HttpStatusCode.OK, status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var row = await db.Set<KeystatsDailyEntity>()
            .SingleAsync(x => x.DeviceId == $"case-{peakKps}-{peakCps}");
        Assert.Equal(7, row.PeakKps);
        Assert.Equal(8, row.PeakCps);
    }

    [Theory]
    [InlineData("peakKps", "peakCps")]
    [InlineData("peakKPS", "peakCPS")]
    public async Task Samples_PeakFieldSpellingIsCaseInsensitive(string peakKps, string peakCps)
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var body = SampleBody();
        body["pimDeviceId"] = $"case-{peakKps}-{peakCps}";
        body.Remove("peakKps");
        body.Remove("peakCps");
        body[peakKps] = 9;
        body[peakCps] = 10;

        var (status, _) = await PostRawAsync(client, SamplesPath, body);
        Assert.Equal(HttpStatusCode.OK, status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var row = await db.Set<KeystatsSampleEntity>()
            .SingleAsync(x => x.PimDeviceId == $"case-{peakKps}-{peakCps}");
        Assert.Equal(9, row.PeakKps);
        Assert.Equal(10, row.PeakCps);
    }

    [Fact]
    public async Task Samples_OldAndNewSpellingProduceIdenticalRows()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var oldSpelling = SampleBody();
        oldSpelling.Remove("peakKps");
        oldSpelling.Remove("peakCps");
        oldSpelling["peakKPS"] = 9;
        oldSpelling["peakCPS"] = 10;
        oldSpelling["sampledAt"] = "2026-10-02T08:00:00Z";
        oldSpelling["date"] = "2026-10-02";

        var newSpelling = SampleBody();
        newSpelling["pimDeviceId"] = "sample-device";
        newSpelling["sampledAt"] = "2026-10-03T08:00:00Z";
        newSpelling["date"] = "2026-10-03";

        Assert.Equal(HttpStatusCode.OK, (await PostRawAsync(client, SamplesPath, oldSpelling)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostRawAsync(client, SamplesPath, newSpelling)).Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var rows = await db.Set<KeystatsSampleEntity>()
            .Where(x => x.PimDeviceId == "sample-device")
            .OrderBy(x => x.StatsDate)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].PeakKps, rows[1].PeakKps);
        Assert.Equal(rows[0].PeakCps, rows[1].PeakCps);
        Assert.Equal(rows[0].KeyPresses, rows[1].KeyPresses);
    }

    // ===== 包络/鉴权回归：400 不能变成 401 或 500 =====

    [Fact]
    public async Task Upload_WithoutToken_StillReturns401()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(UploadPath, UploadBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ===== 重复键（含大小写变体）：校验必须与绑定看同一个值 =====
    // 独立 review 复现过：校验取第一个匹配键、反序列化取最后一个，
    // {"date":"…","DATE":null} 于是先通过校验再把 Date 绑成 null → 服务层 500。

    [Fact]
    public async Task Upload_DuplicateKeyWithCaseVariantNullLast_Returns400NotServerError()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var json = RawUploadJson(
            "\"deviceId\":\"dup-case\"",
            "\"date\":\"2026-10-01\"",
            "\"DATE\":null");

        var (status, document) = await PostRawStringAsync(client, UploadPath, json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("date", document.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_DuplicateSameKeyWithNullLast_Returns400()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var json = RawUploadJson(
            "\"deviceId\":\"dup-same\"",
            "\"deviceId\":null",
            "\"date\":\"2026-10-01\"");

        var (status, document) = await PostRawStringAsync(client, UploadPath, json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("deviceId", document.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_DuplicateKeyLastWins_BindingAndValidationAgree()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        // 最后一个键有效 → 校验通过，且落库的是**最后一个**值（与 System.Text.Json 的后写覆盖一致）。
        var json = RawUploadJson(
            "\"deviceId\":\"dup-last\"",
            "\"date\":\"2026-09-01\"",
            "\"DATE\":\"2026-10-05\"");

        var (status, _) = await PostRawStringAsync(client, UploadPath, json);
        Assert.Equal(HttpStatusCode.OK, status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var row = await db.Set<KeystatsDailyEntity>().SingleAsync(x => x.DeviceId == "dup-last");
        Assert.Equal(new DateTime(2026, 10, 5), row.SnapshotDate.Date);
    }

    // ===== 非 JSON Content-Type 的行为与改动前一致 =====
    // 改动前 [FromBody] 参数会推导出 application/json 的 Accepts 元数据，非 JSON 请求匹配不上
    // 端点、落到全局兜底 404；现在显式 .Accepts<KeystatsUploadRequest>("application/json")
    // 是同一份元数据（已在真栈上对比过改动前后的 404 行为）。

    [Fact]
    public async Task Upload_NonJsonContentType_BehavesAsBefore()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var response = await client.PostAsync(
            UploadPath,
            new StringContent(JsonSerializer.Serialize(UploadBody()), Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ===== helpers =====

    private static string RawUploadJson(params string[] extraFields)
    {
        var fields = new List<string>
        {
            "\"keyPresses\":1",
            "\"leftClicks\":1",
            "\"rightClicks\":1",
            "\"middleClicks\":1",
            "\"sideBackClicks\":1",
            "\"sideForwardClicks\":1",
            "\"mouseDistance\":1",
            "\"scrollDistance\":1",
            "\"peakKps\":1",
            "\"peakCps\":1"
        };
        fields.AddRange(extraFields);
        return "{" + string.Join(",", fields) + "}";
    }

    private static async Task<(HttpStatusCode Status, JsonDocument Document)> PostRawStringAsync(
        HttpClient client, string url, string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    private static Dictionary<string, object?> UploadBody() => new()
    {
        ["deviceId"] = "roundtrip-device",
        ["date"] = "2026-10-01",
        ["keyPresses"] = 1234,
        ["keyPressCounts"] = new Dictionary<string, int> { ["A"] = 900, ["B"] = 334 },
        ["leftClicks"] = 11,
        ["rightClicks"] = 12,
        ["middleClicks"] = 13,
        ["sideBackClicks"] = 14,
        ["sideForwardClicks"] = 15,
        ["mouseDistance"] = 1234.5,
        ["scrollDistance"] = 678.5,
        ["peakKps"] = 7,
        ["peakCps"] = 8
    };

    private static Dictionary<string, object?> SampleBody() => new()
    {
        ["pimDeviceId"] = "sample-device",
        ["sampledAt"] = "2026-10-01T08:00:00Z",
        ["date"] = "2026-10-01",
        ["keyPresses"] = 321,
        ["keyPressCounts"] = new Dictionary<string, int> { ["A"] = 321 },
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
        var username = $"ks-e2e-{Guid.NewGuid():N}"[..24];
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = "ks-e2e"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"keystats-e2e-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            // WO-AUTH-REGISTER-20261014：公开注册默认关闭；本文件靠注册造号，
            // 按决策记录 §2 在测试工厂里显式开启开关（不是删用例/跳用例换绿灯）。
            b.UseSetting("Auth:AllowPublicRegistration", "true");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                // PcTrackerService.UpsertKeystatsAsync 用显式事务（真库上的删除+重插必须原子）。
                // 内存库不支持事务，默认会抛 TransactionIgnoredWarning 异常 —— 这里按 EF 官方
                // 建议忽略该警告，让事务退化成 no-op；真实 SQL/事务链路由
                // PcKeystatsRealDbE2ETests 在真库上验证。
                services.AddDbContext<PimDbContext>(o => o
                    .UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        });
    }
}
