using Pim.Core.Data;

namespace Pim.Module.Files.Entities;

/// <summary>
/// 一个**网盘账号**（工单 05 / F2：同一 provider 支持多账号并存）。
///
/// 注意本表已不再是「一个用户一个来源」：<c>provider</c> 只承载
/// <c>onedrive</c> / <c>google</c> 的区分，账号维度由 <c>account_id</c> 承载，
/// 唯一键是 <c>(user_id, provider, account_id) WHERE account_id IS NOT NULL</c>。
/// Nextcloud 时代的 <c>base_url</c> / <c>internal_base_url</c> / <c>username</c> /
/// <c>app_password_secret</c> 四个死字段已由迁移删除（工单 F3）。
/// </summary>
public sealed class FileProviderEntity : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>
    /// provider 标识（<c>onedrive</c> / <c>google</c>）。列保留；
    /// <c>nextcloud</c> 默认值与旧语义随 F3 一并去掉，因此这里不再有默认值，
    /// 建行时必须显式赋值。
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- OAuth 绑定字段 ----
    /// <summary>OAuth 客户端 id。OneDrive 由用户自带（Azure 应用注册），Google 由服务端配置下发。</summary>
    public string? ClientId { get; set; }

    /// <summary>远端盘 id。</summary>
    public string? DriveId { get; set; }

    /// <summary>远端账号 id（Graph accountId / Google 用户 sub）。多账号唯一键的账号维度。</summary>
    public string? AccountId { get; set; }

    public string? AccountName { get; set; }

    /// <summary>
    /// 增量同步游标。语义由 provider 定义：OneDrive 是 deltaLink 完整 URL，
    /// Google 是 <c>full:&lt;pageToken&gt;</c> / <c>delta:&lt;pageToken&gt;</c>。
    /// null 表示下一次同步走全量。
    /// </summary>
    public string? DeltaLink { get; set; }

    /// <summary>游标最近一次被判定失效（410 / invalid page token）的时间，与「授权失效」无关。</summary>
    public DateTimeOffset? DeltaResetAt { get; set; }

    /// <summary>ISecretProtector 加密后的 refresh token；明文永不落库。</summary>
    public byte[]? RefreshTokenEncrypted { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// 该用户附件/闪念图片默认存入的账号（工单 F2 补充设计：多账号时必须有明确归属，
    /// 否则「附件存哪个网盘」无解）。每个用户至多一个默认账号。
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>idle | syncing | error（同步健康状态，区别于绑定状态 Status）。</summary>
    public string SyncStatus { get; set; } = "idle";

    /// <summary>最近一次同步处理的变更条数（进度可见用，非树总量）。</summary>
    public long SyncedItemCount { get; set; }

    /// <summary>
    /// 连续多少次同步因为「有条目解析不出父路径」而没有推进游标。
    /// 有界重试：连续超过上限后放弃这些条目并正常推进，
    /// 否则一个永远无法定位父目录的条目会让同步**永久**停在原地（复审 Important）。
    /// </summary>
    public int DeferredBlockCount { get; set; }

    /// <summary>
    /// 授权等待期临时持有的加密机密：设备码形态存 device code，浏览器跳转形态存一次性 state。
    /// 两种情况都只需「服务端保存、一次性校验、过期作废」，因此共用一列。
    /// 列名仍是历史名 <c>device_code_encrypted</c>（改名会牵动无谓的迁移噪音）。
    /// </summary>
    public byte[]? PendingAuthorizationSecretEncrypted { get; set; }

    /// <summary>设备码形态给用户看的短码；浏览器跳转形态为 null。</summary>
    public string? UserCode { get; set; }

    /// <summary>设备码形态的验证地址；浏览器跳转形态为 null（授权地址不落库）。</summary>
    public string? VerificationUri { get; set; }

    /// <summary>上面那个一次性机密的过期时间。</summary>
    public DateTimeOffset? PendingAuthorizationExpiresAt { get; set; }

    public List<FileItemEntity> Items { get; } = new();
}
