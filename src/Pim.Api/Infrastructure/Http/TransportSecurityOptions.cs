using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// Q04 传输安全门禁配置。
/// 默认在非 Development/Test 环境强制 HTTPS，且不再放行回环明文数据请求；
/// 健康检查、版本与 ACME 路径由策略单独豁免。本地开发需显式开启回环例外。
/// 部署可用 PIM_REQUIRE_HTTPS / PIM_HSTS_MAX_AGE_SECONDS /
/// PIM_ALLOW_LOOPBACK_CLEARTEXT 覆盖。
/// </summary>
public sealed class TransportSecurityOptions
{
    public const string SectionName = "TransportSecurity";
    public const string RequireHttpsEnvironmentVariable = "PIM_REQUIRE_HTTPS";
    public const string HstsMaxAgeEnvironmentVariable = "PIM_HSTS_MAX_AGE_SECONDS";
    public const string AllowLoopbackCleartextEnvironmentVariable = "PIM_ALLOW_LOOPBACK_CLEARTEXT";
    public const int DefaultHstsMaxAgeSeconds = 31_536_000;

    public bool EnforceHttps { get; init; }

    public bool AllowLoopbackCleartext { get; init; }

    public int HstsMaxAgeSeconds { get; init; }

    public bool EnableHsts => HstsMaxAgeSeconds > 0;

    public static TransportSecurityOptions FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var productionLike = !environment.IsDevelopment() && !environment.IsEnvironment("Test");
        var enforceRaw = configuration[$"{SectionName}:EnforceHttps"]
            ?? Environment.GetEnvironmentVariable(RequireHttpsEnvironmentVariable);
        var hstsRaw = configuration[$"{SectionName}:HstsMaxAgeSeconds"]
            ?? Environment.GetEnvironmentVariable(HstsMaxAgeEnvironmentVariable);
        var loopbackRaw = configuration[$"{SectionName}:AllowLoopbackCleartext"]
            ?? Environment.GetEnvironmentVariable(AllowLoopbackCleartextEnvironmentVariable);

        var enforceHttps = ParseBool(enforceRaw, RequireHttpsEnvironmentVariable) ?? productionLike;
        var allowLoopback = ParseBool(loopbackRaw, AllowLoopbackCleartextEnvironmentVariable)
            ?? !productionLike;
        var hstsMaxAge = ParseInt(hstsRaw, HstsMaxAgeEnvironmentVariable)
            ?? (productionLike ? DefaultHstsMaxAgeSeconds : 0);

        if (hstsMaxAge < 0)
        {
            throw new InvalidOperationException(
                $"{HstsMaxAgeEnvironmentVariable} 不能为负数；0 表示关闭 HSTS。");
        }

        return new TransportSecurityOptions
        {
            EnforceHttps = enforceHttps,
            AllowLoopbackCleartext = allowLoopback,
            HstsMaxAgeSeconds = hstsMaxAge,
        };
    }

    private static bool? ParseBool(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return bool.TryParse(raw.Trim(), out var value)
            ? value
            : throw new InvalidOperationException($"{name} 必须是 true 或 false，当前值为：{raw}");
    }

    private static int? ParseInt(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw.Trim(), out var value)
            ? value
            : throw new InvalidOperationException($"{name} 必须是整数秒，当前值为：{raw}");
    }
}
