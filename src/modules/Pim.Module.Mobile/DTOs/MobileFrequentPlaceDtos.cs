namespace Pim.Module.Mobile.DTOs;

public sealed record MobileFrequentPlaceDto(
    double CenterLatitude,
    double CenterLongitude,
    double RadiusMeters,
    int PointCount,
    int VisitDayCount,
    bool IsHome);

public sealed record MobileFrequentPlacesResponse(
    MobileFrequentPlaceDto? Home,
    IReadOnlyList<MobileFrequentPlaceDto> Places);

/// <summary>move 段扁平列表（按设备分组），供移动统计复用里程。</summary>
public sealed record MobileMovementSegmentDto(
    string DeviceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DistanceMeters);

public sealed record MobileGeoPointDto(double Latitude, double Longitude);

public sealed record MobileOutingDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Seconds);

public sealed record MobileMovementStatsDayDto(
    string Date,
    int OutingCount,
    long OutingSeconds,
    double DistanceMeters);

public sealed record MobileMovementStatsResponse(
    MobileGeoPointDto? HomeCenter,
    int OutingCount,
    long OutingSeconds,
    IReadOnlyList<MobileOutingDto> Outings,
    double DistanceMeters,
    double? MaxSpeedMetersPerSecond,
    IReadOnlyList<MobileMovementStatsDayDto> PerDay);
