using Pim.Core.Common;
using Pim.Module.Mcp.Services;

namespace Pim.Api.Middleware;

/// <summary>
/// Enforces MCP tool scoping on REST requests. Any JWT that carries an <c>mcp_tool</c> claim
/// (issued by <c>POST /api/v1/mcp/verify</c>) may only call the REST endpoint of that tool:
/// - write tools: the exact mapped endpoint (method + path);
/// - read tools: any non-write endpoint.
/// This prevents a verified read-tool token from being reused against write endpoints.
/// </summary>
public sealed class McpScopedTokenMiddleware
{
    private readonly RequestDelegate _next;

    public McpScopedTokenMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tool = context.User?.FindFirst("mcp_tool")?.Value;
        if (tool is not null)
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? string.Empty;
            var allowed = McpToolCatalog.IsWrite(tool)
                ? McpWriteEndpointMap.IsAllowedForTool(tool, method, path)
                : !McpWriteEndpointMap.IsWriteEndpoint(method, path);
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponse<string>.Error(40302, $"mcp scope denied: {tool} cannot call {method} {path}"));
                return;
            }
        }

        await _next(context);
    }
}
