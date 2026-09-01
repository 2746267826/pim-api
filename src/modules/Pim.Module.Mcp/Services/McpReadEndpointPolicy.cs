using System.Text.RegularExpressions;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Endpoint policy for MCP-issued tokens bound to a READ tool. Read tokens may only call
/// read-only endpoints: any GET under the API, plus an explicit allowlist of read-semantic
/// POST endpoints (data-center query/preview, recycle-bin preview, schedule preview).
/// Every other method/path — including high-risk writes such as
/// <c>POST /api/v1/calendar/data-center/batch/execute</c> — is denied. This closes the gap
/// left by <see cref="McpWriteEndpointMap"/> which only knows the 50 MCP write endpoints.
/// </summary>
public static class McpReadEndpointPolicy
{
    private static readonly string[] ReadPostPatterns =
    {
        "^/api/v1/calendar/data-center/query$",
        "^/api/v1/calendar/data-center/batch/preview$",
        "^/api/v1/calendar/data-center/restore/preview$",
        "^/api/v1/calendar/recycle-bin/[^/]+/[^/]+/restore-preview$",
        "^/api/v1/calendar/schedule$", // shared read-preview / write-execute endpoint
    };

    private static readonly Regex[] ReadPostRegexes = ReadPostPatterns
        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToArray();

    public static bool IsReadAllowed(string method, string path)
    {
        var normalizedMethod = method.ToUpperInvariant();
        var normalizedPath = path.TrimEnd('/');
        if (normalizedMethod == "GET")
            return normalizedPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (normalizedMethod == "POST")
        {
            foreach (var pattern in ReadPostRegexes)
            {
                if (pattern.IsMatch(normalizedPath))
                    return true;
            }
        }
        return false;
    }
}
