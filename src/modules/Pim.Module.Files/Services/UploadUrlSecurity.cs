using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;

namespace Pim.Module.Files.Services;

public sealed record UploadUrlValidation(
    bool IsSecure,
    string? Host,
    string? Reason);

/// <summary>
/// Q04：浏览器直传只接受 HTTPS 预授权地址。内置存储返回同源相对地址，
/// 其他网盘必须是绝对 HTTPS；日志和错误只包含主机名，不包含签名查询参数。
/// </summary>
public static class UploadUrlSecurity
{
    public const int InsecureUrlCode = 5346;
    public const string LogPhase = "create-upload-session";

    public static string EnsureSecure(
        string? rawUrl,
        string providerKey,
        string fileName,
        bool allowRelative,
        ILogger? logger = null)
    {
        var validation = Validate(rawUrl, allowRelative);
        if (validation.IsSecure)
        {
            return rawUrl!;
        }

        logger?.LogWarning(
            "Cloud upload session rejected before returning to browser: provider={ProviderKey} file={FileName} phase={Phase} host={Host} reason={Reason}",
            providerKey,
            fileName,
            LogPhase,
            validation.Host ?? "<invalid>",
            validation.Reason);

        throw new DomainException(
            InsecureUrlCode,
            $"预授权上传地址必须使用 HTTPS（{providerKey}）：{validation.Reason}。已停止上传。");
    }

    public static UploadUrlValidation Validate(string? rawUrl, bool allowRelative)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return new UploadUrlValidation(false, null, "地址为空");
        }

        if (allowRelative && rawUrl.StartsWith('/') && !rawUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return new UploadUrlValidation(true, "same-origin", null);
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return new UploadUrlValidation(false, null, "地址格式无效");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new UploadUrlValidation(false, uri.Host, "地址包含账号或密码");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new UploadUrlValidation(false, uri.Host, "必须使用 HTTPS");
        }

        return new UploadUrlValidation(true, uri.Host, null);
    }
}
