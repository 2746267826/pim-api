namespace Pim.Module.Files.Providers.Abstractions;

/// <summary>
/// 工单 05 / F1：通用网盘抽象的归一化模型。
///
/// 设计口径（见 PR 描述「接口切分」一节）：抽象层只暴露**网盘之间真正共通**的概念，
/// provider 私有语义（Graph 的 <c>/drive/root:</c> 前缀路径、ctag、uploadUrl 分片协议，
/// Google 的 pageToken、resumable session URI）一律在各自 adapter 内部消化，
/// 不允许泄漏到消费方（文件模块的同步/写/读、附件存储、闪念对象存储）。
/// </summary>
/// <summary>一个已授权账号在远端网盘上的身份。</summary>
/// <param name="ProviderKey">provider 标识（<c>onedrive</c> / <c>google</c>），即 file_providers.provider。</param>
/// <param name="AccountId">远端账号 id（Graph accountId / Google 用户 sub）。多账号唯一键的一部分。</param>
/// <param name="DisplayName">展示名（账号列表用）。</param>
/// <param name="DriveId">远端盘 id（Graph driveId；Google 用 <c>My Drive</c> 的 root id 语义承载）。</param>
/// <param name="RootExternalId">
/// 根目录在该 provider 外部 id 空间里的标识。变更流里 ParentExternalId 等于它（或为 null）
/// 表示条目挂在根下 —— 这是唯一让「根」在两个 provider 之间对齐的口径。
/// </param>
public sealed record FileProviderAccount(
    string ProviderKey,
    string AccountId,
    string? DisplayName,
    string? DriveId,
    string RootExternalId);

/// <summary>
/// 归一化的一条远端变更。
/// </summary>
/// <param name="ExternalId">远端条目 id（account 作用域内唯一）。</param>
/// <param name="ParentExternalId">
/// 父条目 id。**与 <paramref name="ParentPath"/> 同时为 null 时**表示这条变更就是根目录自身
/// （Graph delta 的根、Google 的 root、Drive 的 My Drive 根）；不是「未知父」。
/// 只看本字段是不够的：Graph 在个别情况下不给 <c>parentReference.id</c>，
/// 但给了 <c>parentReference.path</c>，此时条目并不是根。
/// </param>
/// <param name="ParentPath">
/// 父目录的 **PIM 绝对路径**（已由 adapter 归一化，例如 Graph 的 <c>/drive/root:/Foo</c> → <c>/Foo</c>）。
/// provider 能廉价给出时提供；给不出（Google changes.list 不返回路径）则为 null，
/// 由同步引擎按 ParentExternalId 在本地索引里解析。这是「容纳两者差异」的关键字段。
/// </param>
/// <param name="Name">条目名。</param>
/// <param name="IsFolder">是否目录。</param>
/// <param name="IsRemoved">是否是删除标记（Graph <c>@removed</c> / Google <c>removed=true</c>）。</param>
/// <param name="Size">字节数（目录为 null）。</param>
/// <param name="MimeType">MIME。</param>
/// <param name="ETag">内容指纹（Graph ctag / Google md5Checksum 或 version）。</param>
/// <param name="ModifiedAt">远端修改时间；provider 给不出时用 <see cref="DateTimeOffset.UnixEpoch"/>。</param>
public sealed record FileChange(
    string ExternalId,
    string? ParentExternalId,
    string? ParentPath,
    string Name,
    bool IsFolder,
    bool IsRemoved,
    long? Size,
    string? MimeType,
    string? ETag,
    DateTimeOffset ModifiedAt);

/// <summary>一页变更。<see cref="NextCursor"/> 为 null 且 <see cref="HasMore"/> 为 false 表示本轮已走完。</summary>
public sealed record FileChangePage(
    IReadOnlyList<FileChange> Changes,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// 增量游标失效：远端明确告知「你给的游标我认不了」，必须从头全量重扫。
/// OneDrive 是 HTTP 410；Google 是 <c>invalid_page_token</c> / 过期的 pageToken。
/// 与「授权失效」是两回事（工单 F4.5 明确要求不得混为一谈）。
/// </summary>
public sealed class ProviderCursorExpiredException(string message) : Exception(message);

/// <summary>
/// 瞬态失败（限流 / 5xx）：可以按 <see cref="RetryAfterSeconds"/> 退避后重试同一请求。
/// </summary>
public sealed class ProviderTransientException(int statusCode, int? retryAfterSeconds, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// 远端拒绝了当前 access token（401）：应刷新令牌后重试同一请求。
/// </summary>
public sealed class ProviderUnauthorizedException(string message) : Exception(message);

/// <summary>
/// 授权本身已失效（refresh token 被吊销/过期，invalid_grant）：
/// 重试没有意义，必须把绑定状态置为 expired 并引导用户重新授权。
/// </summary>
public sealed class ProviderAuthorizationExpiredException(string message) : Exception(message);

/// <summary>远端条目不存在（404）。</summary>
public sealed class ProviderItemNotFoundException(string message) : Exception(message);

/// <summary>远端条目超过本次读取允许的上限。</summary>
public sealed class ProviderContentTooLargeException(long actualBytes)
    : Exception($"Remote content exceeds the allowed size: {actualBytes} bytes")
{
    public long ActualBytes { get; } = actualBytes;
}

/// <summary>
/// provider 侧的**配置/运维**失败（例如第三方项目未启用对应 API、项目被停用/受限）。
///
/// 与「授权失效」严格区分：用户重新授权、检查文件权限都**修不好**，只有运维能修。
/// 因此出口按服务端故障（5xx）处理并记 Error 级日志，让它进监控告警，
/// 而不是伪装成用户侧 4xx（那会让用户陷入「按提示重连 → 还是失败」的死循环，监控也看不到）。
/// </summary>
public sealed class ProviderConfigurationFailureException(string message) : Exception(message);

/// <summary>
/// 远端空间/配额不足。这是**用户可自愈**的（清理网盘后再试），
/// 既不是「上游故障」（502）也不是「授权失效」（重新连接也没用），因此单列一类。
/// </summary>
public sealed class ProviderStorageQuotaExceededException(string message) : Exception(message);

/// <summary>该 provider 不支持本次请求的能力（例如未注册分享能力）。</summary>
public sealed class ProviderCapabilityNotSupportedException(string capability, string providerKey)
    : Exception($"Provider '{providerKey}' does not support {capability}")
{
    public string Capability { get; } = capability;

    public string ProviderKey { get; } = providerKey;
}
