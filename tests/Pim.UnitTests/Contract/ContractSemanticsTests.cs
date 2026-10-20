using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Contract;

using Pim.UnitTests.Api;

/// <summary>
/// 契约语义（docs/client-contract-semantics.md）必须体现在导出的 OpenAPI 文档里。
/// 与 CI 的「起 Test 环境取 swagger.json」等价，但用进程内宿主，测试里直接断言。
/// </summary>
public sealed class ContractSemanticsTests : IClassFixture<ContractSemanticsTests.Host>
{
    public sealed class Host : IClassFixture<Host>
    {
        internal readonly WebApplicationFactory<Program> Factory;

        public Host()
        {
            Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("contract-sem"));
        }
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

    [Theory]
    [InlineData("PcSummaryResponse", "/api/v1/pc/summary")]
    [InlineData("TodaySectionRegistryDto", "/api/v1/today/sections")]
    public async Task BusinessDayFields_AreDeclaredOnDailyEndpoints(string schemaName, string path)
    {
        var swagger = await SwaggerAsync();
        var schema = Schema(swagger, schemaName);
        var props = schema.GetProperty("properties");
        foreach (var field in new[] { "businessDate", "businessTimeZone", "businessDayStartHour" })
            Assert.True(props.TryGetProperty(field, out _), $"{schemaName} 缺少 {field}");
        Assert.True(swagger.GetProperty("paths").GetProperty(path).GetProperty("get").TryGetProperty("x-business-day", out _),
            $"{path} 缺少 x-business-day");
    }

    [Fact]
    public async Task IdempotencyKey_IsDeclaredOnWriteOperationsOnly()
    {
        var swagger = await SwaggerAsync();
        var tasks = swagger.GetProperty("paths").GetProperty("/api/v1/calendar/tasks");

        bool HasKey(JsonElement op) =>
            op.TryGetProperty("parameters", out var ps) && ps.EnumerateArray()
                .Any(p => p.TryGetProperty("name", out var n) && n.GetString() == "Idempotency-Key");

        Assert.True(HasKey(tasks.GetProperty("post")), "POST /tasks 缺少 Idempotency-Key");
        Assert.False(HasKey(tasks.GetProperty("get")), "GET /tasks 不应声明 Idempotency-Key");
        // 读操作（整份文档里的 GET）都不应声明
        foreach (var path in swagger.GetProperty("paths").EnumerateObject())
        {
            if (path.Value.TryGetProperty("get", out var getOp))
                Assert.False(HasKey(getOp), $"{path.Name} 的 GET 不应声明 Idempotency-Key");
        }
    }

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
