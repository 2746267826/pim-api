namespace Pim.Core.Endpoints;

public sealed record EndpointStatusDto(
    string DeviceId,
    string Platform,
    string? AppVersion,
    string UploadStatus,
    int CollectionCacheCount,
    int OnlineOnlyBlockedCount,
    DateTimeOffset? LastHeartbeatAt,
    /// <summary>
    /// 最近一次同步唤醒为什么没有完成上传（#569）。客户端上报、服务端原样保存；
    /// 取值集合见契约里该字段的 description（不声明闭合 enum，历史值可能与当前写入侧不同）。
    /// </summary>
    string? LastWakeBlockReason = null);

public sealed record EndpointHeartbeatRequest(
    string Platform,
    string? AppVersion = null,
    string? UploadStatus = null,
    int? CollectionCacheCount = null,
    /// <summary>
    /// 最近一次同步唤醒未上传的原因（#569）。可选；缺省表示客户端未上报。
    /// 服务端不校验取值集合（诊断字段），只在超长时截断。
    /// </summary>
    string? LastWakeBlockReason = null);

public sealed record EndpointCollectionQualityDto(
    string DeviceId,
    string Platform,
    string UploadStatus,
    int IssueCount,
    DateTimeOffset CheckedAt);

public sealed record EndpointNotificationActionRequest(
    string Action,
    string RiskLevel,
    string? ConfirmationId = null,
    string? RelatedObjectType = null,
    string? RelatedObjectId = null);

public sealed record EndpointNotificationActionResponse(
    string Result,
    string? DetailUrl = null,
    string? Message = null);
