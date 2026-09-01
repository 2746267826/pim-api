using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Host wiring for the in-process MCP server (called from Program.cs after all
/// endpoints are mapped, before <c>app.Run()</c>):
/// <list type="bullet">
/// <item>captures the full pipeline for the in-process dispatcher (single process, no HTTP hop);</item>
/// <item>HTTP mode: bearer guard middleware (401 <c>40101</c> JSON), <c>/mcp/</c> 308 redirect, <c>MapMcp</c> Streamable HTTP endpoint;</item>
/// <item>stdio mode (<c>--mcp-stdio</c>): same tool registry over stdio for local Claude Code / Codex usage.</item>
/// </list>
/// </summary>
public static class McpServerBootstrap
{
    /// <summary>
    /// Maps the /mcp endpoint family when MCP is enabled and captures the in-process pipeline.
    /// Must be called after all endpoints are mapped and before <c>app.Run()</c>.
    /// </summary>
    public static void ConfigureHttp(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;

        if (options.Enabled)
        {
            var mcpPath = options.Path;

            // Bearer guard (Python _RequireBearer parity): any request on the MCP path without
            // an Authorization: Bearer header is rejected with 401 JSON — OPTIONS passes for CORS.
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var isMcpPath = string.Equals(path, mcpPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(mcpPath + "/", StringComparison.OrdinalIgnoreCase);
                if (isMcpPath
                    && !HttpMethods.IsOptions(context.Request.Method)
                    && !HasBearerHeader(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { code = 40101, message = "missing bearer token", data = (object?)null });
                    return;
                }
                await next();
            });

            // Trailing-slash normalization: 308 preserves method + body (301 would rewrite POST to GET).
            app.MapMethods(mcpPath + "/", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" },
                (HttpContext context) => Results.Redirect(mcpPath, permanent: true, preserveMethod: true));

            // Streamable HTTP endpoint: GET = SSE event stream, POST = JSON-RPC.
            app.MapMcp(mcpPath);
        }

        // Capture the final pipeline (includes /mcp endpoints above) for in-process dispatch.
        app.Services.GetRequiredService<McpInProcessDispatcher>()
            .Initialize(McpInProcessPipeline.BuildPipeline(app), app.Services);
    }

    /// <summary>
    /// Runs the MCP server over stdio for local clients (Claude Code / Codex mcp.json):
    /// <c>dotnet Pim.Api.dll --mcp-stdio</c>. Console output must stay protocol-clean,
    /// so the host logging sink is redirected to stderr before this runs.
    /// </summary>
    public static async Task RunStdioAsync(WebApplication app, CancellationToken ct = default)
    {
        app.Services.GetRequiredService<McpInProcessDispatcher>()
            .Initialize(McpInProcessPipeline.BuildPipeline(app), app.Services);

        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var serverOptions = McpServerFactory.CreateOptions();

        var transport = new StdioServerTransport(McpServerFactory.ServerName, loggerFactory);
        var server = McpServer.Create(transport, serverOptions, loggerFactory, app.Services);
        await server.RunAsync(ct);
    }

    private static bool HasBearerHeader(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        return auth.TrimStart().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }
}