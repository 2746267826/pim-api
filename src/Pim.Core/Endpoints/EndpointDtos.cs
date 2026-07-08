namespace Pim.Core.Endpoints;

public sealed record EndpointStatusDto(
    string DeviceId,
    string Platform,
    string? AppVersion,
    string UploadStatus,
    int CollectionCacheCount,
    int OnlineOnlyBlockedCount,
    DateTimeOffset? LastHeartbeatAt);

public sealed record EndpointHeartbeatRequest(
    string Platform,
    string? AppVersion = null,
    string? UploadStatus = null,
    int? CollectionCacheCount = null);

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
