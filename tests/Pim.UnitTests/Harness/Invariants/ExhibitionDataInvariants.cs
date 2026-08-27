using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 展览馆数据一致性不变量 — 同一日期多路 API 返回的核心指标必须自洽，与 DataConsistencyInvariants 同源
/// </summary>
public static class ExhibitionDataInvariants
{
    /// <summary>EXH-01: summary.totalForegroundSeconds == heatmap 所有桶 foregroundSeconds 之和（容差 桶数*1s）</summary>
    public static (bool pass, string detail) CheckSummaryEqualsHeatmap(
        double summaryTotal, IReadOnlyList<double> heatmapBuckets)
    {
        var sum = heatmapBuckets.Sum();
        var tol = heatmapBuckets.Count * 1.0;
        var diff = Math.Abs(summaryTotal - sum);
        return diff <= tol
            ? (true, $"EXH-01 PASS: summary {summaryTotal:F1} ≈ heatmap {sum:F1} diff {diff:F1}")
            : (false, $"EXH-01 FAIL: summary {summaryTotal:F1} != heatmap {sum:F1} diff {diff:F1} > {tol:F1}");
    }

    /// <summary>EXH-02: category-share 8类百分比和 ==100% 误差≤1%</summary>
    public static (bool pass, string detail) CheckCategoryShareHundred(IReadOnlyList<double> percentages)
    {
        var sum = percentages.Sum();
        var diff = Math.Abs(sum - 100);
        return diff <= 1.0
            ? (true, $"EXH-02 PASS: sum {sum:F2}%")
            : (false, $"EXH-02 FAIL: sum {sum:F2}% !=100 diff {diff:F2}");
    }

    /// <summary>EXH-03: heatmap 0-23 小时全覆盖，无缺小时</summary>
    public static (bool pass, string detail) CheckHeatmapHoursFull(IReadOnlyList<int> hours)
    {
        var set = hours.ToHashSet();
        var missing = Enumerable.Range(0, 24).Where(h => !set.Contains(h)).ToList();
        return missing.Count == 0
            ? (true, "EXH-03 PASS: 0-23 hours full")
            : (false, $"EXH-03 FAIL: missing hours {string.Join(",", missing)}");
    }

    /// <summary>EXH-04: GPS轨迹连续性 — 相邻点距离 < 5km 且在北京 39.8-40.1/116.2-116.6 范围内</summary>
    public static (bool pass, string detail) CheckGpsContinuity(IReadOnlyList<(double lat, double lng)> points)
    {
        if (points.Count < 2) return (true, "EXH-04 PASS: <2 points");
        for (int i = 1; i < points.Count; i++)
        {
            var (lat1, lng1) = points[i - 1];
            var (lat2, lng2) = points[i];
            if (lat2 < 39.8 || lat2 > 40.1 || lng2 < 116.2 || lng2 > 116.6)
                return (false, $"EXH-04 FAIL: point {i} out of Beijing range {lat2},{lng2}");
            var dLat = lat2 - lat1; var dLng = lng2 - lng1;
            var approxKm = Math.Sqrt(dLat * dLat + dLng * dLng) * 111; // 粗略
            if (approxKm > 5) return (false, $"EXH-04 FAIL: gap {i - 1}->{i} {approxKm:F1}km >5km");
        }
        return (true, $"EXH-04 PASS: {points.Count} points continuous");
    }

    /// <summary>EXH-05: 任务完成率 0-100% 且 7日均线平滑（与 task 完成率联动）</summary>
    public static (bool pass, string detail) CheckTaskRateRange(IReadOnlyList<double> rates)
    {
        if (rates.Any(r => r < 0 || r > 100)) return (false, $"EXH-05 FAIL: rate out of 0-100 {string.Join(",", rates.Where(r => r < 0 || r > 100))}");
        // 7日均线平滑：相邻日变化 <30%
        for (int i = 1; i < rates.Count; i++)
            if (Math.Abs(rates[i] - rates[i - 1]) > 30) return (false, $"EXH-05 FAIL: jump {i - 1}->{i} {rates[i - 1]:F1}%->{rates[i]:F1}% >30%");
        return (true, $"EXH-05 PASS: {rates.Count} rates in 0-100 and smooth");
    }
}
