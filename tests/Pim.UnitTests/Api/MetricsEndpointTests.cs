using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// /metrics 端点鉴权：匿名与错误密钥 401；OpsKey（X-PIM-Ops-Key 或 Bearer）放行；
/// 未配置 OpsKey 时匿名一律 401（指标不公开）。
/// </summary>
public class MetricsEndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory(bool withOpsKey)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            if (withOpsKey) b.UseSetting("PIM_OPS_KEY", "test-ops-key");
        });

    [Fact]
    public async Task Metrics_Anonymous_Returns401()
    {
        using var factory = CreateFactory(withOpsKey: true);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_WrongOpsKey_Returns401()
    {
        using var factory = CreateFactory(withOpsKey: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_ValidOpsKeyHeader_Returns200WithPrometheusText()
    {
        using var factory = CreateFactory(withOpsKey: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "test-ops-key");

        var resp = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("# HELP", body);
    }

    [Fact]
    public async Task Metrics_ValidBearerToken_Returns200()
    {
        using var factory = CreateFactory(withOpsKey: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-ops-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_NoOpsKeyConfigured_AnonymousReturns401()
    {
        using var factory = CreateFactory(withOpsKey: false);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task HealthLive_Anonymous_Returns200()
    {
        using var factory = CreateFactory(withOpsKey: false);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
