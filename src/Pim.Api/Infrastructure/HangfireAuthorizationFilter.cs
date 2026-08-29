using System.Security.Cryptography;
using System.Text;
using Hangfire.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pim.Api.Infrastructure;

public sealed class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        // Primary: require authenticated admin role (consistent with AiEndpoints RequireAuthorization(Roles="admin"))
        if (http.User.Identity?.IsAuthenticated == true && http.User.IsInRole("admin"))
            return true;

        // Fallback 1: OpsKey header (X-PIM-Ops-Key) for ops tooling
        try
        {
            var cfg = http.RequestServices.GetService<IConfiguration>();
            if (cfg != null)
            {
                var opsKey = cfg["PIM_OPS_KEY"] ?? cfg["Ops:Key"];
                if (!string.IsNullOrEmpty(opsKey))
                {
                    var headerKey = http.Request.Headers["X-PIM-Ops-Key"].FirstOrDefault()
                        ?? http.Request.Headers["X-Ops-Key"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(headerKey) && FixedTimeEquals(headerKey, opsKey))
                        return true;
                }

                // Fallback 2: BasicAuth via Hangfire:Username / Hangfire:Password
                var user = cfg["Hangfire:Username"];
                var pass = cfg["Hangfire:Password"];
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                {
                    var auth = http.Request.Headers.Authorization.FirstOrDefault();
                    if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var encoded = auth.Substring(6).Trim();
                            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                            var sep = decoded.IndexOf(':');
                            if (sep >= 0)
                            {
                                var u = decoded.Substring(0, sep);
                                var p = decoded.Substring(sep + 1);
                                if (FixedTimeEquals(u, user) && FixedTimeEquals(p, pass))
                                    return true;
                            }
                        }
                        catch { /* invalid base64 -> deny */ }
                    }
                }
            }
        }
        catch { /* config unavailable -> deny */ }

        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
