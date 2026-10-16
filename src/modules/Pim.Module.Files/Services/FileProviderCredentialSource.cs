using Microsoft.Extensions.Configuration;

namespace Pim.Module.Files.Services;

/// <summary>
/// provider 的服务端凭据来源（工单 05 / F4.2）。
///
/// 硬性约束：
/// <list type="bullet">
/// <item>client secret **不落库**（<c>file_providers</c> 里只有 <c>client_id</c>，没有 secret 列）、
/// **不回显**（任何 DTO 都不带它）、**不写日志**。</item>
/// <item>读取顺序：显式配置节 <c>Pim:Files:&lt;Provider&gt;:ClientSecret</c> 优先，
/// 其次本工单承诺的扁平环境变量名（<c>PIM_FILES_GOOGLE_CLIENT_SECRET</c>）。</item>
/// <item>缺失时返回 null —— 由调用方给出**明确错误**，绝不允许静默失败。</item>
/// </list>
/// </summary>
public sealed class FileProviderCredentialSource(IConfiguration configuration)
{
    /// <summary>Google 控制台里登记的 Authorized redirect URI（默认值即工单给定的地址）。</summary>
    public const string DefaultGoogleRedirectUri =
        "https://home.hsww.party:15858/api/v1/files/google/callback";

    private readonly IConfiguration _configuration = configuration;

    /// <summary>取该 provider 的 (clientId, clientSecret)。</summary>
    /// <param name="storedClientId">
    /// 数据库里该账号行存的 client id。OneDrive 由用户自带（Azure 应用注册），因此它优先；
    /// Google 由服务端配置下发，用户不填。
    /// </param>
    public FileProviderCredentials Resolve(string providerKey, string? storedClientId)
    {
        var configuredId = Read(providerKey, "ClientId");
        var secret = Read(providerKey, "ClientSecret");
        var clientId = string.IsNullOrWhiteSpace(storedClientId) ? configuredId : storedClientId;
        return new FileProviderCredentials(clientId, secret, RedirectUriFor(providerKey));
    }

    /// <summary>该 provider 的回调地址；未配置时用 Google 的既定地址。</summary>
    public string? RedirectUriFor(string providerKey)
    {
        var configured = Read(providerKey, "RedirectUri");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.Equals(providerKey, "google", StringComparison.OrdinalIgnoreCase)
            ? DefaultGoogleRedirectUri
            : null;
    }

    /// <summary>
    /// 该 provider 的**对外来源**（scheme://host[:port]），从配置的回调地址推导。
    /// Google 的断点续传初始化必须带 <c>Origin</c>，否则浏览器分片直传会被 CORS 拦下。
    /// </summary>
    public string? PublicOriginFor(string providerKey)
    {
        var redirect = RedirectUriFor(providerKey);
        return Uri.TryCreate(redirect, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private string? Read(string providerKey, string setting)
    {
        var fromConfiguration = _configuration[$"Pim:Files:{providerKey}:{setting}"];
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            return fromConfiguration;
        }

        var envName = $"PIM_FILES_{providerKey.ToUpperInvariant()}_{ToScreamingSnake(setting)}";
        var fromEnvironment = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    private static string ToScreamingSnake(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (char.IsUpper(ch) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }
}

/// <summary>解析出的凭据。<see cref="ClientSecret"/> 只在服务端内存里流转。</summary>
public sealed record FileProviderCredentials(string? ClientId, string? ClientSecret, string? RedirectUri);
