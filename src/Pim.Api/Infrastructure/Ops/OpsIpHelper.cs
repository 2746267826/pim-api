namespace Pim.Api.Infrastructure.Ops;

public static class OpsIpHelper
{
    /// <summary>
    /// Unified client IP resolution for ops endpoints.
    /// Trusts reverse proxy (e.g., nginx/haproxy) when present: prefers X-Forwarded-For first entry,
    /// falls back to RemoteIpAddress. This is consistent across CIDR check, rate limiting and audit.
    /// When not behind a trusted proxy, X-Forwarded-For can be spoofed; deploy Pim behind 127.0.0.1 bound
    /// reverse proxy or set ForwardedHeaders middleware.
    /// </summary>
    public static string GetClientIp(HttpContext ctx)
    {
        var xff = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
