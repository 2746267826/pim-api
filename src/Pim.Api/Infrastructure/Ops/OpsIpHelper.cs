namespace Pim.Api.Infrastructure.Ops;

public static class OpsIpHelper
{
    /// <summary>
    /// Unified client IP resolution for ops endpoints.
    /// Relies on UseForwardedHeaders (XForwardedFor|XForwardedProto) having already rewritten
    /// HttpContext.Connection.RemoteIpAddress to the real client IP. No manual X-Forwarded-For parsing.
    /// </summary>
    public static string GetClientIp(HttpContext ctx)
    {
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
