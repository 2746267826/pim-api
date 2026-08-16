using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 移动统计（出门 / 里程 / 速度峰值）。
/// 家中心来自 MobileFrequentPlaceService 的 DBSCAN 家聚类；里程复用
/// MobileLocationAggregationService.GetMovementSegmentsAsync 的 move 段（jump 已剔除），不复制算法。
/// </summary>
public sealed class MobileMovementStatsService
{
    private const double HomeRadiusMeters = 150;
    private const double EarthRadiusMeters = 6371000;
    private const int MaxOutings = 50;
    private static readonly TimeSpan OutingBridgeGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinOutingDuration = TimeSpan.FromMinutes(10);

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileLocationQueryService _queryService;
    private readonly MobileLocationAggregationService _aggregationService;
    private readonly MobileFrequentPlaceService _frequentPlaceService;

    public MobileMovementStatsService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileLocationQueryService queryService,
        MobileLocationAggregationService aggregationService,
        MobileFrequentPlaceService frequentPlaceService)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
        _aggregationService = aggregationService;
        _frequentPlaceService = frequentPlaceService;
    }

    public async Task<MobileMovementStatsResponse> GetMovementStatsAsync(
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var usablePoints = await LoadUsablePointsAsync(context, ct);
        var segments = await _aggregationService.GetMovementSegmentsAsync(request, ct);
        var distanceMeters = Math.Round(segments.Sum(segment => segment.DistanceMeters), 1);
        var maxSpeed = ComputeMaxSpeed(usablePoints, segments);

        var frequentPlaces = await _frequentPlaceService.GetFrequentPlacesAsync(request, ct);
        var home = frequentPlaces.Home;
        if (home is null)
        {
            return new MobileMovementStatsResponse(
                null,
                0,
                0,
                [],
                distanceMeters,
                maxSpeed,
                BuildPerDay(segments, [], context));
        }

        var (outingCount, outingSeconds, outings) = ComputeOutings(usablePoints, home);
        return new MobileMovementStatsResponse(
            new MobileGeoPointDto(home.CenterLatitude, home.CenterLongitude),
            outingCount,
            outingSeconds,
            outings,
            distanceMeters,
            maxSpeed,
            BuildPerDay(segments, outings, context));
    }

    private async Task<List<MobileLocationPointEntity>> LoadUsablePointsAsync(
        MobileLocationQueryContext context,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId
                && point.RecordedAtUtc >= context.Range.RangeStartUtc
                && point.RecordedAtUtc < context.Range.RangeEndUtc
                && !string.Equals(point.Quality, "rejected", StringComparison.OrdinalIgnoreCase)
                && point.HorizontalAccuracyMeters <= (decimal)context.MaxAccuracyMeters);

        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(point => point.DeviceId == context.DeviceId);

        return await query
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .ToListAsync(ct);
    }

    private static (int Count, long Seconds, IReadOnlyList<MobileOutingDto> Outings) ComputeOutings(
        IReadOnlyList<MobileLocationPointEntity> points,
        MobileFrequentPlaceDto home)
    {
        // 离家距离序列（usable 点按时间）：>150m 起、<=150m 止；间隔 <=10min 桥接；
        // 区间时长 >=10min 计一次出门。
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        DateTimeOffset? start = null;
        DateTimeOffset? lastAway = null;
        foreach (var point in points)
        {
            var away = DistanceMeters(
                Convert.ToDouble(point.Latitude),
                Convert.ToDouble(point.Longitude),
                home.CenterLatitude,
                home.CenterLongitude) > HomeRadiusMeters;
            if (!away)
                continue;

            if (start is null)
            {
                start = point.RecordedAtUtc;
                lastAway = point.RecordedAtUtc;
            }
            else if (point.RecordedAtUtc - lastAway <= OutingBridgeGap)
            {
                lastAway = point.RecordedAtUtc;
            }
            else
            {
                intervals.Add((start.Value, lastAway!.Value));
                start = point.RecordedAtUtc;
                lastAway = point.RecordedAtUtc;
            }
        }

        if (start is not null)
            intervals.Add((start.Value, lastAway!.Value));

        var outings = intervals
            .Where(interval => interval.End - interval.Start >= MinOutingDuration)
            .Select(interval => new MobileOutingDto(
                interval.Start,
                interval.End,
                Convert.ToInt64((interval.End - interval.Start).TotalSeconds)))
            .OrderByDescending(outing => outing.StartUtc)
            .Take(MaxOutings)
            .OrderBy(outing => outing.StartUtc)
            .ToList();

        return (outings.Count, outings.Sum(outing => outing.Seconds), outings);
    }

    private static double? ComputeMaxSpeed(
        IReadOnlyList<MobileLocationPointEntity> usablePoints,
        IReadOnlyList<MobileMovementSegmentDto> segments)
    {
        var pointSpeeds = usablePoints
            .Where(point => point.SpeedMetersPerSecond.HasValue)
            .Select(point => Convert.ToDouble(point.SpeedMetersPerSecond!.Value))
            .ToList();
        if (pointSpeeds.Count > 0)
            return Math.Round(pointSpeeds.Max(), 3);

        var segmentSpeeds = segments
            .Where(segment => segment.EndUtc > segment.StartUtc)
            .Select(segment => segment.DistanceMeters / (segment.EndUtc - segment.StartUtc).TotalSeconds)
            .ToList();
        return segmentSpeeds.Count > 0 ? Math.Round(segmentSpeeds.Max(), 3) : null;
    }

    private static IReadOnlyList<MobileMovementStatsDayDto> BuildPerDay(
        IReadOnlyList<MobileMovementSegmentDto> segments,
        IReadOnlyList<MobileOutingDto> outings,
        MobileLocationQueryContext context)
    {
        var timeZone = ResolveTimezone(context.Range.Timezone);
        var days = new Dictionary<DateTime, (int OutingCount, long OutingSeconds, double DistanceMeters)>();
        foreach (var outing in outings)
        {
            var date = TimeZoneInfo.ConvertTime(outing.StartUtc, timeZone).Date;
            var entry = days.GetValueOrDefault(date);
            days[date] = (entry.OutingCount + 1, entry.OutingSeconds + outing.Seconds, entry.DistanceMeters);
        }

        foreach (var segment in segments)
        {
            var date = TimeZoneInfo.ConvertTime(segment.StartUtc, timeZone).Date;
            var entry = days.GetValueOrDefault(date);
            days[date] = (entry.OutingCount, entry.OutingSeconds, entry.DistanceMeters + segment.DistanceMeters);
        }

        return days
            .OrderBy(pair => pair.Key)
            .Select(pair => new MobileMovementStatsDayDto(
                pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                pair.Value.OutingCount,
                pair.Value.OutingSeconds,
                Math.Round(pair.Value.DistanceMeters, 1)))
            .ToList();
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var lat1Rad = DegreesToRadians(lat1);
        var lat2Rad = DegreesToRadians(lat2);
        var deltaLat = DegreesToRadians(lat2 - lat1);
        var deltaLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
            + Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180;

    private static TimeZoneInfo ResolveTimezone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }
}
