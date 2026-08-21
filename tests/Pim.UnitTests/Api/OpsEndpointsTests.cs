using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Pim.Api.Endpoints;
using Pim.Api.Infrastructure.Ops;
using Pim.Api.Middleware;
using Pim.Api.Services;
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
    public async Task Ops_WithValidKey_AlwaysSucceeds_NoCidrCheck()
    {
        // CIDR 已移除，无论 RemoteIpAddress 如何均不做 403
        using var server = CreateServer(new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "secret");
        var resp = await client.GetAsync("/api/v1/ops/logs/files");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task NonOpsPath_SkipsMiddleware()
    {
        using var server = CreateServer(new Dictionary<string, string?>());
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static TestServer CreateLogsServer(string logDir, Dictionary<string, string?> config)
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config!))
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(new OpsLogsService(logDir));
                services.AddLogging();
            })
            .Configure(app =>
            {
                app.UseMiddleware<ExceptionMiddleware>();
                app.UseMiddleware<OpsKeyMiddleware>();
                app.UseRouting();
                app.UseEndpoints(e => e.MapOpsLogsEndpoints());
            });
        return new TestServer(builder);
    }

    [Fact]
    public async Task OpsLogs_WithoutKey_Returns401()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ops-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            using var server = CreateLogsServer(tmp, new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
            var resp = await server.CreateClient().GetAsync("/api/v1/ops/logs/files");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task OpsLogs_WithKey_Succeeds()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ops-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "pim-api-20260821.jsonl"), """{"@t":"2026-08-21T00:00:00Z","@m":"hi"}""");
            using var server = CreateLogsServer(tmp, new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
            var c = server.CreateClient();
            c.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "secret");
            var resp = await c.GetAsync("/api/v1/ops/logs/files");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var tailResp = await c.GetAsync("/api/v1/ops/logs/tail?file=pim-api-20260821.jsonl&lines=10");
            Assert.Equal(HttpStatusCode.OK, tailResp.StatusCode);
            var queryResp = await c.GetAsync("/api/v1/ops/logs/query?file=pim-api-20260821.jsonl&limit=10");
            Assert.Equal(HttpStatusCode.OK, queryResp.StatusCode);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task OpsLogs_Truncated_Returns206()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ops-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, "pim-api-20260821.jsonl");
            var big = new string('a', 11 * 1024);
            var lines = Enumerable.Range(0, 6000).Select(i => $"{{\"@t\":\"2026-08-21T00:00:00Z\",\"@m\":\"{big}{i}\"}}");
            await File.WriteAllLinesAsync(file, lines);
            using var server = CreateLogsServer(tmp, new Dictionary<string, string?> { ["PIM_OPS_KEY"] = "secret" });
            var c = server.CreateClient();
            c.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "secret");
            var resp = await c.GetAsync("/api/v1/ops/logs/tail?file=pim-api-20260821.jsonl&lines=500");
            Assert.Equal((HttpStatusCode)206, resp.StatusCode);
            Assert.True(resp.Headers.Contains("X-Truncated") || resp.Headers.Contains("x-truncated"));
        }
        finally { Directory.Delete(tmp, true); }
    }
}
