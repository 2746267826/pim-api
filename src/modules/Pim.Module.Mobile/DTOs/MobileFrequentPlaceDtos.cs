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
