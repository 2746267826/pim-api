using System.Net;
using Microsoft.AspNetCore.Http;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// Q04 明文传输判定。公网只允许 HTTPS；仅健康检查、版本探测和 ACME 挑战这类
/// 不含用户数据的路径保留明文，且本机回环访问继续放行以支持本地开发与容器探活。
/// </summary>
public static class TransportSecurityPolicy
{
    private static readonly string[] ExemptPaths =
    [
        "/health",
        "/health/live",
        "/health/ready",
        "/api/version",
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
