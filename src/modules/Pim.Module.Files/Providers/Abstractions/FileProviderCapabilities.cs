namespace Pim.Module.Files.Providers.Abstractions;

/// <summary>
/// 工单 05 / F1：通用 provider 能力接口。
///
/// 切分口径：**按能力切，而不是按「一个网盘一个实现」切一个大接口**。
/// 理由（PR 描述「接口切分」一节）：两个 provider 的能力集合并不相同 ——
/// Graph 有预授权 uploadUrl 分片、Google 有 resumable session URI 且两者协议不同形；
/// 若把它们塞进一个 25 方法的胖接口，Google 侧会有一半方法只能抛「不支持」，
/// 那就是把 Graph 的形状冒充成通用契约。能力接口让「不支持」在类型层面就显式
/// （<see cref="IFileProviderSession.Shares"/> 为 null），消费方必须显式处理。
/// </summary>
/// <summary>远端条目（读取侧归一化视图）。</summary>
/// <param name="Path">
/// 该条目**自身**的 PIM 绝对路径（根目录为 <c>/</c>）。provider 能廉价算出时提供；
/// 算不出时为 null，由同步引擎按 ParentExternalId 在本地索引里解析。
/// </param>
public sealed record RemoteItem(
    string Id,
    string Name,
    long? Size,
    string? MimeType,
    bool IsFolder,
    string? Path = null,
    string? ETag = null);

/// <summary>
/// 目标父目录的两种坐标：**外部 id** 与 **PIM 绝对路径**。
///
/// 为什么要同时给：各 provider 的 HTTP API 说的是不同的语言 ——
/// Graph 的写入端点按路径寻址（<c>/drive/root:/A/B:/children</c>），
/// Drive v3 按父 id 寻址（<c>parents: [id]</c>）。
/// 抽象层把两个坐标都递过去，provider 用自己那一套；
/// 强行只留一个，另一边就得在 adapter 里现造一套路径解析/反查，那才是真正的泄漏。
/// 两者都由调用方（文件模块）从本地索引取，必定一致。
/// </summary>
public sealed record RemoteFolderRef(string? ExternalId, string Path);

/// <summary>远端瞬时读到的内容。</summary>
public sealed record RemoteContent(byte[] Bytes, string? ContentType);

/// <summary>远端上传会话：<see cref="UploadUrl"/> 已预授权，客户端直接分片 PUT，PIM 不搬字节。</summary>
public sealed record RemoteUploadSession(string UploadUrl, DateTimeOffset? ExpirationDateTime);

/// <summary>分享权限档（个人自用只有这两档）。</summary>
public enum FileSharePermission
{
    /// <summary>可看。</summary>
    View,

    /// <summary>可编辑。</summary>
    Edit,
}

/// <summary>远端分享链接。</summary>
public sealed record RemoteShareLink(
    string WebUrl,
    string PermissionType,
    string? PermissionId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// 一个 provider 实现的入口。由 <see cref="IFileProviderFactory"/> 按
/// <see cref="ProviderKey"/> 解析，因此新增网盘不需要改任何消费方代码。
/// </summary>
public interface IFileProvider
{
    /// <summary>provider 标识，与 <c>file_providers.provider</c> 同域（<c>onedrive</c> / <c>google</c>）。</summary>
    string ProviderKey { get; }

    /// <summary>是否为「浏览器跳转授权」形态（Google 是；OneDrive 设备码不是）。</summary>
    FileProviderAuthorizationMode AuthorizationMode { get; }

    /// <summary>是否支持增量游标同步。为 false 时同步引擎走整树列举。</summary>
    bool SupportsIncrementalSync { get; }

    /// <summary>是否支持分享。</summary>
    bool SupportsSharing { get; }

    /// <summary>打开一个**账号作用域**会话。同一会话内的所有调用共享同一 access token。</summary>
    Task<IFileProviderSession> OpenSessionAsync(FileProviderContext context, CancellationToken ct = default);
}

/// <summary>
/// 账号作用域会话。这是「同一 provider 支持多账号并存」的落点：
/// 会话绑定在**具体账号行**（<see cref="FileProviderContext.ProviderRowId"/>）上，
/// 不存在任何「按用户取唯一 OneDrive」的隐含假设。
/// </summary>
public interface IFileProviderSession
{
    FileProviderAccount Account { get; }

    /// <summary>内容读取能力（下载直链 / 缩略图 / 预览 / 小文件读 / 条目回读）。</summary>
    IFileContentReader Content { get; }

    /// <summary>写入能力（上传 / 改名 / 移动 / 删除 / 新建目录 / 上传会话）。</summary>
    IFileItemWriter Writer { get; }

    /// <summary>分享能力；该 provider 不支持时为 null（消费方必须显式处理，不许静默吞掉）。</summary>
    IFileShareManager? Shares { get; }

    /// <summary>增量变更流能力；不支持时为 null。</summary>
    IIncrementalChangeSource? Changes { get; }
}

/// <summary>打开会话所需的账号上下文；access token 由模块的令牌服务按账号提供。</summary>
/// <param name="AccessToken">取当前账号的 access token（内部走缓存与刷新）。</param>
public sealed record FileProviderContext(
    Guid ProviderRowId,
    Guid UserId,
    string ProviderKey,
    string? ClientId,
    string? DriveId,
    string? AccountId,
    Func<CancellationToken, Task<string>> AccessToken);

/// <summary>
/// 增量变更流。游标是**不透明字符串**：OneDrive 就是 deltaLink 完整 URL，
/// Google 是 <c>full:&lt;pageToken&gt;</c>（首扫中）/ <c>delta:&lt;pageToken&gt;</c>（增量中）。
/// 这样「首扫怎么走、增量怎么走」的差异留在 adapter 内，引擎只认「有没有下一页」。
/// </summary>
public interface IIncrementalChangeSource
{
    /// <summary>
    /// 取一页变更。<paramref name="cursor"/> 为 null 表示**首次/重置后的全量重扫**。
    /// 游标失效必须抛 <see cref="ProviderCursorExpiredException"/>，而不是静默从头开始 ——
    /// 引擎需要知道「这轮是全量重扫」才能做缺失项清理。
    /// </summary>
    Task<FileChangePage> GetChangesPageAsync(string? cursor, CancellationToken ct = default);
}

/// <summary>内容读取能力。</summary>
public interface IFileContentReader
{
    /// <summary>预授权下载直链；provider 不提供时为 null。</summary>
    Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken ct = default);

    /// <summary>缩略图直链；不支持该类型时为 null。</summary>
    Task<string?> GetThumbnailUrlAsync(string itemId, string size, CancellationToken ct = default);

    /// <summary>网页预览地址；不支持时为 null。</summary>
    Task<string?> GetPreviewUrlAsync(string itemId, CancellationToken ct = default);

    /// <summary>条目在 provider 网页端的地址；给不出时为 null。</summary>
    Task<string?> GetWebUrlAsync(string itemId, CancellationToken ct = default);

    /// <summary>小文件瞬态读取；不存在返回 null，超过 <paramref name="maxBytes"/> 抛
    /// <see cref="ProviderContentTooLargeException"/>。</summary>
    Task<RemoteContent?> DownloadSmallAsync(string itemId, long maxBytes, CancellationToken ct = default);

    /// <summary>按 id 回读条目；不存在返回 null。</summary>
    Task<RemoteItem?> GetItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// 按 PIM 绝对路径回读条目；不存在返回 null。
    /// 只能按父 id 寻址的 provider（Drive v3）无法在不查索引的情况下解析路径，
    /// 此时抛 <see cref="ProviderCapabilityNotSupportedException"/>，调用方必须改用 id 定位。
    /// </summary>
    Task<RemoteItem?> GetItemByPathAsync(string path, CancellationToken ct = default);
}

/// <summary>写入能力。</summary>
public interface IFileItemWriter
{
    /// <summary>覆盖已有条目的内容（小文件）。</summary>
    Task PutContentAsync(string itemId, byte[] bytes, string contentType, CancellationToken ct = default);

    /// <summary>
    /// 在 <paramref name="parent"/> 下新建文件（重名时由 provider 决定改名策略，
    /// **绝不覆盖同名文件**）。返回新条目 id。
    /// </summary>
    Task<string> CreateFileAsync(
        RemoteFolderRef parent,
        string name,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default);

    /// <summary>改名/移动；返回条目 id。</summary>
    Task<string> PatchItemAsync(
        string itemId,
        string? newName,
        string? newParentExternalId,
        CancellationToken ct = default);

    /// <summary>删除（进远端回收站）；远端已不存在时不报错。</summary>
    Task DeleteItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>在 <paramref name="parent"/> 内新建目录；返回新条目 id。</summary>
    Task<string> CreateFolderAsync(RemoteFolderRef parent, string name, CancellationToken ct = default);

    /// <summary>创建上传会话（PIM 只创建会话与登记元数据，不搬字节）。</summary>
    Task<RemoteUploadSession> CreateUploadSessionAsync(
        RemoteFolderRef parent,
        string fileName,
        CancellationToken ct = default);
}

/// <summary>分享能力。</summary>
public interface IFileShareManager
{
    Task<RemoteShareLink> CreateShareLinkAsync(
        string itemId,
        FileSharePermission permission,
        DateTimeOffset? expiration,
        CancellationToken ct = default);

    Task RevokeSharePermissionAsync(string itemId, string permissionId, CancellationToken ct = default);

    Task<IReadOnlyList<RemoteShareLink>> ListSharePermissionsAsync(string itemId, CancellationToken ct = default);
}

/// <summary>把 provider 的 OAuth 刷新端点包成能力，令牌服务因此与 provider 无关。</summary>
public interface IFileProviderOAuthClient
{
    string ProviderKey { get; }

    /// <summary>刷新失败时抛 <see cref="ProviderAuthorizationExpiredException"/>（凭证失效）或
    /// <see cref="ProviderTransientException"/>（可重试）。</summary>
    Task<FileProviderTokenResult> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken ct = default);
}

/// <summary>刷新得到的令牌。</summary>
public sealed record FileProviderTokenResult(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    string? Scope);

/// <summary>授权形态。</summary>
public enum FileProviderAuthorizationMode
{
    /// <summary>设备码轮询（OneDrive）。</summary>
    DeviceCode,

    /// <summary>浏览器跳转（Google）。需要回调端点 + 人工点同意。</summary>
    BrowserRedirect,
}

/// <summary>发起授权得到的挑战。</summary>
/// <param name="AuthorizationUrl">BrowserRedirect 形态的授权地址（含 state）。可回给客户端。</param>
/// <param name="State">BrowserRedirect 形态的一次性 state。服务端加密保存，回调时校验；**不回给客户端**。</param>
/// <param name="DeviceCode">
/// DeviceCode 形态的设备码。服务端加密保存供轮询使用；**不回给客户端**（客户端只需要 user code）。
/// </param>
public sealed record FileProviderAuthorizationChallenge(
    FileProviderAuthorizationMode Mode,
    string? UserCode = null,
    string? VerificationUri = null,
    int? ExpiresIn = null,
    string? AuthorizationUrl = null,
    string? State = null,
    string? DeviceCode = null);

/// <summary>授权完成后的结果。</summary>
public sealed record FileProviderAuthorizationResult(
    FileProviderAccount Account,
    string? RefreshToken,
    int ExpiresIn);

/// <summary>授权发起所需的服务端配置（client id/secret 只从配置/环境变量来）。</summary>
public sealed record FileProviderAuthorizationRequest(
    Guid ProviderRowId,
    Guid UserId,
    string? ClientId,
    string? ClientSecret,
    string? RedirectUri);

/// <summary>
/// 授权（绑定）能力：设备码与浏览器跳转两种形态共存。
/// </summary>
public interface IFileProviderAuthorizer
{
    string ProviderKey { get; }

    FileProviderAuthorizationMode Mode { get; }

    /// <summary>发起授权。</summary>
    Task<FileProviderAuthorizationChallenge> BeginAsync(
        FileProviderAuthorizationRequest request,
        CancellationToken ct = default);
}

/// <summary>设备码形态：轮询换取令牌。</summary>
public interface IDeviceCodeAuthorizer : IFileProviderAuthorizer
{
    /// <summary>
    /// 轮询一次。<c>authorization_pending</c> / <c>slow_down</c> 抛
    /// <see cref="FileProviderAuthorizationPendingException"/>，
    /// <c>expired_token</c> 抛 <see cref="FileProviderAuthorizationFailedException"/>，
    /// 用户拒绝抛 <see cref="FileProviderAuthorizationDeniedException"/>。
    /// </summary>
    Task<FileProviderAuthorizationResult> PollAsync(
        FileProviderAuthorizationRequest request,
        string deviceCode,
        CancellationToken ct = default);
}

/// <summary>浏览器跳转形态：回调换令牌。</summary>
public interface IBrowserRedirectAuthorizer : IFileProviderAuthorizer
{
    /// <summary>
    /// 该 provider 的回调路径（注册到第三方控制台的那个地址的 path 部分）。
    /// 端点按它挂载，避免把回调地址硬编码在模块里。
    /// </summary>
    string CallbackPath { get; }

    /// <summary>用回调带来的 code + state 换取令牌。state 无效/过期必须抛
    /// <see cref="FileProviderAuthorizationFailedException"/>。</summary>
    Task<FileProviderAuthorizationResult> CompleteAsync(
        FileProviderAuthorizationRequest request,
        string code,
        string state,
        CancellationToken ct = default);
}

/// <summary>授权仍在等待用户操作（非错误）。</summary>
public sealed class FileProviderAuthorizationPendingException(int? pollIntervalSeconds, string message)
    : Exception(message)
{
    public int? PollIntervalSeconds { get; } = pollIntervalSeconds;
}

/// <summary>用户拒绝了授权。</summary>
public sealed class FileProviderAuthorizationDeniedException(string message) : Exception(message);

/// <summary>授权失败/已过期/state 非法。</summary>
public sealed class FileProviderAuthorizationFailedException(string message) : Exception(message);
