using System.Text;
using System.Text.Json;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// stdio-mode bearer token resolution, ported from the retired Python server
/// (<c>scripts/mcp/pim_mcp_server.py</c>): env vars first, then token files
/// (JSON <c>{accessToken,refreshToken}</c> or plain/two-line), with JWT-expiry
/// preference and mtime+size cached reads.
/// </summary>
public sealed class McpStdioTokenSource
{
    private static readonly string[] EnvTokenNames = { "PIM_ACCESS_TOKEN", "PIM_TOKEN", "MCP_BEARER_TOKEN", "BEARER_TOKEN", "PIM_JWT" };
    private static readonly string[] RefreshEnvNames = { "PIM_REFRESH_TOKEN", "PIM_REFRESH", "MCP_REFRESH_TOKEN" };
    private const long MaxTokenFileBytes = 32 * 1024;

    private readonly List<string> _fileCandidates;
    private readonly object _cacheLock = new();
    private string? _cachedPath;
    private long _cachedMtime;
    private long _cachedSize;
    private string? _cachedAccess;
    private string? _cachedRefresh;

    public McpStdioTokenSource(string? appBaseDirectory = null)
    {
        _fileCandidates = new List<string>();
        foreach (var envName in new[] { "PIM_TOKEN_FILE", "PIM_TOKEN_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
                _fileCandidates.Add(value.Trim());
        }

        var baseDir = appBaseDirectory ?? AppContext.BaseDirectory;
        _fileCandidates.Add(Path.Combine(baseDir, ".token"));
        _fileCandidates.Add(Path.Combine(baseDir, ".pim-token"));
        _fileCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pim", "token"));
        _fileCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pim", "token.json"));
        _fileCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "pim", "token"));
        _fileCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "pim", "token.json"));
    }

    /// <summary>Resolve the best available access token (non-expired preferred), or null.</summary>
    public string? GetAccessToken()
    {
        var envToken = FirstNonEmpty(EnvTokenNames).Map(StripBearer);
        var fileAccess = TryLoadTokenFile().Access;

        if (IsValid(envToken))
            return envToken;
        if (IsValid(fileAccess))
            return fileAccess;
        // Both expired or one missing: prefer file (external writer likely refreshed it), then env.
        return fileAccess ?? envToken;
    }

    /// <summary>Resolve a refresh token (env first when no file refresh present, otherwise file wins).</summary>
    public string? GetRefreshToken()
    {
        var fileRefresh = TryLoadTokenFile().Refresh;
        foreach (var envName in RefreshEnvNames)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var envRefresh = StripBearer(value);
                if (fileRefresh is not null && fileRefresh != envRefresh)
                    return fileRefresh;
                return envRefresh;
            }
        }
        return fileRefresh;
    }

    private static string? FirstNonEmpty(string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static string StripBearer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? trimmed[7..].Trim() : trimmed;
    }

    private static bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        return !IsExpired(token, leewaySeconds: 60);
    }

    private static bool IsExpired(string token, int leewaySeconds)
    {
        var exp = DecodeJwtExp(token);
        if (exp is null)
            return false; // non-JWT tokens are treated as not expired (Python parity)
        return exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + leewaySeconds;
    }

    private static long? DecodeJwtExp(string token)
    {
        try
        {
            var parts = token.Trim().Split('.');
            if (parts.Length < 2)
                return null;
            var payload = parts[1].Trim();
            payload += new string('=', (-payload.Length % 4) & 3);
            var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'))));
            if (json.RootElement.TryGetProperty("exp", out var exp))
            {
                if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var expLong))
                    return expLong;
                if (exp.ValueKind == JsonValueKind.String && long.TryParse(exp.GetString()?.Trim(), out var expStr))
                    return expStr;
            }
        }
        catch (Exception)
        {
            // Not a JWT — treat as opaque token (Python parity).
        }
        return null;
    }

    private (string? Access, string? Refresh) TryLoadTokenFile()
    {
        foreach (var candidate in _fileCandidates)
        {
            try
            {
                var file = new FileInfo(candidate);
                if (!file.Exists)
                    continue;
                if (file.Length > MaxTokenFileBytes)
                    continue;

                var mtime = file.LastWriteTimeUtc.Ticks;
                var size = file.Length;
                lock (_cacheLock)
                {
                    if (_cachedPath == candidate && _cachedMtime == mtime && _cachedSize == size && _cachedAccess is not null)
                        return (_cachedAccess, _cachedRefresh);
                }

                var (access, refresh) = ReadTokenFile(candidate);
                if (access is not null)
                {
                    // Re-stat after the read so a writer that swapped the file mid-read does not
                    // leave a stale cache entry (Python parity).
                    try
                    {
                        var after = new FileInfo(candidate);
                        mtime = after.LastWriteTimeUtc.Ticks;
                        size = after.Length;
                    }
                    catch (Exception)
                    {
                        // Keep the pre-read stat on stat failure.
                    }
                    lock (_cacheLock)
                    {
                        _cachedPath = candidate;
                        _cachedMtime = mtime;
                        _cachedSize = size;
                        _cachedAccess = access;
                        _cachedRefresh = refresh;
                    }
                    return (access, refresh);
                }
            }
            catch (Exception)
            {
                // Unreadable candidate — try the next one.
            }
        }
        return (null, null);
    }

    private static (string? Access, string? Refresh) ReadTokenFile(string path)
    {
        var raw = File.ReadAllText(path, Encoding.UTF8).Trim();
        if (raw.Length == 0)
            return (null, null);
        var stripped = raw.TrimStart('\uFEFF');

        if (stripped.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(stripped);
                var root = doc.RootElement;
                var (at, rt) = PickTokenPair(root);
                if (at is not null)
                    return (StripBearer(at), rt is null ? null : StripBearer(rt));
            }
            catch (Exception)
            {
                // JSON but not the recognized shape — fall through to plain parsing.
            }
        }

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length >= 2)
        {
            var first = StripBearer(lines[0]);
            if (first.Length > 0)
                return (first, StripBearer(lines[1]));
        }
        if (lines.Length == 1)
        {
            var single = StripBearer(lines[0]);
            return (single.Length > 0 ? single.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] : null, null);
        }
        return (null, null);
    }

    private static (string? Access, string? Refresh) PickTokenPair(JsonElement root)
    {
        var at = Pick(root, "accessToken", "access_token", "token");
        var rt = Pick(root, "refreshToken", "refresh_token");
        if (at is null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            at = Pick(data, "accessToken", "access_token", "token");
            rt = rt ?? Pick(data, "refreshToken", "refresh_token");
        }
        return (at, rt);
    }

    private static string? Pick(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }
        return null;
    }
}

internal static class StringExtensions
{
    public static string? Map(this string? value, Func<string, string> mapper)
        => value is null ? null : mapper(value);
}