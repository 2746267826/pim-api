using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// 反向代理信任边界。默认只信任本机回环；部署时用
/// <c>ReverseProxy:KnownProxies</c> 或 <c>PIM_TRUSTED_PROXIES</c> 填实际代理 IP/CIDR。
/// 未显式配置时，额外信任 <c>PIM_NETWORK_SUBNET</c> 推导出的 compose 网关地址，
/// 这样改网段不会留下旧网关并让所有反代请求失去信任。
/// 未列出的来源发来的 X-Forwarded-Proto 会被框架忽略。
/// </summary>
public static class ForwardedHeadersConfigurator
{
    public const string ConfigurationKey = "ReverseProxy:KnownProxies";
    public const string EnvironmentVariable = "PIM_TRUSTED_PROXIES";
    public const string NetworkSubnetEnvironmentVariable = "PIM_NETWORK_SUBNET";
    private const string DefaultTrustedProxies = "127.0.0.1/32,::1/128";

    public static ForwardedHeadersOptions Create(string? configured, string? networkSubnet = null)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        var explicitConfiguration = !string.IsNullOrWhiteSpace(configured);
        var raw = explicitConfiguration ? configured : DefaultTrustedProxies;
        foreach (var token in raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(token, out var address))
            {
                options.KnownProxies.Add(address);
                continue;
            }

            var slash = token.IndexOf('/');
            if (slash <= 0
                || !IPAddress.TryParse(token[..slash], out var networkAddress)
                || !int.TryParse(token[(slash + 1)..], out var prefixLength)
                || prefixLength < 0
                || prefixLength > (networkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            {
                throw new InvalidOperationException(
                    $"可信代理配置项无效：{token}。请填写 IP 或 CIDR，例如 127.0.0.1/32 或 172.31.240.0/24。");
            }

            if (prefixLength == 0)
            {
                throw new InvalidOperationException(
                    $"可信代理配置不能使用默认路由（{token}）；请填写实际反向代理 IP 或最小 CIDR。");
            }

            options.KnownNetworks.Add(
                new Microsoft.AspNetCore.HttpOverrides.IPNetwork(networkAddress, prefixLength));
        }

        if (!explicitConfiguration)
        {
            var gateway = DeriveGatewayAddress(networkSubnet);
            if (gateway is not null && !options.KnownProxies.Contains(gateway))
            {
                options.KnownProxies.Add(gateway);
            }
        }

        if (options.KnownProxies.Count == 0 && options.KnownNetworks.Count == 0)
        {
            throw new InvalidOperationException("可信代理列表不能为空。");
        }

        return options;
    }

    /// <summary>
    /// 从 compose 的固定网段推导默认网关（网络地址 + 1）。Docker 自定义 bridge
    /// 未显式指定 gateway 时使用该地址；它也是宿主机反代经端口映射进入容器时的来源。
    /// 无法解析/未配置时返回 null，保持只信任回环。
    /// </summary>
    public static IPAddress? DeriveGatewayAddress(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return null;
        }

        var token = cidr.Trim();
        var slash = token.IndexOf('/');
        if (slash <= 0
            || !IPAddress.TryParse(token[..slash], out var address)
            || !int.TryParse(token[(slash + 1)..], out var prefixLength))
        {
            throw new InvalidOperationException(
                $"{NetworkSubnetEnvironmentVariable} 必须是 CIDR，例如 172.31.240.0/24，当前值为：{cidr}");
        }

        var bytes = address.GetAddressBytes();
        var maxPrefix = bytes.Length * 8;
        if (prefixLength <= 0 || prefixLength > maxPrefix)
        {
            throw new InvalidOperationException(
                $"{NetworkSubnetEnvironmentVariable} 前缀长度无效（{cidr}）；必须是 1-{maxPrefix}。");
        }

        var network = new byte[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            var remainingBits = prefixLength - index * 8;
            var mask = remainingBits >= 8
                ? 0xFF
                : remainingBits <= 0
                    ? 0
                    : 0xFF << (8 - remainingBits) & 0xFF;
            network[index] = (byte)(bytes[index] & mask);
        }

        if (prefixLength < maxPrefix)
        {
            for (var index = network.Length - 1; index >= 0; index--)
            {
                if (++network[index] != 0)
                {
                    break;
                }
            }
        }

        return new IPAddress(network);
    }
}
