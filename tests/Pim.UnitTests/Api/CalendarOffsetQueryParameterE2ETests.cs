using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #352 的真实宿主端到端回归：带**非零时区偏移**的 ISO 8601 查询参数
/// （如 <c>+08:00</c>）必须被正确处理并返回 200，且语义与等价 UTC 时刻一致。
///
/// <para>
/// 为什么必须是真实宿主 + 真实 PostgreSQL：缺陷只在参数真正写进 Npgsql 时触发
/// （<c>Cannot write DateTimeOffset with Offset=08:00:00</c>）。InMemory provider
/// 不做类型转换，用它验证等于什么都没验证 —— 这正是本缺陷此前逃过所有单测的原因。
/// </para>
///
/// <para>
/// 修复在数据库边界（<see cref="UtcDateTimeOffsetParameterInterceptor"/>），因此
/// 这里从**HTTP 入口**验证，覆盖从查询参数绑定 → 服务层 → EF → Npgsql 的完整链路。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class CalendarOffsetQueryParameterE2ETests
{
    /// <summary>
    /// 用真实 PostgreSQL 起一个完整 API 宿主。
    ///
    /// <para>
    /// **不替换也不重建 <c>PimDbContext</c> 的注册**：只把连接串喂给配置，
    /// 让 <c>AddPimInfrastructure</c> 按生产路径完成装配。这样本用例真正验证的是
    /// 「生产的 DI 注册里带着偏移归一化拦截器」——若有人把
    /// <c>ServiceCollectionExtensions</c> 里的 <c>AddInterceptors</c> 删掉，
    /// 这些用例会立刻失败（复审 Minor：先前版本自行重建注册，删掉生产那行仍然全绿）。
    /// </para>
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            // 走生产装配路径：AddPimInfrastructure 读这个键并装上拦截器。
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            // 生产装配会创建 DataProtection 密钥目录（默认 /data/keys/data-protection，
            // CI runner 上不可写 → UnauthorizedAccessException: Access to the path '/data' is denied）。
            // 指向进程可写的临时目录，保持"跑真实装配路径"而不是绕过它。
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
            // Jwt:PrivateKeyPath 缺失时 Test 环境回退到进程内临时 RSA，无需真实密钥文件。
        });

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>建一个日历（创建日程必须归属某个日历）。</summary>
    private static async Task<Guid> CreateCalendarAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/calendar/calendars", new
        {
            name = $"offset-probe-{Guid.NewGuid():N}"[..30],
            color = "#3B82F6",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// 取 <c>data</c> 并剔除**请求回显**与**服务端生成时刻**，只比较查询结果本身：
    /// <list type="bullet">
    ///   <item><description><c>start</c>/<c>end</c> 是请求参数原样回显，<c>+14:00</c> 与
    ///   等价 <c>Z</c> 的字符串表示本就不同（同一时刻），不是语义差异；</description></item>
    ///   <item><description><c>generatedAt</c>/<c>timestamp</c> 是两次请求各自的生成时刻。</description></item>
    /// </list>
    /// </summary>
    private static string NormalizeData(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return json;
        }

        static string Canonical(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .Where(property => property.Name is not ("generatedAt" or "timestamp" or "start" or "end"))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"\"{property.Name}\":{Canonical(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
            JsonValueKind.String => $"\"{element.GetString()}\"",
            _ => element.GetRawText()
        };

        return Canonical(data);
    }

    [SkippableFact]
    public async Task Layers_WithNonZeroOffset_Returns200AndMatchesEquivalentUtc()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), $"offlay_{Guid.NewGuid():N}"[..20]);
        var client = Authed(factory, token);

        // 2026-09-27T00:00:00+08:00 与 2026-09-26T16:00:00Z 是同一时刻。
        var offsetResponse = await client.GetAsync(
            "/api/v1/calendar/layers?start=2026-09-27T00:00:00%2B08:00&end=2026-09-28T00:00:00%2B08:00&layers=all");
        var utcResponse = await client.GetAsync(
            "/api/v1/calendar/layers?start=2026-09-26T16:00:00Z&end=2026-09-27T16:00:00Z&layers=all");

        // #352 的核心断言：带偏移不再 500，且与 UTC 等价。
        Assert.Equal(HttpStatusCode.OK, utcResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, offsetResponse.StatusCode);
        Assert.Equal(
            NormalizeData(await utcResponse.Content.ReadAsStringAsync()),
            NormalizeData(await offsetResponse.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// 带偏移与等价 UTC 必须返回**同一批日程**：在窗口内播种一条日程，
    /// 断言两种格式都命中它。这补上了"两边都为空"时比对无区分度的缺口
    /// （复审 Minor）。
    /// </summary>
    [SkippableFact]
    public async Task Layers_WithNonZeroOffset_ReturnsTheSameEventsAsEquivalentUtc()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var username = $"offrow_{Guid.NewGuid():N}"[..20];
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), username);
        var client = Authed(factory, token);

        // 窗口 = 2026-03-02T16:00Z ~ 2026-03-03T16:00Z（= 2026-03-03 00:00+08:00 起一整天）。
        // 在该窗口内建一条日程，其标题可唯一识别。
        var title = $"offset-probe-{Guid.NewGuid():N}";
        var calendarId = await CreateCalendarAsync(client);
        var createResponse = await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title,
            description = (string?)null,
            location = (string?)null,
            dtStart = "2026-03-03T02:00:00+08:00",
            dtEnd = "2026-03-03T03:00:00+08:00",
            rRule = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var offsetResponse = await client.GetAsync(
            "/api/v1/calendar/layers?start=2026-03-03T00:00:00%2B08:00&end=2026-03-04T00:00:00%2B08:00&layers=events");
        var utcResponse = await client.GetAsync(
            "/api/v1/calendar/layers?start=2026-03-02T16:00:00Z&end=2026-03-03T16:00:00Z&layers=events");

        Assert.Equal(HttpStatusCode.OK, utcResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, offsetResponse.StatusCode);

        var offsetBody = await offsetResponse.Content.ReadAsStringAsync();
        var utcBody = await utcResponse.Content.ReadAsStringAsync();

        // 两种格式都必须命中同一条日程（证明偏移被正确归一，未把窗口挪走）。
        Assert.Contains(title, offsetBody, StringComparison.Ordinal);
        Assert.Contains(title, utcBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Events_WithNonZeroOffset_Returns200AndMatchesEquivalentUtc()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), $"offevt_{Guid.NewGuid():N}"[..20]);
        var client = Authed(factory, token);

        var offsetResponse = await client.GetAsync(
            "/api/v1/calendar/events?start=2026-09-27T00:00:00%2B08:00&end=2026-09-28T00:00:00%2B08:00");
        var utcResponse = await client.GetAsync(
            "/api/v1/calendar/events?start=2026-09-26T16:00:00Z&end=2026-09-27T16:00:00Z");

        Assert.Equal(HttpStatusCode.OK, utcResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, offsetResponse.StatusCode);
        Assert.Equal(
            NormalizeData(await utcResponse.Content.ReadAsStringAsync()),
            NormalizeData(await offsetResponse.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// 其他合法偏移同样必须被正确处理（不止 <c>+08:00</c>）。
    /// 覆盖半小时偏移、负偏移与正负偏移边界。
    /// </summary>
    [SkippableTheory]
    // offset 起始与结束（同一 +offset），以及等价的 UTC 起始与结束。
    [InlineData("+05:30", "2026-09-26T18:30:00Z", "2026-09-27T18:30:00Z")] // 印度
    [InlineData("-08:00", "2026-09-27T08:00:00Z", "2026-09-28T08:00:00Z")] // 太平洋
    [InlineData("+14:00", "2026-09-26T10:00:00Z", "2026-09-27T10:00:00Z")] // 最大正偏移
    [InlineData("-11:00", "2026-09-27T11:00:00Z", "2026-09-28T11:00:00Z")] // 最小负偏移
    public async Task Layers_WithAnyLegalOffset_Returns200AndMatchesEquivalentUtc(
        string offset, string utcStart, string utcEnd)
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), $"offany_{Guid.NewGuid():N}"[..20]);
        var client = Authed(factory, token);

        var encodedOffset = Uri.EscapeDataString(offset);
        var offsetResponse = await client.GetAsync(
            $"/api/v1/calendar/layers?start=2026-09-27T00:00:00{encodedOffset}&end=2026-09-28T00:00:00{encodedOffset}&layers=all");
        var utcResponse = await client.GetAsync(
            $"/api/v1/calendar/layers?start={utcStart}&end={utcEnd}&layers=all");

        Assert.Equal(HttpStatusCode.OK, offsetResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, utcResponse.StatusCode);
        Assert.Equal(
            NormalizeData(await utcResponse.Content.ReadAsStringAsync()),
            NormalizeData(await offsetResponse.Content.ReadAsStringAsync()));
    }
}
