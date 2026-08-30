using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 常去地点 DBSCAN 聚类。
/// 数据源 mobile_location_points，过滤 rejected / 精度 &gt; 100m，按 user + 可选 deviceId + 时间范围；
/// equirectangular 局部平面投影（以数据集平均纬度为原点），米制 DBSCAN（eps=75m, minPts=10），
/// 质心/半径在平面坐标计算后转回经纬度。家 = 本地夜间 01:00-06:00 点数最多的簇（平局取点数多；
/// 无夜间点退化为点数最多）。噪声点不输出。
/// </summary>
public sealed class MobileFrequentPlaceService
{
    private const double BaseEpsMeters = 75;
    private const double MinEpsMeters = 30;
    private const double MaxEpsMeters = 150;
    private const int MinPoints = 10;
    private const double MaxAccuracyMeters = 100;
    private const double EarthRadiusMeters = 6371000;

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileLocationQueryService _queryService;

    public MobileFrequentPlaceService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileLocationQueryService queryService)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
    }

    public async Task<MobileFrequentPlacesResponse> GetFrequentPlacesAsync(
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var points = await LoadUsablePointsAsync(context, ct);
        var places = BuildPlaces(points, context);
        return new MobileFrequentPlacesResponse(
            places.FirstOrDefault(place => place.IsHome),
            places);
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
                && (point.Quality == null || point.Quality.ToLower() != "rejected")
                && point.HorizontalAccuracyMeters <= (decimal)MaxAccuracyMeters);

        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(point => point.DeviceId == context.DeviceId);

        return await query
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .ToListAsync(ct);
    }

    private List<MobileFrequentPlaceDto> BuildPlaces(
        List<MobileLocationPointEntity> points,
        MobileLocationQueryContext context)
    {
        if (points.Count < MinPoints)
            return [];

        var meanLatRad = DegreesToRadians(points.Average(point => Convert.ToDouble(point.Latitude)));
        var cosMeanLat = Math.Cos(meanLatRad);
        var projected = points
            .Select((point, index) => new SimpleDbscan.Point(
                index,
                DegreesToRadians(Convert.ToDouble(point.Longitude)) * cosMeanLat * EarthRadiusMeters,
                DegreesToRadians(Convert.ToDouble(point.Latitude)) * EarthRadiusMeters))
            .ToList();

        var adaptiveEps = ComputeAdaptiveEps(projected);
        var result = SimpleDbscan.Run(projected, adaptiveEps, MinPoints);
        var timeZone = ResolveTimezone(context.Range.Timezone);
        var places = new List<MobileFrequentPlaceDto>();
        var nightCounts = new List<int>();
        foreach (var cluster in result.Clusters)
        {
            var members = cluster.Select(index => points[index]).ToList();
            var (centroidX, centroidY) = Centroid(cluster, projected);
            var (latitude, longitude) = ToLatLon(centroidX, centroidY, cosMeanLat);
            var rawRadius = cluster.Max(index => DistanceMeters(
                centroidX, centroidY, projected[index].X, projected[index].Y));
            // 半径阈值来源: INV-L15 要求 [0,500]，防止异常聚类半径过大
            var radiusMeters = Math.Clamp(rawRadius, 0, 500);
            var visitDayCount = members
                .Select(point => TimeZoneInfo.ConvertTime(point.RecordedAtUtc, timeZone).Date)
                .Distinct()
                .Count();

            places.Add(new MobileFrequentPlaceDto(
                latitude,
                longitude,
                Math.Round(radiusMeters, 1),
                members.Count,
                visitDayCount,
                false));
            nightCounts.Add(members.Count(point => IsNightPoint(point, timeZone)));
        }

        if (places.Count == 0)
            return [];

        // 家 = 夜间点最多（平局取点数多）；无夜间点时退化为点数最多的簇
        var homeIndex = Enumerable.Range(0, places.Count)
            .OrderByDescending(index => nightCounts[index])
            .ThenByDescending(index => places[index].PointCount)
            .First();
        places[homeIndex] = places[homeIndex] with { IsHome = true };

        // 稳定输出顺序：点数降序，其次纬度升序
        return places
            .OrderByDescending(place => place.PointCount)
            .ThenBy(place => place.CenterLatitude)
            .ToList();
    }

    private static (double X, double Y) Centroid(
        IReadOnlyList<int> cluster,
        IReadOnlyList<SimpleDbscan.Point> projected)
    {
        var x = cluster.Average(index => projected[index].X);
        var y = cluster.Average(index => projected[index].Y);
        return (x, y);
    }

    private static (double Latitude, double Longitude) ToLatLon(double x, double y, double cosMeanLat)
    {
        var latitude = RadiansToDegrees(y / EarthRadiusMeters);
        var longitude = RadiansToDegrees(x / (cosMeanLat * EarthRadiusMeters));
        return (latitude, longitude);
    }

    private static bool IsNightPoint(MobileLocationPointEntity point, TimeZoneInfo timeZone)
    {
        var hour = TimeZoneInfo.ConvertTime(point.RecordedAtUtc, timeZone).Hour;
        return hour >= 1 && hour < 6;
    }

    private static double DistanceMeters(double x1, double y1, double x2, double y2)
        => Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));

    private static double ComputeAdaptiveEps(IReadOnlyList<SimpleDbscan.Point> projected)
    {
        if (projected.Count < 2) return BaseEpsMeters;
        // k-distance median (k = MinPoints-1) → estimate local density
        var k = Math.Min(MinPoints - 1, projected.Count - 1);
        var kDistances = new List<double>(projected.Count);
        foreach (var p in projected)
        {
            var distances = projected.Select(q => DistanceMeters(p.X, p.Y, q.X, q.Y)).OrderBy(d => d).ToList();
            kDistances.Add(distances[k]);
        }
        kDistances.Sort();
        var medianK = kDistances[kDistances.Count / 2];
        // scale eps: dense area → smaller, sparse → larger, clamp to [30,150]
        var scaled = Math.Clamp(medianK * 1.2, MinEpsMeters, MaxEpsMeters);
        // blend with base to avoid extreme shift
        return Math.Clamp((scaled + BaseEpsMeters) / 2, MinEpsMeters, MaxEpsMeters);
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180;

    private static double RadiansToDegrees(double radians)
        => radians * 180 / Math.PI;

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
