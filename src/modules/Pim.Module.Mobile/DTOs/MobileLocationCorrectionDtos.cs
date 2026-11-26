namespace Pim.Module.Mobile.DTOs;

/// <summary>
/// 历史位置原始点管理查询。segmentId 仅在“只看当前段”时生效；
/// includeExcluded=true 时忽略 segmentId，返回该设备/时间范围内全部未删除点，便于恢复被排除点。
/// </summary>
public sealed record MobileManagedLocationQueryRequest(
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc,
    string? Timezone,
    string? DeviceId,
    string? SegmentId,
    bool IncludeExcluded,
    string? Cursor,
    int? PageSize);

public sealed record MobileManagedLocationPointDto(
    Guid Id,
    string DeviceId,
    DateTimeOffset RecordedAtUtc,
    double Latitude,
    double Longitude,
    double? OriginalLatitude,
    double? OriginalLongitude,
    double HorizontalAccuracyMeters,
    string Provider,
    string SourceKind,
    string Quality,
    bool IsExcluded,
    bool IsCorrected,
    int Revision,
    DateTimeOffset? ExcludedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed record MobileManagedLocationPointPageDto(
    IReadOnlyList<MobileManagedLocationPointDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record MobileLocationPointCorrectionRequest(
    double Latitude,
    double Longitude,
    int Revision,
    bool? Excluded = null,
    string? Reason = null);

public sealed record MobileLocationPointRestoreRequest(
    int Revision,
    string? Reason = null);

public sealed record MobileLocationPointExclusionItem(
    Guid PointId,
    int Revision);

public sealed record MobileLocationPointExclusionRequest(
    IReadOnlyList<MobileLocationPointExclusionItem> Items,
    bool Excluded,
    string? Reason = null);

public sealed record MobileLocationFillRequest(
    string DeviceId,
    DateTimeOffset RecordedAtUtc,
    double Latitude,
    double Longitude,
    double? HorizontalAccuracyMeters = null,
    string? Reason = null);

public sealed record MobileLocationSegmentMergeRequest(
    IReadOnlyList<string> SegmentIds,
    string Kind,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    string? Timezone = null,
    string? DeviceId = null,
    string? Reason = null);

public sealed record MobileSavedPlaceCreateRequest(
    string Name,
    string? Type,
    double CenterLatitude,
    double CenterLongitude,
    double RadiusMeters);

public sealed record MobileSavedPlaceUpdateRequest(
    string Name,
    string? Type,
    int Revision);

public sealed record MobileSavedPlaceDto(
    Guid Id,
    string Name,
    string Type,
    double CenterLatitude,
    double CenterLongitude,
    double RadiusMeters,
    bool IsEnabled,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record MobileLocationPointRevisionDto(
    Guid Id,
    Guid PointId,
    string DeviceId,
    string Operation,
    int RevisionBefore,
    int RevisionAfter,
    double? OriginalLatitude,
    double? OriginalLongitude,
    double? NewLatitude,
    double? NewLongitude,
    bool? ExcludedBefore,
    bool? ExcludedAfter,
    string? Reason,
    Guid ActorUserId,
    DateTimeOffset CreatedAtUtc);

public sealed record MobileLocationBatchItemResult(
    Guid PointId,
    string Outcome,
    string? Message = null);

public sealed record MobileLocationBatchMutationResult(
    int UpdatedCount,
    IReadOnlyList<MobileLocationBatchItemResult> Items,
    DateTimeOffset RecomputedAtUtc);

public sealed record MobileLocationPointMutationResult(
    MobileManagedLocationPointDto Point,
    DateTimeOffset RecomputedAtUtc);

public sealed record MobileLocationPointDeleteResult(
    Guid PointId,
    DateTimeOffset DeletedAtUtc,
    DateTimeOffset RecomputedAtUtc);

public sealed record MobileLocationSegmentMergeResult(
    string Kind,
    Guid BoundaryPointId,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset RecomputedAtUtc);
