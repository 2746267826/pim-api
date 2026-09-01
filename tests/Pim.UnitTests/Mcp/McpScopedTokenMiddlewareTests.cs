using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pim.Api.Middleware;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpScopedTokenMiddlewareTests
{
    private static async Task<int> RunAsync(string? tool, string method, string path)
    {
        var context = new DefaultHttpContext();
        if (tool is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("mcp_tool", tool) }, "test"));
        }
        context.Request.Method = method;
        context.Request.Path = path;
        var middleware = new McpScopedTokenMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
        });
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task NoMcpClaim_PassesThrough()
    {
        Assert.Equal(200, await RunAsync(null, "POST", "/api/v1/calendar/tasks"));
    }

    [Fact]
    public async Task ReadToken_AllowsGet()
    {
        Assert.Equal(200, await RunAsync("get_tasks", "GET", "/api/v1/calendar/tasks"));
        Assert.Equal(200, await RunAsync("get_tasks", "GET", "/api/version"));
    }

    [Fact]
    public async Task ReadToken_AllowsReadSemanticPosts()
    {
        Assert.Equal(200, await RunAsync("get_tasks", "POST", "/api/v1/calendar/data-center/query"));
        Assert.Equal(200, await RunAsync("get_tasks", "POST", "/api/v1/calendar/schedule"));
    }

    [Fact]
    public async Task ReadToken_DeniesUnmappedHighRiskWrite()
    {
        // Not in the 50-tool map, but must still be denied for read tokens.
        Assert.Equal(403, await RunAsync("get_tasks", "POST", "/api/v1/calendar/data-center/batch/execute"));
    }

    [Fact]
    public async Task ReadToken_DeniesWriteEndpoints()
    {
        Assert.Equal(403, await RunAsync("get_tasks", "POST", "/api/v1/calendar/tasks"));
        Assert.Equal(403, await RunAsync("get_tasks", "DELETE", "/api/v1/calendar/tasks/x"));
        Assert.Equal(403, await RunAsync("get_tasks", "POST", "/api/v1/quick-notes"));
    }

    [Fact]
    public async Task WriteToken_AllowedOnlyOnOwnEndpoint()
    {
        Assert.Equal(200, await RunAsync("create_task", "POST", "/api/v1/calendar/tasks"));
        Assert.Equal(403, await RunAsync("create_task", "POST", "/api/v1/quick-notes"));
        Assert.Equal(403, await RunAsync("create_task", "DELETE", "/api/v1/calendar/tasks/x"));
        Assert.Equal(200, await RunAsync("delete_event", "DELETE", "/api/v1/calendar/events/x"));
    }

    [Fact]
    public async Task UnknownTool_IsDeniedExplicitly()
    {
        // Forged/unknown tool names must not fall back to the read policy.
        Assert.Equal(403, await RunAsync("bogus_tool", "GET", "/api/v1/calendar/tasks"));
        Assert.Equal(403, await RunAsync("bogus_tool", "POST", "/api/v1/calendar/tasks"));
    }

    [Fact]
    public async Task ReadToken_AllowsRootHealth()
    {
        Assert.Equal(200, await RunAsync("get_system_health", "GET", "/health"));
    }

    [Fact]
    public async Task WriteToken_TrailingSlash_IsNormalized()
    {
        Assert.Equal(200, await RunAsync("create_task", "POST", "/api/v1/calendar/tasks/"));
    }
}
