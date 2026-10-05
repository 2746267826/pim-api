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

        // 整行相等（trim 后按行比对）：单纯的 `Contains` 允许在数据保护目录那行尾部再加一个
        // `:ro`（那正是 #386 在新挂载形态下的回归），所以这里按「整行」而非「子串」判定。
        var lines = compose.Split('\n').Select(line => line.Trim()).ToList();
        Assert.Contains(JwtKeyMount, lines);
        Assert.Contains(DataProtectionKeyMount, lines);
        Assert.DoesNotContain($"{DataProtectionKeyMount}:ro", lines);
    }

    [Fact]
    public void ProdCompose_DoesNotMountTheWholeKeysDirectory()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        // 整目录只读 = #386 的缺陷形态；整目录可写 = 私钥失去保护。两者都不允许，
        // 也不允许「两条具体挂载之外再追加一条整目录挂载」—— 因此这里按**前缀子串**判定，
        // 后缀是 `:ro`、`:rw` 还是没有后缀都要拦下。
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
        var readme = RepoFile("README.md");

        // AC-2.1 点名的变体：只对含 `/data/keys` 的行做检查（`挂载为只读` 是原「已知限制」引用块的原文）。
        foreach (var line in readme.Split('\n').Where(line => line.Contains("/data/keys")))
        {
            Assert.DoesNotContain("挂载为只读", line);
            Assert.DoesNotContain("只读挂载", line);
        }

        Assert.DoesNotContain("/data/keys:ro", readme);
        Assert.DoesNotContain("已知限制", readme);
    }

    [Fact]
    public void Readme_LocksTheTwoMissingKeyPathConsequences()
    {
        // 锁住**要守的那两句话**，而不是对全部含路径的行禁用普通说法：
        // 私钥缺失 → 健康检查持续失败；数据保护目录缺失 → 容器仍 healthy，但已有密文解不开。
        var readme = RepoFile("README.md");

        Assert.Contains("私钥文件缺失", readme);
        Assert.Contains("健康检查会持续失败", readme);
        Assert.Contains("数据保护密钥目录缺失", readme);
        Assert.Contains("key-*.xml", readme);
        Assert.Contains("密文因此解不开", readme);
        // 缺失私钥的补救必须点名命名卷里的同路径，否则按文档操作恢复不了（第二十轮 review 的实测）
        Assert.Contains("_pim_data", readme);
        Assert.Contains("--force-recreate", readme);
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
