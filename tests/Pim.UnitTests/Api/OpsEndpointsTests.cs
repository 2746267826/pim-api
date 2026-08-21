using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Pim.Api.Infrastructure.Ops;
using Xunit;

namespace Pim.UnitTests.Api;

public class OpsEndpointsTests
{
    private static TestServer CreateServer(Dictionary<string, string?> config, Action<IApplicationBuilder>? configure = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(config!);
            })
            .ConfigureServices(services =>
            {
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseMiddleware<OpsKeyMiddleware>();
                if (configure != null)
                {
                    configure(app);
                }
                else
                {
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { ok = true });
                    });
                }
            });
        return new TestServer(builder);
    }

    [Fact]
    public async Task Ops_WithoutKey_Returns401()
    {
        using var server = CreateServer(new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Ops_WithValidKey_Succeeds()
    {
        using var server = CreateServer(new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "secret");
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ops_WithInvalidKey_Returns401()
    {
        using var server = CreateServer(new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "wrong");
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Ops_NoConfiguredKey_Returns503()
    {
        using var server = CreateServer(new Dictionary<string, string?>());
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "anything");
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Ops_CidrBlocked_Returns403()
    {
        using var server = CreateServer(new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret", ["PIM_OPS_ALLOWED_CIDRS"] = "10.0.0.0/8" });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "secret");
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        // TestServer RemoteIpAddress is null => IsIpAllowed(null) returns false when CIDR configured => 403
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task NonOpsPath_SkipsMiddleware()
    {
        using var server = CreateServer(new Dictionary<string, string?>());
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
