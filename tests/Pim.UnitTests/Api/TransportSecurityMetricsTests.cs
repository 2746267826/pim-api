using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Infrastructure.Http;
using Pim.Api.Infrastructure.Ops;
using Prometheus;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 生产门禁与 /metrics 的集成回归：真实传输中间件 + 真实 OpsKey 过滤器。
/// 生产 Prometheus 在 pim-net 内以 HTTP 抓取 /metrics，不能因为它没有 TLS
/// 或伪造转发头就整条指标链路 426；但无 OpsKey 仍必须 401，数据路径仍必须 426。
/// </summary>
public class TransportSecurityMetricsTests
{
    [Fact]
    public async Task ProductionGate_AllowsMetricsOnlyWithOpsKey_AndRejectsCleartextData()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var noKey = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        var wrongKey = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        wrongKey.Headers.Add("X-PIM-Ops-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongKey)).StatusCode);

        var withKey = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        withKey.Headers.Add("X-PIM-Ops-Key", "test-ops-key");
        var metrics = await client.SendAsync(withKey);
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Equal(
            TransportSecurityMiddleware.CleartextValue,
            metrics.Headers.GetValues(TransportSecurityMiddleware.TransportHeaderName).Single());

        var data = await client.GetAsync("/api/v1/native-notes");
        Assert.Equal(HttpStatusCode.UpgradeRequired, data.StatusCode);
        Assert.Equal(
            TransportSecurityMiddleware.HttpsRequiredValue,
            data.Headers.GetValues(TransportSecurityMiddleware.TransportHeaderName).Single());
        var body = await data.Content.ReadAsStringAsync();
        Assert.Contains("42600", body);

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyHttpsHeader_UnlocksDataPathWithoutLoopbackCleartext()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/native-notes");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginDataPathIsRejectedOverCleartext()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "probe", password = "not-a-real-password" });

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    private static TestServer CreateServer()
    {
        var builder = new WebHostBuilder()
            .UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PIM_OPS_KEY"] = "test-ops-key",
                });
            })
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                // TestServer 默认没有 RemoteIpAddress；固定成回环，模拟宿主机反代/探针。
                app.Use(async (context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    await next(context);
                });
                app.UseForwardedHeaders(ForwardedHeadersConfigurator.Create("127.0.0.1/32,::1/128"));
                app.UseMiddleware<TransportSecurityMiddleware>(
                    new TransportSecurityOptions
                    {
                        EnforceHttps = true,
                        AllowLoopbackCleartext = false,
                        HstsMaxAgeSeconds = TransportSecurityOptions.DefaultHstsMaxAgeSeconds,
                    });
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
                    endpoints.MapGet("/api/v1/native-notes", () => Results.Ok(new { notes = Array.Empty<string>() }));
                    endpoints.MapPost("/api/v1/auth/login", () => Results.Ok(new { ok = true }));
                    endpoints.MapMetrics("/metrics")
                        .AddEndpointFilter(MetricsAuthorizationFilter.InvokeAsync);
                });
            });

        return new TestServer(builder);
    }
}
