using System.Net;
using Microsoft.AspNetCore.Http;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// Q04 明文传输判定。公网只允许 HTTPS；仅健康检查、版本探测、ACME 挑战和
/// 受 OpsKey/admin 鉴权保护的 Prometheus 指标这类不含用户数据的路径保留明文。
/// 本机回环访问是否放行由 <see cref="TransportSecurityOptions.AllowLoopbackCleartext"/> 决定。
/// </summary>
public static class TransportSecurityPolicy
{
    private static readonly string[] ExemptPaths =
    [
        "/health",
        "/health/live",
        "/health/ready",
        "/api/version",
        // Prometheus 在 pim-net 内直接以 HTTP 抓取；该端点自身强制 OpsKey/admin 鉴权，
        // 不承载用户数据。不要在这里加入登录、笔记、文件等数据路径。
        "/metrics",
    ];

    public static bool IsExemptPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var normalized = value.TrimEnd('/');
        if (normalized.Length == 0)
        {
            normalized = "/";
        }

        normalized = normalized.ToLowerInvariant();
        if (normalized.StartsWith("/.well-known/acme-challenge/", StringComparison.Ordinal))
        {
            return true;
        }

        return ExemptPaths.Contains(normalized, StringComparer.Ordinal);
    }

    public static bool IsLoopback(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        return IPAddress.IsLoopback(remoteAddress);
    }

    public static bool ShouldRejectCleartext(
        TransportSecurityOptions options,
        HttpContext context)
    {
        if (!options.EnforceHttps || context.Request.IsHttps)
        {
            return false;
        }

        if (IsExemptPath(context.Request.Path))
        {
            return false;
        }

        if (options.AllowLoopbackCleartext && IsLoopback(context.Connection.RemoteIpAddress))
        {
            return false;
        }

        return true;
    }
}
