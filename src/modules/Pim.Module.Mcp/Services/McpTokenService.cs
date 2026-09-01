using System.Security.Cryptography;
using System.Text;

namespace Pim.Module.Mcp.Services;

public static class McpTokenService
{
    /// <summary>URL-safe token prefix.</summary>
    public const string Prefix = "pim_mcp_";

    /// <summary>Random payload length in bytes (=> 48 hex chars).</summary>
    private const int RandomBytes = 24;

    /// <summary>Generates a fresh raw token: <c>pim_mcp_</c> + 48 URL-safe hex chars.</summary>
    public static string GenerateToken()
    {
        var bytes = new byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>SHA-256 hex digest. The only form stored in the database.</summary>
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Short display prefix (first 12 chars).</summary>
    public static string TokenPrefix(string token)
        => token.Length <= 12 ? token : token[..12];
}
