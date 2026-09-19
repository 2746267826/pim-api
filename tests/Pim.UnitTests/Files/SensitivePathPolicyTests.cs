using Xunit;
using Microsoft.Extensions.Configuration;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// SensitivePathPolicy 测试：默认规则、配置覆盖、大小写不敏感、段边界匹配。
/// </summary>
public class SensitivePathPolicyTests
{
    private static SensitivePathPolicy CreatePolicy(params string[] patterns)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:SensitivePathPatterns:0"] = patterns.FirstOrDefault(),
                ["Files:SensitivePathPatterns:1"] = patterns.Skip(1).FirstOrDefault(),
            })
            .Build();
        return new SensitivePathPolicy(patterns.Length > 0 ? config : null);
    }

    [Fact]
    public void DefaultPatterns_ProtectSecretsAndPasswords()
    {
        var policy = new SensitivePathPolicy(null);

        Assert.True(policy.IsProtected("/Secrets/合同.txt"));
        Assert.True(policy.IsProtected("/Passwords/db.json"));
        Assert.False(policy.IsProtected("/工作/报告.docx"));
    }

    [Fact]
    public void IsProtected_IsCaseInsensitive()
    {
        var policy = CreatePolicy("/Secrets/*");

        Assert.True(policy.IsProtected("/secrets/a.txt"));
        Assert.True(policy.IsProtected("/SECRETS/sub/b.txt"));
    }

    [Fact]
    public void IsProtected_MatchesSegmentBoundary_NotPrefixOfString()
    {
        var policy = CreatePolicy("/Secrets/*");

        Assert.True(policy.IsProtected("/Secrets"));
        Assert.False(policy.IsProtected("/SecretsApp/notes.txt"));
        Assert.False(policy.IsProtected("/MySecrets/a.txt"));
    }

    [Fact]
    public void IsProtected_NestedPaths_UnderProtectedRoot()
    {
        var policy = CreatePolicy("/Private/*");

        Assert.True(policy.IsProtected("/Private/a/b/c.txt"));
    }

    [Fact]
    public void RootPath_IsNeverProtected()
    {
        var policy = CreatePolicy("/Secrets/*");

        Assert.False(policy.IsProtected("/"));
        Assert.False(policy.IsProtected(null));
        Assert.False(policy.IsProtected(""));
    }

    [Fact]
    public void ConfigOverride_AddsPatterns()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:SensitivePathPatterns:0"] = "/Secrets/*",
                ["Files:SensitivePathPatterns:1"] = "/金库/*",
            })
            .Build();
        var policy = new SensitivePathPolicy(config);

        Assert.True(policy.IsProtected("/金库/保险单.pdf"));
        Assert.True(policy.IsProtected("/Secrets/a.txt"));
        Assert.False(policy.IsProtected("/Passwords/db.json")); // 覆盖模式替换默认
    }
}
