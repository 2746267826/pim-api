using Xunit;

namespace Pim.UnitTests.Operations;

/// <summary>
/// Q04 生产传输门禁的配置回归：生产 compose 必须默认强制 HTTPS，同时
/// Prometheus 的 HTTP 抓取必须仍然可用；可信代理默认值必须跟随 PIM_NETWORK_SUBNET，
/// 否则改网段后所有反代请求会因旧网关地址失去信任而统一 426。
/// </summary>
public class ProductionTransportConfigTests
{
    private static string RepoFile(params string[] relative)
        => File.ReadAllText(Path.Combine(["..", "..", "..", "..", "..", .. relative])).ReplaceLineEndings("\n");

    [Fact]
    public void ProdCompose_DefaultsToEnforcedHttpsAndRejectsLoopbackCleartext()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        Assert.Contains("PIM_REQUIRE_HTTPS=${PIM_REQUIRE_HTTPS:-true}", compose);
        Assert.Contains(
            "PIM_ALLOW_LOOPBACK_CLEARTEXT=${PIM_ALLOW_LOOPBACK_CLEARTEXT:-false}",
            compose);
    }

    [Fact]
    public void ProdCompose_PassesSubnetForGatewayDerivation()
    {
        var compose = RepoFile("docker-compose.prod.yml");

        Assert.Contains("- PIM_NETWORK_SUBNET=${PIM_NETWORK_SUBNET:-172.31.240.0/24}", compose);
        Assert.Contains("- PIM_TRUSTED_PROXIES=${PIM_TRUSTED_PROXIES:-}", compose);
        // 旧实现把网关写死为 172.31.240.1/32；改 PIM_NETWORK_SUBNET 后它就失效了。
        Assert.DoesNotContain("PIM_TRUSTED_PROXIES=${PIM_TRUSTED_PROXIES:-127.0.0.1/32,::1/128,172.31.240.1/32}", compose);
    }

    [Fact]
    public void ProdEnvExample_DocumentsTheSubnetTrustLinkage()
    {
        var env = RepoFile(".env.prod.example");

        Assert.Contains("PIM_REQUIRE_HTTPS=true", env);
        Assert.Contains("PIM_ALLOW_LOOPBACK_CLEARTEXT=false", env);
        Assert.Contains("留空时 API 默认信任", env);
        Assert.Contains("修改网段只需改 PIM_NETWORK_SUBNET", env);
    }

    [Fact]
    public void Prometheus_ProdScrapesMetricsOverHttpWithOpsKey()
    {
        var prometheus = RepoFile("deploy", "prometheus", "prometheus.prod.yml");

        Assert.Contains("metrics_path: /metrics", prometheus);
        Assert.Contains("scheme: http", prometheus);
        Assert.Contains("bearer_token_file: /etc/prometheus/ops_token", prometheus);
        Assert.Contains("targets:", prometheus);
        Assert.Contains("- pim:5000", prometheus);
    }

    [Fact]
    public void Readme_DocumentsThe426ContractAndMetricsExemption()
    {
        var readme = RepoFile("README.md");

        Assert.Contains("/metrics", readme);
        Assert.Contains("42600", readme);
        Assert.Contains("X-Pim-Transport: https-required", readme);
        Assert.Contains("OpsKey", readme);
        Assert.Contains("PIM_NETWORK_SUBNET", readme);
        Assert.Contains("verify-transport-gate.sh", readme);
    }
}
