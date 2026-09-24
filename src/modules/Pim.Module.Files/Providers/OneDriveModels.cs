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

    /// <summary>预授权短时效下载直链；Graph 未返回时为 null。</summary>
    Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default);

    /// <summary>缩略图直链；该文件类型不支持（404）时为 null。</summary>
    Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default);

    /// <summary>Office/PDF 网页预览 embed 地址（POST /preview，无副作用）。</summary>
    Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default);

    /// <summary>小文件瞬态下载；404 返回 null，超过 maxBytes 抛 OneDriveContentTooLargeException。</summary>
    Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default);

    /// <summary>小文件简单上传（≤4MB 场景）。</summary>
    Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default);

    /// <summary>重命名/移动（PATCH name / parentReference），返回 driveItem id。</summary>
    Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default);

    /// <summary>删除（进 OneDrive 回收站）；404 视为已删除，不报错。</summary>
    Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default);

    /// <summary>
    /// 生成分享链接（REQ-21）。<paramref name="expiration"/> 为 null 表示不过期；
    /// 个人版是否支持有效期需实测（V2），不支持时由调用方决定降级方式。
    /// </summary>
    Task<OneDriveShareLink> CreateShareLinkAsync(
        string accessToken,
        string itemId,
        OneDriveSharePermission permission,
        DateTimeOffset? expiration,
        CancellationToken ct = default);

    /// <summary>撤销分享权限（按 permission id）。</summary>
    Task RevokeSharePermissionAsync(string accessToken, string itemId, string permissionId, CancellationToken ct = default);

    /// <summary>列出条目当前的全部分享权限（供「我的分享」与就地撤销）。</summary>
    Task<IReadOnlyList<OneDriveShareLink>> ListSharePermissionsAsync(string accessToken, string itemId, CancellationToken ct = default);

    /// <summary>按路径简单上传新文件（PUT /drive/root:{path}:/content），返回 driveItem id。</summary>
    Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default);

    /// <summary>在当前目录内新建文件夹（REQ-15），返回 driveItem id。</summary>
    Task<string> CreateFolderAsync(string accessToken, string folderPath, string name, CancellationToken ct = default);

    /// <summary>按路径回读条目（REQ-14 登记上传结果）；不存在返回 null。</summary>
    Task<OneDrivePathItem?> GetItemByPathAsync(string accessToken, string itemPath, CancellationToken ct = default);

    /// <summary>
    /// 创建上传会话（REQ-14）：>4MB 的文件由浏览器直接向返回的 <c>uploadUrl</c> 分片上传，
    /// PIM 服务器只创建会话与登记元数据，不搬运字节。
    /// 冲突行为固定为 <c>rename</c>（REQ-12：绝不覆盖同名文件）。
    /// </summary>
    Task<OneDriveUploadSession> CreateUploadSessionAsync(
        string accessToken,
        string itemPath,
        string fileName,
        CancellationToken ct = default);

    /// <summary>项的 OneDrive 网页地址（webUrl）。</summary>
    Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default);
}

public sealed record OneDriveSmallContent(byte[] Bytes, string? ContentType);

/// <summary>Graph 返回的上传会话（<c>uploadUrl</c> 已预授权，分片 PUT 不得再带 Authorization）。</summary>
public sealed record OneDriveUploadSession(string UploadUrl, DateTimeOffset? ExpirationDateTime);

/// <summary>按路径回读到的条目（REQ-14 登记上传结果用；名称以服务端为准，重名时已被改名）。</summary>
public sealed record OneDrivePathItem(string Id, string Name, long? Size, string? MimeType);

/// <summary>分享链接的权限档（REQ-21 / V3：个人版按平台能力只提供可用档位）。</summary>
public enum OneDriveSharePermission
{
    /// <summary>可看。</summary>
    View,

    /// <summary>可编辑。</summary>
    Edit,
}

/// <summary>Graph 返回的分享链接（<c>link.webUrl</c>）。</summary>
public sealed record OneDriveShareLink(string WebUrl, string PermissionType, string? PermissionId, DateTimeOffset? ExpiresAt);

/// <summary>瞬态下载内容超过允许上限。</summary>
public sealed class OneDriveContentTooLargeException(long actualBytes)
    : Exception($"OneDrive content exceeds the allowed size: {actualBytes} bytes")
{
    public long ActualBytes { get; } = actualBytes;
}

public static class OneDriveAuth
{
    public const string DefaultTenant = "consumers";

    /// <summary>P1 只做读取；写入 scope 一并申请，避免后续阶段重新绑定。</summary>
    public const string Scopes = "Files.ReadWrite.All Files.Read offline_access";
}
