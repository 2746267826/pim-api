using Xunit;

namespace Pim.UnitTests.Operations;

/// <summary>
/// #386 / REQ-1 · REQ-2 的回归保护：生产编排的密钥目录必须保持「最小权限挂载」形态 ——
/// JWT 私钥文件只读、数据保护密钥目录可写。整目录只读会让 DataProtection 在主密钥到期
/// （框架默认 90 天）时无法写入新密钥，进而让 Outlook / OneDrive 的令牌链路全线失效；
/// 整目录可写又会让私钥文件失去保护。README 里把「整体只读」写成交付形态的说法同样锁住。
/// </summary>
public class ProductionComposeKeyMountTests
{
    private const string JwtKeyMount = "- /data/keys/jwt_private.pem:/data/keys/jwt_private.pem:ro";
    private const string DataProtectionKeyMount = "- /data/keys/data-protection:/data/keys/data-protection";

    private static string RepoFile(params string[] relative)
        => File.ReadAllText(Path.Combine(["..", "..", "..", "..", "..", .. relative])).ReplaceLineEndings("\n");

    [Fact]
    public void ProdCompose_MountsJwtKeyReadOnlyAndDataProtectionKeysWritable()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        Assert.Contains(JwtKeyMount, compose);
        Assert.Contains(DataProtectionKeyMount, compose);
    }

    [Fact]
    public void ProdCompose_DoesNotMountTheWholeKeysDirectory()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        // 整目录只读 = #386 的缺陷形态；整目录可写 = 私钥失去保护。两者都不允许。
        Assert.DoesNotContain("- /data/keys:/data/keys:ro", compose);
        Assert.DoesNotContain("- /data/keys:/data/keys", compose);
    }

    [Fact]
    public void ProdCompose_KeyPathsMatchTheConfiguredPaths()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        Assert.Contains("Jwt__PrivateKeyPath=/data/keys/jwt_private.pem", compose);
        Assert.Contains("DataProtection__KeysPath=/data/keys/data-protection", compose);
    }

    [Fact]
    public void Readme_DoesNotDescribeTheKeysDirectoryAsMountedReadOnlyAsAWhole()
    {
        var lines = RepoFile("README.md")
            .Split('\n')
            .Where(line => line.Contains("/data/keys"));

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.DoesNotContain("只读挂载", line);
            Assert.DoesNotContain("挂载为只读", line);
        }

        Assert.DoesNotContain("/data/keys:ro", RepoFile("README.md"));
    }

    [Fact]
    public void DevCompose_KeepsItsDataProtectionKeysOnTheWritableVolume()
    {
        // REQ-1 的范围界定：开发编排的数据保护密钥目录在可写命名卷 pim_data 上，
        // 不存在 #386 的缺陷，本次不改动它。这条断言把这个前提钉住。
        var compose = RepoFile("docker-compose.yml");

        Assert.Contains("DataProtection__KeysPath=/data/data-protection", compose);
        Assert.Contains("pim_data:/data", compose);
    }
}
