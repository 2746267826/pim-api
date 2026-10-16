using Pim.Module.Files.Services;

namespace Pim.Module.Files.DTOs;

/// <summary>
/// 发起绑定的请求体。OneDrive 需要用户自带 Azure 应用注册的 <see cref="ClientId"/>；
/// Google 的凭据由服务端配置提供，这里留空即可。
/// </summary>
public sealed record StartProviderBindingRequest(string? ClientId = null);

/// <summary>绑定发起结果。设备码形态给 userCode/verificationUri；跳转形态给 authorizationUrl。</summary>
public sealed record ProviderBindingStartDto(
    Guid ProviderId,
    string ProviderKey,
    string Mode,
    string? UserCode,
    string? VerificationUri,
    int? ExpiresIn,
    string? AuthorizationUrl)
{
    public static ProviderBindingStartDto From(FileProviderBindingStartResult result)
        => new(
            result.ProviderId,
            result.ProviderKey,
            result.Mode,
            result.UserCode,
            result.VerificationUri,
            result.ExpiresIn,
            result.AuthorizationUrl);
}

public sealed record ProviderBindingStatusDto(
    Guid ProviderId,
    string Status,
    string? DriveId,
    string? AccountId,
    string? AccountName,
    string? UserCode,
    string? VerificationUri,
    DateTimeOffset? DeviceCodeExpiresAt,
    int? PollIntervalSeconds = null)
{
    public static ProviderBindingStatusDto From(FileProviderBindingStatusResult result)
        => new(
            result.ProviderId,
            result.Status,
            result.DriveId,
            result.AccountId,
            result.AccountName,
            result.UserCode,
            result.VerificationUri,
            result.DeviceCodeExpiresAt,
            result.PollIntervalSeconds);
}

public sealed record FileSyncResultDto(
    int PagesProcessed,
    int ItemsApplied,
    int ItemsDeleted,
    bool FullRecrawl,
    int ItemsDeferred = 0)
{
    public static FileSyncResultDto From(FileSyncResult result)
        => new(result.PagesProcessed, result.ItemsApplied, result.ItemsDeleted, result.FullRecrawl, result.ItemsDeferred);
}

/// <summary>预授权直链/预览地址的统一响应体（不再带 provider 名）。</summary>
public sealed record FileLinkDto(string Url);

public sealed record FileTextDto(string Content, string? MimeType, long Size, bool Truncated)
{
    public static FileTextDto From(FileTextContent content)
        => new(content.Content, content.MimeType, content.Size, content.Truncated);
}

public sealed record SaveFileTextRequest(string Content);

public sealed record FileWriteResultDto(Guid ItemId, string Path)
{
    public static FileWriteResultDto From(FileWriteResult result)
        => new(result.ItemId, result.Path);
}
