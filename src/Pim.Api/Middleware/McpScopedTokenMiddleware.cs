using Pim.Core.Common;
using Pim.Module.Mcp.Services;

namespace Pim.Api.Middleware;

/// <summary>
/// Enforces MCP tool scoping on REST requests. Any JWT that carries an <c>mcp_tool</c> claim
/// (issued by <c>POST /api/v1/mcp/verify</c>) may only call endpoints permitted for that tool:
/// - write tools: the exact mapped endpoint (method + path);
/// - read tools: any GET under /api/* plus an allowlist of read-semantic POST endpoints.
/// This prevents a verified read-tool token from being reused against write endpoints
/// (including unmapped high-risk ones such as data-center/batch/execute).
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
                : McpReadEndpointPolicy.IsReadAllowed(method, path);
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
