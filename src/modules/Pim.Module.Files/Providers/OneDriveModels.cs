namespace Pim.Module.Files.Providers;

/// <summary>OneDrive 绑定申请的设备码（PIM 只透传给用户，device_code 加密留在服务端轮询）。</summary>
public sealed record OneDriveDeviceCodeStart(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn);

public sealed record OneDriveTokenResult(string AccessToken, string? RefreshToken, int ExpiresIn, string? Scope);

public sealed record OneDriveDriveInfo(string DriveId, string DriveType, long? QuotaUsed, long? QuotaTotal);

public sealed record OneDriveAccountInfo(string AccountId, string? DisplayName);

/// <summary>Graph delta 流中的一条变更。Removed 条目只带 Id。</summary>
public sealed record OneDriveDeltaItem(
    string Id,
    string? ParentId,
    string? ParentPath,
    string Name,
    bool IsFolder,
    bool IsRemoved,
    long? Size,
    string? MimeType,
    string? Ctag,
    DateTimeOffset ModifiedAt);

public sealed record OneDriveDeltaPage(
    IReadOnlyList<OneDriveDeltaItem> Items,
    string? NextLink,
    string? DeltaLink);

/// <summary>
/// Graph 调用失败（非 2xx）。429 携带 Retry-After；410 表示 delta 游标失效需要全量重扫。
/// </summary>
public sealed class OneDriveGraphException(int statusCode, int? retryAfterSeconds, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public interface IOneDriveGraphClient
{
    Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default);

    Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default);

    Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default);

    Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default);

    Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default);

    Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default);
}

public static class OneDriveAuth
{
    public const string DefaultTenant = "consumers";

    /// <summary>P1 只做读取；写入 scope 一并申请，避免后续阶段重新绑定。</summary>
    public const string Scopes = "Files.ReadWrite.All Files.Read offline_access";
}
