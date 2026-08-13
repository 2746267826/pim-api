namespace Pim.Module.Mobile.DTOs;

public sealed record MobileLocationQueryRequest(
    DateTimeOffset? RangeStartUtc = null,
    DateTimeOffset? RangeEndUtc = null,
    string? Timezone = null,
    string? DeviceId = null,
    double? MaxAccuracyMeters = null,
    bool? IncludeRejected = null,
    string? Cursor = null,
    int? PageSize = null);

public sealed record MobileLocationQueryContext(
    MobileAnalyticsRangeDto Range,
    string? DeviceId,
    double MaxAccuracyMeters,
    bool IncludeRejected,
    string? Cursor,
    int PageSize);

public sealed record MobileGeoBoundsDto(
    double MinLatitude,
    double MinLongitude,
    double MaxLatitude,
    double MaxLongitude);

public sealed record MobileLocationAnalyticsOverviewResponse(
    MobileAnalyticsRangeDto Range,
    DateTimeOffset GeneratedAt,
    int PointCount,
    int UsablePointCount,
    int RejectedPointCount,
    long ActiveSpanSeconds,
    double DistanceMeters,
    int StayCount,
    long LongestStaySeconds,
    double AverageAccuracyMeters,
    int QualityIssueCount,
    IReadOnlyList<string> QualityFlags);

public sealed record MobileLocationPathPointDto(
    string Id,
    DateTimeOffset RecordedAtUtc,
    double Latitude,
    double Longitude,
    double HorizontalAccuracyMeters,
    string Quality,
    IReadOnlyList<string> QualityFlags);

public sealed record MobileLocationSegmentDto(
    string Id,
    string TrackId,
    string DeviceId,
    string Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string LocalStart,
    string LocalEnd,
    long DurationSeconds,
    double DistanceMeters,
    int PointCount,
    double AverageSpeedMetersPerSecond,
    double AverageAccuracyMeters,
    double MaxAccuracyMeters,
    string Quality,
    IReadOnlyList<string> QualityFlags,
    MobileGeoBoundsDto? Bounds,
    IReadOnlyList<MobileLocationPathPointDto> Path);

public sealed record MobileLocationTrackDto(
    string Id,
    string DeviceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DistanceMeters,
    long DurationSeconds,
    int PointCount,
    int SegmentCount,
    MobileGeoBoundsDto? Bounds,
    IReadOnlyList<string> QualityFlags,
    IReadOnlyList<MobileLocationSegmentDto> Segments);

public sealed record MobileLocationSegmentPointPageDto(
    IReadOnlyList<MobileLocationPointDto> Items,
    string? NextCursor,
    bool HasMore);
