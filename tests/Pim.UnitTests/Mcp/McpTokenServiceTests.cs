using System.Linq;
using System.Security.Cryptography;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpTokenServiceTests
{
    [Fact]
    public void Generate_ProducesPrefixedToken()
    {
        var token = McpTokenService.GenerateToken();
        Assert.StartsWith("pim_mcp_", token);
        Assert.Equal("pim_mcp_".Length + 48, token.Length);
    }

    [Fact]
    public void Generate_TokensAreUnique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => McpTokenService.GenerateToken()).ToList();
        Assert.Equal(100, tokens.Distinct().Count());
    }

    [Fact]
    public void Hash_IsStableSha256Hex()
    {
        var a = McpTokenService.HashToken("pim_mcp_abc");
        var b = McpTokenService.HashToken("pim_mcp_abc");
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, McpTokenService.HashToken("pim_mcp_abd"));
    }

    [Fact]
    public void Prefix_IsFirst12Chars()
    {
        Assert.Equal("pim_mcp_ab12", McpTokenService.TokenPrefix("pim_mcp_ab12cd34ef56"));
    }

    [Fact]
    public void Token_IsUrlSafe()
    {
        var token = McpTokenService.GenerateToken();
        Assert.StartsWith("pim_mcp_", token);
        var payload = token["pim_mcp_".Length..];
        Assert.All(payload, c => Assert.Contains(c, "0123456789abcdef"));
    }
}
