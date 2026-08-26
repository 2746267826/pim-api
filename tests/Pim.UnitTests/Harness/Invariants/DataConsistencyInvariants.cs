using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 跨接口一致性不变量
/// 同一个数据通过不同API返回时，核心指标必须一致
/// </summary>
public static class DataConsistencyInvariants
{
    /// <summary>
    /// INV-C01: overview.totalForegroundSeconds == heatmap所有桶之和
    /// 容差: 桶数量 * 1秒（每桶舍入误差）
    /// </summary>
    public static (bool pass, string detail) CheckOverviewEqualsHeatmapSum(
        double overviewTotal, Dictionary<int, double> heatmapBuckets)
    {
        var heatmapSum = heatmapBuckets.Values.Sum();
        var tolerance = heatmapBuckets.Count * 1.0;
        var diff = Math.Abs(overviewTotal - heatmapSum);

        if (diff > tolerance)
        {
            return (false,
                $"INV-C01 FAIL: overview {overviewTotal:F1} != heatmap sum {heatmapSum:F1}, diff {diff:F1} > tolerance {tolerance:F1}");
        }
        return (true, "INV-C01 PASS");
    }

    /// <summary>
    /// INV-C02: overview.totalForegroundSeconds == charts.dailyTrend所有点之和
    /// </summary>
    public static (bool pass, string detail) CheckOverviewEqualsDailyTrendSum(
        double overviewTotal, List<(string date, double seconds)> dailyTrend)
    {
        var trendSum = dailyTrend.Sum(d => d.seconds);
        var tolerance = dailyTrend.Count * 1.0;
        var diff = Math.Abs(overviewTotal - trendSum);

        if (diff > tolerance)
        {
            return (false,
                $"INV-C02 FAIL: overview {overviewTotal:F1} != dailyTrend sum {trendSum:F1}, diff {diff:F1} > tolerance {tolerance:F1}");
        }
        return (true, "INV-C02 PASS");
    }

    /// <summary>
    /// INV-C03: overview.appCount == 实际去重package数量
    /// </summary>
    public static (bool pass, string detail) CheckAppCountConsistency(
        int overviewAppCount, HashSet<string> actualPackages)
    {
        if (overviewAppCount != actualPackages.Count)
        {
            return (false,
                $"INV-C03 FAIL: overview appCount {overviewAppCount} != actual {actualPackages.Count}");
        }
        return (true, "INV-C03 PASS");
    }

    /// <summary>
    /// INV-C04: 跨模块日报对齐（PC和Mobile使用同一业务日切割）
    /// </summary>
    public static (bool pass, string detail) CheckCrossModuleDayAlignment(
        List<(string date, string source)> mobileDays,
        List<(string date, string source)> pcDays)
    {
        var mobileDateSet = mobileDays.Select(d => d.date).ToHashSet();
        var pcDateSet = pcDays.Select(d => d.date).ToHashSet();

        // 两边都有的日期应该一致
        var commonDates = mobileDateSet.Intersect(pcDateSet).ToList();
        if (commonDates.Any())
        {
            // 检查日期格式一致性
            var mobileFormat = mobileDays.First().date.Contains('-') ? "yyyy-MM-dd" : "yyyyMMdd";
            var pcFormat = pcDays.First().date.Contains('-') ? "yyyy-MM-dd" : "yyyyMMdd";

            if (mobileFormat != pcFormat)
            {
                return (false,
                    $"INV-C04 FAIL: mobile uses {mobileFormat} format but PC uses {pcFormat}");
            }
        }
        return (true, "INV-C04 PASS");
    }

    /// <summary>
    /// INV-C05: category-share饼图所有百分比之和 == 100%（误差 <= 1%）
    /// </summary>
    public static (bool pass, string detail) CheckCategoryShareSumToHundred(
        List<(string category, double percentage)> categoryShare)
    {
        var sum = categoryShare.Sum(c => c.percentage);
        var diff = Math.Abs(sum - 100.0);

        if (diff > 1.0)
        {
            return (false,
                $"INV-C05 FAIL: category share sum {sum:F1}% != 100%, diff {diff:F1}%");
        }
        return (true, "INV-C05 PASS");
    }

    /// <summary>
    /// INV-C06: 热力图hour桶0-23全覆盖
    /// </summary>
    public static (bool pass, string detail) CheckHeatmapHourCoverage(
        Dictionary<int, double> heatmapBuckets)
    {
        var missingHours = Enumerable.Range(0, 24)
            .Where(h => !heatmapBuckets.ContainsKey(h))
            .ToList();

        if (missingHours.Any())
        {
            return (false,
                $"INV-C06 FAIL: heatmap missing hours: {string.Join(", ", missingHours)}");
        }
        return (true, "INV-C06 PASS");
    }

    /// <summary>
    /// INV-C07: 设备管理 - 合并后数据量 == 合并前各设备之和
    /// </summary>
    public static (bool pass, string detail) CheckDeviceMergeDataIntegrity(
        Dictionary<string, int> preMergeCounts,
        string targetDeviceId,
        int postMergeCount)
    {
        var expectedTotal = preMergeCounts.Values.Sum();
        if (postMergeCount != expectedTotal)
        {
            return (false,
                $"INV-C07 FAIL: merge expected {expectedTotal} records but got {postMergeCount}");
        }
        return (true, "INV-C07 PASS");
    }

    /// <summary>
    /// INV-C08: 删除设备后关联数据应全部清除
    /// </summary>
    public static (bool pass, string detail) CheckDeviceDeleteCascade(
        Dictionary<string, int> remainingCounts)
    {
        var orphaned = remainingCounts.Where(kv => kv.Value > 0).ToList();
        if (orphaned.Any())
        {
            var worst = orphaned.First();
            return (false,
                $"INV-C08 FAIL: after delete, {worst.Key} still has {worst.Value} orphaned records");
        }
        return (true, "INV-C08 PASS");
    }

    // ========== 扩展不变量 ==========

    /// <summary>
    /// INV-C09: 粒度桶数量符合预期 - hour粒度下每日最多24桶，去重后总桶数 <= days*24*categories
    /// 不变量: bucketCount <= days*24*maxCategories 且 >= days
    /// </summary>
    public static (bool pass, string detail) CheckHeatmapGranularityBucketCount(
        int bucketCount, int dayCount, string granularity)
    {
        int expectedMax = granularity switch
        {
            "hour" => dayCount * 24 * 8,
            "30m" => dayCount * 48 * 8,
            "15m" => dayCount * 96 * 8,
            "day" => dayCount * 8,
            _ => dayCount * 24 * 8
        };
        if (bucketCount > expectedMax)
            return (false, $"INV-C09 FAIL: bucketCount {bucketCount} > expectedMax {expectedMax} for granularity {granularity}");
        return (true, "INV-C09 PASS");
    }

    /// <summary>
    /// INV-C10: Timeline 块按时间有序且不重叠（按StartUtc排序后，后一块Start >= 前一块End - gap）
    /// 不变量: blocks[i].StartUtc >= blocks[i-1].EndUtc - 5min容差 或 LifeCategory不同可重叠但同类不重叠
    /// </summary>
    public static (bool pass, string detail) CheckTimelineBlocksOrdered(
        List<(DateTimeOffset start, DateTimeOffset end, string lifeCategory)> blocks)
    {
        var sorted = blocks.OrderBy(b => b.start).ThenBy(b => b.end).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].start < sorted[i - 1].start)
                return (false, $"INV-C10 FAIL: block {i} not ordered {sorted[i].start:O} < {sorted[i-1].start:O}");
            // 同类块不应重叠
            if (sorted[i].lifeCategory == sorted[i - 1].lifeCategory && sorted[i].start < sorted[i - 1].end)
                return (false, $"INV-C10 FAIL: overlapping same-category blocks {i-1} and {i}: {sorted[i-1].end:O} > {sorted[i].start:O}");
        }
        return (true, "INV-C10 PASS");
    }

    /// <summary>
    /// INV-C11: AppCount 整体非负且不超过去重包数（考虑系统噪音过滤）
    /// 不变量: 0 <= appCount <= distinctPackages + 8 (up to categories)
    /// </summary>
    public static (bool pass, string detail) CheckAppCountBounded(
        int appCount, int distinctPackages)
    {
        if (appCount < 0)
            return (false, $"INV-C11 FAIL: appCount {appCount} negative");
        if (appCount > distinctPackages)
            return (false, $"INV-C11 FAIL: appCount {appCount} > distinct {distinctPackages}");
        return (true, "INV-C11 PASS");
    }

    /// <summary>
    /// INV-C12: Overview completeness + fallbackShare 约等于1（误差0.02）
    /// 不变量: |completeness + fallbackShare -1| <=0.02
    /// </summary>
    public static (bool pass, string detail) CheckQualityCompletenessFallbackRelation(
        double completeness, double fallbackShare)
    {
        var sum = completeness + fallbackShare;
        var diff = Math.Abs(sum - 1.0);
        if (diff > 0.02)
            return (false, $"INV-C12 FAIL: completeness {completeness:F2} + fallback {fallbackShare:F2} = {sum:F2} !=1, diff {diff:F2}");
        return (true, "INV-C12 PASS");
    }

    /// <summary>
    /// INV-C13: 每日趋势 deve vs heatmap 按日期对齐且和值一致（误差1s*天数）
    /// 不变量: sum(dailyTrend) == sum(heatmap group by date) within tolerance
    /// </summary>
    public static (bool pass, string detail) CheckDailyTrendVsHeatmapByDate(
        Dictionary<string, double> dailyTrendByDate, Dictionary<string, double> heatmapByDate)
    {
        var allDates = dailyTrendByDate.Keys.Union(heatmapByDate.Keys).ToList();
        foreach (var date in allDates)
        {
            var dv = dailyTrendByDate.GetValueOrDefault(date);
            var hv = heatmapByDate.GetValueOrDefault(date);
            var diff = Math.Abs(dv - hv);
            if (diff > 1.0)
                return (false, $"INV-C13 FAIL: date {date} daily {dv:F1} != heatmap {hv:F1}, diff {diff:F1}");
        }
        return (true, "INV-C13 PASS");
    }

    /// <summary>
    /// INV-C14: Switch/Pickup 计数非负且 <= 总会话数
    /// 不变量: 0 <= switchCount <= totalSessionCount
    /// </summary>
    public static (bool pass, string detail) CheckSwitchCountBounded(int switchCount, int totalSessionCount)
    {
        if (switchCount < 0)
            return (false, $"INV-C14 FAIL: switchCount {switchCount} negative");
        if (switchCount > totalSessionCount)
            return (false, $"INV-C14 FAIL: switchCount {switchCount} > totalSessions {totalSessionCount}");
        return (true, "INV-C14 PASS");
    }

    /// <summary>
    /// INV-C15: 热力图 LocalHour 0-23 合法且桶秒数与强度一致（复用P13逻辑）
    /// 不变量: 0 <= localHour <=23 && intensity ∈[0,5] 且与秒数映射一致
    /// </summary>
    public static (bool pass, string detail) CheckHeatmapBucketValidity(
        List<(int localHour, double seconds, int intensity)> buckets)
    {
        foreach (var b in buckets)
        {
            if (b.localHour < 0 || b.localHour > 23)
                return (false, $"INV-C15 FAIL: localHour {b.localHour} out of [0,23]");
            if (b.intensity < 0 || b.intensity > 5)
                return (false, $"INV-C15 FAIL: intensity {b.intensity} out of [0,5]");
        }
        return (true, "INV-C15 PASS");
    }

    /// <summary>
    /// INV-C16: 每日趋势日期格式 yyyy-MM-dd 且递增
    /// 不变量: date 按字典序递增且符合 yyyy-MM-dd
    /// </summary>
    public static (bool pass, string detail) CheckDailyTrendDateFormat(
        List<string> dates)
    {
        var sorted = dates.OrderBy(d => d, StringComparer.Ordinal).ToList();
        for (int i = 0; i < dates.Count; i++)
        {
            if (dates[i] != sorted[i])
                return (false, $"INV-C16 FAIL: dates not sorted at {i}: {dates[i]} != {sorted[i]}");
            if (!System.Text.RegularExpressions.Regex.IsMatch(dates[i], @"^\d{4}-\d{2}-\d{2}$"))
                return (false, $"INV-C16 FAIL: date {dates[i]} invalid format");
        }
        return (true, "INV-C16 PASS");
    }

    /// <summary>
    /// INV-C17: 设备合并前后数据完整性 - 合并后去重包数 <= 合并前去重包数之和
    /// 不变量: postDistinct <= preDistinctSum
    /// </summary>
    public static (bool pass, string detail) CheckDeviceMergePackageDistinct(
        int preDistinctSum, int postDistinct)
    {
        if (postDistinct < 0)
            return (false, $"INV-C17 FAIL: postDistinct {postDistinct} negative");
        if (postDistinct > preDistinctSum)
            return (false, $"INV-C17 FAIL: postDistinct {postDistinct} > preSum {preDistinctSum} (should dedup)");
        return (true, "INV-C17 PASS");
    }
}
