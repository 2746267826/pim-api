using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Contract;

using Pim.UnitTests.Api;

/// <summary>
/// 契约语义（docs/client-contract-semantics.md）必须同时体现在两处：
/// 导出的 OpenAPI 文档里，以及**运行时真的返回/接受**的东西里。
/// 与 CI 的「起 Test 环境取 swagger.json」等价，但用进程内宿主，测试里直接断言。
/// </summary>
public sealed class ContractSemanticsTests : IClassFixture<ContractSemanticsTests.Host>
{
    public sealed class Host : IDisposable
    {
        internal readonly WebApplicationFactory<Program> Factory;

        public Host()
        {
            Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("contract-sem"));
        }

        public void Dispose() => Factory.Dispose();
    }

    private readonly Host _host;

    public ContractSemanticsTests(Host host) => _host = host;

    private async Task<JsonElement> SwaggerAsync()
    {
        var client = _host.Factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"swagger 未导出：{(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static JsonElement Schema(JsonElement root, string name) =>
        root.GetProperty("components").GetProperty("schemas").GetProperty(name);

    /// <summary>
    /// §1：四个按日端点的载荷 schema 都必须声明业务日三字段；端点带 x-business-day。
    /// 四个都测（只有两个被测过的旧版本会让另外两个静默退化）。
    /// </summary>
    [Theory]
    [InlineData("TodaySectionRegistryDto", "/api/v1/today/sections")]
    [InlineData("TodaySectionDto", "/api/v1/today/sections/{sectionId}")]
    [InlineData("PcSummaryResponse", "/api/v1/pc/summary")]
    [InlineData("MobileUsageSummaryResponse", "/api/v1/mobile/summary")]
    public async Task BusinessDayFields_AreDeclaredOnDailyEndpoints(string schemaName, string path)
    {
        var swagger = await SwaggerAsync();
        var props = Schema(swagger, schemaName).GetProperty("properties");
        foreach (var field in new[] { "businessDate", "businessTimeZone", "businessDayStartHour" })
            Assert.True(props.TryGetProperty(field, out _), $"{schemaName} 缺少 {field}");
        Assert.True(
            swagger.GetProperty("paths").GetProperty(path).GetProperty("get").TryGetProperty("x-business-day", out _),
            $"{path} 缺少 x-business-day");
    }

    /// <summary>
    /// §1：契约声明了还不够 —— 运行时必须真的下发（否则客户端删掉本地推算后会拿不到值）。
    /// 四个端点都测：只测其中一两个时，其余端点的 `string.Empty` 默认值也能让「字段存在」通过。
    /// </summary>
    [Theory]
    [InlineData("/api/v1/today/sections")]
    [InlineData("/api/v1/today/sections/calendar.tasks")]
    [InlineData("/api/v1/pc/summary?date=2026-10-21")]
    [InlineData("/api/v1/mobile/summary?date=2026-10-21")]
    public async Task BusinessDayFields_ArePresentAtRuntime(string path)
    {
        // 同一个 fixture 共用一个库：theory 每个用例都要新用户，否则第二次登录会命中第一次创建的用户。
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            _host.Factory, $"contract-sem-runtime-{Guid.NewGuid():N}"[..40]);
        var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var root = await AuthRegistrationTestHost.ReadJsonAsync(await client.GetAsync(path));
        var data = root.GetProperty("data");
        var businessDate = data.GetProperty("businessDate").GetString();
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), businessDate);
        Assert.Equal("Asia/Shanghai", data.GetProperty("businessTimeZone").GetString());
        Assert.Equal(4, data.GetProperty("businessDayStartHour").GetInt32());
    }

    /// <summary>§1：registry 的 businessDate 与既有 date 同源，否则客户端会看到两个「今天」。</summary>
    [Fact]
    public async Task TodayRegistry_BusinessDateMatchesDate()
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_host.Factory, "contract-sem-registry");
        var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var data = (await AuthRegistrationTestHost.ReadJsonAsync(
            await client.GetAsync("/api/v1/today/sections"))).GetProperty("data");
        Assert.Equal(data.GetProperty("date").GetString(), data.GetProperty("businessDate").GetString());
    }

    /// <summary>
    /// §1 回归：00:00–04:00 属于上一个业务日。pc.* 分区的载荷按 PcBusinessDate 取数，
    /// 信封上的 businessDate 必须与载荷一致，不能一个说 21 号、另一个说 20 号。
    /// </summary>
    [Fact]
    public async Task PcSections_EnvelopeBusinessDateMatchesPayload()
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_host.Factory, "contract-sem-pc");
        var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // 明确带时间 02:00：query.Date = 2026-10-21，PcBusinessDate = 2026-10-20。
        var root = await AuthRegistrationTestHost.ReadJsonAsync(
            await client.GetAsync("/api/v1/today/sections/pc.activity?date=2026-10-21T02:00"));
        var data = root.GetProperty("data");
        Assert.Equal("2026-10-20", data.GetProperty("businessDate").GetString());
        Assert.Equal("2026-10-20",
            data.GetProperty("data").GetProperty("summary").GetProperty("businessDate").GetString());

        // 同一次请求里，不按 PC 业务日取数的分区仍然是日历日 21 号。
        var other = (await AuthRegistrationTestHost.ReadJsonAsync(
            await client.GetAsync("/api/v1/today/sections/calendar.tasks?date=2026-10-21T02:00")))
            .GetProperty("data");
        Assert.Equal("2026-10-21", other.GetProperty("businessDate").GetString());
    }

    /// <summary>
    /// §2：<c>Idempotency-Key</c> 只能出现在**运行时真的会生效**的写操作上。
    /// 匿名端点（无 JWT 用户）、OpsKey 端点、JSON 404 兜底、以及返回一次性凭据的端点都不应有。
    /// </summary>
    [Fact]
    public async Task IdempotencyKey_IsDeclaredOnlyWhereItWorks()
    {
        var swagger = await SwaggerAsync();
        var paths = swagger.GetProperty("paths");

        bool HasKey(JsonElement op) =>
            op.TryGetProperty("parameters", out var ps) && ps.EnumerateArray()
                .Any(p => p.TryGetProperty("name", out var n) && n.GetString() == "Idempotency-Key");

        Assert.True(HasKey(paths.GetProperty("/api/v1/calendar/tasks").GetProperty("post")),
            "POST /tasks 缺少 Idempotency-Key");
        Assert.False(HasKey(paths.GetProperty("/api/v1/calendar/tasks").GetProperty("get")),
            "GET /tasks 不应声明 Idempotency-Key");

        // 读操作（整份文档里的 GET）都不应声明
        foreach (var path in paths.EnumerateObject())
            if (path.Value.TryGetProperty("get", out var getOp))
                Assert.False(HasKey(getOp), $"{path.Name} 的 GET 不应声明 Idempotency-Key");

        // 运行时不会生效的三类：OpsKey、匿名、兜底
        Assert.False(HasKey(paths.GetProperty("/api/v1/ops/db/query").GetProperty("post")),
            "OpsKey 端点没有 JWT 用户，不应声明 Idempotency-Key");
        Assert.False(HasKey(paths.GetProperty("/api/v1/auth/login").GetProperty("post")),
            "匿名端点不应声明 Idempotency-Key");
        if (paths.TryGetProperty("/api/{path}", out var catchAll))
            foreach (var op in catchAll.EnumerateObject())
                Assert.False(HasKey(op.Value), $"兜底路由 {op.Name} 不应声明 Idempotency-Key");

        // 返回一次性凭据的端点：缓存响应体 = 凭据落库 24h，必须豁免
        Assert.False(HasKey(paths.GetProperty("/api/v1/mcp/clients").GetProperty("post")),
            "POST /mcp/clients 返回明文 token，不能参与幂等缓存");
        foreach (var path in new[]
                 {
                     "/api/v1/files/items/upload-session",   // 预授权上传 URL
                     "/api/v1/files/providers/onedrive",     // 设备码 user code
                     "/api/v1/files/providers/google/authorize", // 带一次性 state 的授权地址
                     "/api/v1/calendar/outlook/device-code"  // 设备码 user code
                 })
            Assert.False(HasKey(paths.GetProperty(path).GetProperty("post")),
                $"{path} 的响应含一次性凭据，不能参与幂等缓存");
    }

    /// <summary>§2：幂等运行时确实会返回的 409，以及重放标记，都必须在契约里。</summary>
    [Fact]
    public async Task IdempotentWrite_DeclaresConflictAndReplayHeader()
    {
        var swagger = await SwaggerAsync();
        var post = swagger.GetProperty("paths").GetProperty("/api/v1/calendar/tasks").GetProperty("post");
        var responses = post.GetProperty("responses");
        Assert.True(responses.TryGetProperty("409", out var conflict), "写操作缺少 409 响应");
        Assert.Contains("40902", conflict.GetProperty("description").GetString());
        Assert.True(
            responses.GetProperty("201").GetProperty("headers").TryGetProperty("Idempotent-Replayed", out _),
            "成功响应缺少 Idempotent-Replayed 头");
    }

    /// <summary>§3：分页端点只声明一种成功形状。</summary>
    [Fact]
    public async Task PagedEndpoints_DeclareExactlyOneSuccessShape()
    {
        var swagger = await SwaggerAsync();
        foreach (var path in new[] { "/api/v1/calendar/tasks", "/api/v1/calendar/events" })
        {
            var ok = swagger.GetProperty("paths").GetProperty(path).GetProperty("get")
                .GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.EndsWith("PagedResultApiResponse", ok.GetProperty("$ref").GetString());
        }
    }
}
