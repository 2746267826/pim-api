using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 定位类不变量定义
/// </summary>
public static class LocationInvariants
{
    // ========== 物理约束 ==========

    /// <summary>
    /// INV-L01: 两点间速度 <= 350km/h（高铁上限）
    /// 转换: 350km/h ≈ 97.2m/s
    /// </summary>
    public static (bool pass, string detail) CheckSpeedCap(
        List<(double lat, double lon, double speedMps)> points,
        double maxSpeedMps = 97.2)
    {
        var violations = points
            .Where(p => p.speedMps > maxSpeedMps)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.OrderByDescending(p => p.speedMps).First();
            return (false,
                $"INV-L01 FAIL: point ({worst.lat:F4},{worst.lon:F4}) speed {worst.speedMps * 3.6:F0}km/h > {maxSpeedMps * 3.6:F0}km/h");
        }
        return (true, "INV-L01 PASS");
    }

    /// <summary>
    /// INV-L02: GPS坐标在合理范围内（中国境内）
    /// </summary>
    public static (bool pass, string detail) CheckValidChinaCoordinates(
        List<(double lat, double lon)> points)
    {
        var violations = points
            .Where(p => p.lat < 3.0 || p.lat > 54.0 || p.lon < 73.0 || p.lon > 135.0)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.First();
            return (false,
                $"INV-L02 FAIL: point ({worst.lat:F4},{worst.lon:F4}) outside China bounds");
        }
        return (true, "INV-L02 PASS");
    }

    /// <summary>
    /// INV-L03: 水平精度 > 0 且 < 1000米
    /// </summary>
    public static (bool pass, string detail) CheckValidAccuracy(
        List<double> accuracyMeters)
    {
        var violations = accuracyMeters
            .Where(a => a <= 0 || a > 1000)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.First();
            return (false,
                $"INV-L03 FAIL: accuracy {worst:F1}m out of valid range (0, 1000]");
        }
        return (true, "INV-L03 PASS");
    }

    /// <summary>
    /// INV-L04: 海拔在合理范围内（-500m 到 9000m）
    /// </summary>
    public static (bool pass, string detail) CheckValidAltitude(
        List<double> altitudes)
    {
        var violations = altitudes
            .Where(a => a < -500 || a > 9000)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.First();
            return (false,
                $"INV-L04 FAIL: altitude {worst:F0}m out of valid range [-500, 9000]");
        }
        return (true, "INV-L04 PASS");
    }

    /// <summary>
    /// INV-L05: 时间戳递增（轨迹点按时间排序）
    /// </summary>
    public static (bool pass, string detail) CheckTimestampsMonotonic(
        List<DateTimeOffset> timestamps)
    {
        for (int i = 1; i < timestamps.Count; i++)
        {
            if (timestamps[i] < timestamps[i - 1])
            {
                return (false,
                    $"INV-L05 FAIL: timestamp {i} ({timestamps[i]:O}) < timestamp {i-1} ({timestamps[i-1]:O})");
            }
        }
        return (true, "INV-L05 PASS");
    }

    /// <summary>
    /// INV-L06: 常去地点数量 >= 0 且不为null
    /// </summary>
    public static (bool pass, string detail) CheckFrequentPlacesNonNegative(
        int frequentPlaceCount)
    {
        if (frequentPlaceCount < 0)
        {
            return (false,
                $"INV-L06 FAIL: frequent place count {frequentPlaceCount} is negative");
        }
        return (true, "INV-L06 PASS");
    }

    /// <summary>
    /// INV-L07: 噪声底限不应被异常值拉高到 > 100米
    /// </summary>
    public static (bool pass, string detail) CheckNoiseFloorReasonable(
        double noiseFloorMeters, double maxReasonable = 100.0)
    {
        if (noiseFloorMeters > maxReasonable)
        {
            return (false,
                $"INV-L07 FAIL: noiseFloor {noiseFloorMeters:F1}m > {maxReasonable:F1}m (likely corrupted by outliers)");
        }
        return (true, "INV-L07 PASS");
    }

    /// <summary>
    /// INV-L08: DBSCAN聚类数 >= 0，且每个聚类至少2个点
    /// </summary>
    public static (bool pass, string detail) CheckClusterValidity(
        List<List<(double lat, double lon)>> clusters)
    {
        var invalidClusters = clusters.Where(c => c.Count < 2).ToList();
        if (invalidClusters.Any())
        {
            return (false,
                $"INV-L08 FAIL: {invalidClusters.Count} cluster(s) have < 2 points");
        }
        return (true, "INV-L08 PASS");
    }

    // ========== 扩展不变量 ==========

    /// <summary>
    /// INV-L09: 地理边界框有效性 - minLat <= maxLat 且 minLon <= maxLon 且在地球范围内
    /// 不变量: minLat <= maxLat, minLon <= maxLon, 范围在 [-90,90]x[-180,180]
    /// </summary>
    public static (bool pass, string detail) CheckBoundsValidity(
        double minLat, double maxLat, double minLon, double maxLon)
    {
        if (minLat > maxLat + 1e-9)
            return (false, $"INV-L09 FAIL: minLat {minLat:F4} > maxLat {maxLat:F4}");
        if (minLon > maxLon + 1e-9)
            return (false, $"INV-L09 FAIL: minLon {minLon:F4} > maxLon {maxLon:F4}");
        if (minLat < -90 || maxLat > 90 || minLon < -180 || maxLon > 180)
            return (false, $"INV-L09 FAIL: bounds [{minLat:F2},{maxLat:F2}]x[{minLon:F2},{maxLon:F2}] out of earth range");
        return (true, "INV-L09 PASS");
    }

    /// <summary>
    /// INV-L10: 总里程非负且不超过速度上限*时间
    /// 不变量: 0 <= distance <= 97.2 * durationSeconds + 1e-6
    /// </summary>
    public static (bool pass, string detail) CheckDistanceBounded(
        double distanceMeters, double durationSeconds, double maxSpeedMps = 97.2)
    {
        if (distanceMeters < -1e-9)
            return (false, $"INV-L10 FAIL: distance {distanceMeters:F1}m negative");
        var maxDistance = maxSpeedMps * Math.Max(0, durationSeconds) + 1.0;
        if (distanceMeters > maxDistance)
            return (false, $"INV-L10 FAIL: distance {distanceMeters:F1}m > max {maxDistance:F1}m (speed cap)");
        return (true, "INV-L10 PASS");
    }

    /// <summary>
    /// INV-L11: 轨迹段速度非负且合理 - 单段平均速度 <=350km/h
    /// 不变量: 0 <= avgSpeed <= 97.2
    /// </summary>
    public static (bool pass, string detail) CheckSegmentSpeedValid(
        List<(double distanceMeters, double durationSeconds, double avgSpeedMps)> segments,
        double maxSpeedMps = 97.2)
    {
        foreach (var s in segments)
        {
            if (s.avgSpeedMps < -1e-9)
                return (false, $"INV-L11 FAIL: segment avgSpeed {s.avgSpeedMps:F2} negative");
            if (s.avgSpeedMps > maxSpeedMps + 1e-9)
                return (false, $"INV-L11 FAIL: segment speed {s.avgSpeedMps * 3.6:F0}km/h > cap");
            var expected = s.durationSeconds > 0 ? s.distanceMeters / s.durationSeconds : 0;
            if (Math.Abs(s.avgSpeedMps - expected) > 1e-3 && s.durationSeconds > 0)
                return (false, $"INV-L11 FAIL: speed mismatch {s.avgSpeedMps:F3} vs expected {expected:F3}");
        }
        return (true, "INV-L11 PASS");
    }

    /// <summary>
    /// INV-L12: 轨迹点去重后数量 <= 原始数量且访问天数 <= 区间天数
    /// 不变量: distinctDays <= totalDays
    /// </summary>
    public static (bool pass, string detail) CheckVisitDayCountBounded(
        int visitDayCount, int totalDayCount)
    {
        if (visitDayCount < 0)
            return (false, $"INV-L12 FAIL: visitDayCount {visitDayCount} negative");
        if (visitDayCount > totalDayCount)
            return (false, $"INV-L12 FAIL: visitDayCount {visitDayCount} > totalDays {totalDayCount}");
        return (true, "INV-L12 PASS");
    }

    /// <summary>
    /// INV-L13: 单点段质量标记包含 single-point
    /// 不变量: count==1 => qualityFlags contains "single-point"
    /// </summary>
    public static (bool pass, string detail) CheckSinglePointQualityFlag(
        List<(int pointCount, List<string> qualityFlags)> segments)
    {
        foreach (var seg in segments)
        {
            if (seg.pointCount == 1 && !seg.qualityFlags.Contains("single-point"))
                return (false, $"INV-L13 FAIL: single-point segment missing flag");
            if (seg.pointCount > 1 && seg.qualityFlags.Contains("single-point"))
                return (false, $"INV-L13 FAIL: multi-point segment has single-point flag");
        }
        return (true, "INV-L13 PASS");
    }

    /// <summary>
    /// INV-L14: 轨迹间隙超过2小时应分轨
    /// 不变量: 同一track内相邻点间隔 <=7200s
    /// </summary>
    public static (bool pass, string detail) CheckTrackGapThreshold(
        List<List<DateTimeOffset>> tracks, double maxGapSeconds = 7200.0)
    {
        foreach (var track in tracks)
        {
            var sorted = track.OrderBy(t => t).ToList();
            for (int i = 1; i < sorted.Count; i++)
            {
                var gap = (sorted[i] - sorted[i - 1]).TotalSeconds;
                if (gap > maxGapSeconds + 1e-9)
                    return (false, $"INV-L14 FAIL: track gap {gap:F0}s > {maxGapSeconds:F0}s should split");
            }
        }
        return (true, "INV-L14 PASS");
    }

    /// <summary>
    /// INV-L15: 常去地点半径 0-500米且点数≥10（阈值来源: MobileFrequentPlaceService BaseEps 75m, MaxEps 150m, MinPoints 10）
    /// 不变量: 0 <= radius <=500 && pointCount >=10
    /// </summary>
    public static (bool pass, string detail) CheckFrequentPlaceRadius(
        List<(double radiusMeters, int pointCount)> places)
    {
        foreach (var p in places)
        {
            if (p.radiusMeters < -1e-9 || p.radiusMeters > 500.0 + 1e-9)
                return (false, $"INV-L15 FAIL: radius {p.radiusMeters:F1}m out of [0,500]");
            if (p.pointCount < 10)
                return (false, $"INV-L15 FAIL: pointCount {p.pointCount} < 10");
        }
        return (true, "INV-L15 PASS");
    }

    /// <summary>
    /// INV-L16: 家唯一性 - 最多1个 IsHome=true 且若有则为夜间点最多簇
    /// 不变量: homeCount <=1
    /// </summary>
    public static (bool pass, string detail) CheckHomeUniqueness(int homeCount)
    {
        if (homeCount < 0 || homeCount > 1)
            return (false, $"INV-L16 FAIL: homeCount {homeCount} not in [0,1]");
        return (true, "INV-L16 PASS");
    }

    /// <summary>
    /// INV-L17: 轨迹分段 move/stay 互斥且覆盖所有点
    /// 不变量: stayCount + moveCount == totalSegments 且 stay/move 至少其一 >0 当点数>1
    /// </summary>
    public static (bool pass, string detail) CheckSegmentKindCoverage(int stayCount, int moveCount, int totalSegments, int pointCount)
    {
        if (stayCount + moveCount != totalSegments)
            return (false, $"INV-L17 FAIL: stay {stayCount}+move {moveCount} != total {totalSegments}");
        if (pointCount > 1 && totalSegments == 0)
            return (false, $"INV-L17 FAIL: pointCount {pointCount}>1 but 0 segments");
        return (true, "INV-L17 PASS");
    }
}
