using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// 反向代理信任边界。默认只信任本机回环；部署时用
/// <c>ReverseProxy:KnownProxies</c> 或 <c>PIM_TRUSTED_PROXIES</c> 填实际代理 IP/CIDR。
/// 未列出的来源发来的 X-Forwarded-Proto 会被框架忽略。
/// </summary>
public static class ForwardedHeadersConfigurator
{
    public const string ConfigurationKey = "ReverseProxy:KnownProxies";
    public const string EnvironmentVariable = "PIM_TRUSTED_PROXIES";
    private const string DefaultTrustedProxies = "127.0.0.1/32,::1/128";

    public static ForwardedHeadersOptions Create(string? configured)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        var raw = string.IsNullOrWhiteSpace(configured) ? DefaultTrustedProxies : configured;
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

            options.KnownNetworks.Add(new IPNetwork(networkAddress, prefixLength));
        }

        if (options.KnownProxies.Count == 0 && options.KnownNetworks.Count == 0)
        {
            throw new InvalidOperationException("可信代理列表不能为空。");
        }

        return options;
    }
}
