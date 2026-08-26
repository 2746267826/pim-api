using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 手机时间类不变量定义
/// 每条不变量都是一个可验证的断言，用于属性测试
/// </summary>
public static class MobileTimeInvariants
{
    // ========== 基础物理约束 ==========

    /// <summary>
    /// INV-M01: 单设备单小时去重后时长 <= 3600秒 * (1 + 容差)
    /// 容差5%给四舍五入和边界重叠
    /// </summary>
    public static (bool pass, string detail) CheckSingleHourCap(
        Dictionary<int, double> hourBuckets, double tolerance = 0.05)
    {
        var maxAllowed = 3600.0 * (1 + tolerance);
        var violations = hourBuckets
            .Where(kv => kv.Value > maxAllowed)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.OrderByDescending(v => v.Value).First();
            return (false,
                $"INV-M01 FAIL: hour {worst.Key} has {worst.Value:F1}s, max allowed {maxAllowed:F1}s");
        }
        return (true, "INV-M01 PASS");
    }

    /// <summary>
    /// INV-M02: 单设备单天去重后时长 <= 86400秒 * (1 + 容差)
    /// </summary>
    public static (bool pass, string detail) CheckSingleDayCap(
        Dictionary<string, double> dailyTotals, double tolerance = 0.05)
    {
        var maxAllowed = 86400.0 * (1 + tolerance);
        var violations = dailyTotals
            .Where(kv => kv.Value > maxAllowed)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.OrderByDescending(v => v.Value).First();
            return (false,
                $"INV-M02 FAIL: day {worst.Key} has {worst.Value:F1}s, max allowed {maxAllowed:F1}s");
        }
        return (true, "INV-M02 PASS");
    }

    /// <summary>
    /// INV-M03: 总时长 <= 查询区间长度
    /// </summary>
    public static (bool pass, string detail) CheckTotalNotExceedRange(
        double totalSeconds, TimeSpan queryRange)
    {
        var maxAllowed = queryRange.TotalSeconds;
        if (totalSeconds > maxAllowed)
        {
            return (false,
                $"INV-M03 FAIL: total {totalSeconds:F1}s exceeds range {maxAllowed:F1}s");
        }
        return (true, "INV-M03 PASS");
    }

    // ========== 数学一致性 ==========

    /// <summary>
    /// INV-M04: 小时桶之和 == 总时长（误差 <= 1秒 * 桶数量）
    /// </summary>
    public static (bool pass, string detail) CheckBucketsSumEqualTotal(
        Dictionary<int, double> hourBuckets, double totalSeconds, int bucketCount)
    {
        var bucketSum = hourBuckets.Values.Sum();
        var tolerance = bucketCount * 1.0; // 每桶允许1秒舍入误差
        var diff = Math.Abs(bucketSum - totalSeconds);

        if (diff > tolerance)
        {
            return (false,
                $"INV-M04 FAIL: buckets sum {bucketSum:F1} != total {totalSeconds:F1}, diff {diff:F1} > tolerance {tolerance:F1}");
        }
        return (true, "INV-M04 PASS");
    }

    /// <summary>
    /// INV-M05: 分类桶之和 == 总时长（误差 <= 1秒 * 分类数）
    /// </summary>
    public static (bool pass, string detail) CheckCategoryBucketsSumEqualTotal(
        Dictionary<string, double> categoryBuckets, double totalSeconds)
    {
        var categorySum = categoryBuckets.Values.Sum();
        var tolerance = categoryBuckets.Count * 1.0;
        var diff = Math.Abs(categorySum - totalSeconds);

        if (diff > tolerance)
        {
            return (false,
                $"INV-M05 FAIL: category sum {categorySum:F1} != total {totalSeconds:F1}, diff {diff:F1} > tolerance {tolerance:F1}");
        }
        return (true, "INV-M05 PASS");
    }

    /// <summary>
    /// INV-M06: fallback summary 按小时去重后，同一app同一小时只取一条
    /// </summary>
    public static (bool pass, string detail) CheckDeduplicatedSummaries(
        List<(string packageName, int hour, double totalTimeMs)> summaries)
    {
        var duplicates = summaries
            .GroupBy(s => (s.packageName, s.hour))
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Any())
        {
            var worst = duplicates.First();
            return (false,
                $"INV-M06 FAIL: {worst.Key.packageName} hour {worst.Key.hour} has {worst.Count()} duplicates");
        }
        return (true, "INV-M06 PASS");
    }

    /// <summary>
    /// INV-M07: 非负约束 - 所有时长 >= 0
    /// </summary>
    public static (bool pass, string detail) CheckNonNegative(
        Dictionary<int, double> hourBuckets)
    {
        var negatives = hourBuckets.Where(kv => kv.Value < 0).ToList();
        if (negatives.Any())
        {
            var worst = negatives.First();
            return (false,
                $"INV-M07 FAIL: hour {worst.Key} has negative value {worst.Value:F1}s");
        }
        return (true, "INV-M07 PASS");
    }

    /// <summary>
    /// INV-M08: 单session时长 <= 24小时（标记为异常的阈值）
    /// </summary>
    public static (bool pass, string detail) CheckSingleSessionCap(
        List<(string packageName, double durationMs)> sessions,
        double maxDurationMs = 8 * 3600 * 1000) // 8小时
    {
        var violations = sessions
            .Where(s => s.durationMs > maxDurationMs)
            .ToList();

        if (violations.Any())
        {
            var worst = violations.OrderByDescending(v => v.durationMs).First();
            return (false,
                $"INV-M08 FAIL: session {worst.packageName} has {worst.durationMs / 3600000:F1}h, max {maxDurationMs / 3600000:F1}h");
        }
        return (true, "INV-M08 PASS");
    }

    /// <summary>
    /// INV-M09: 所有App分类必须在预定义的LifeCategories内
    /// </summary>
    public static (bool pass, string detail) CheckValidCategories(
        Dictionary<string, string> appCategories)
    {
        var validCategories = new HashSet<string>
        {
            "聊天", "视频", "音乐", "社交", "新闻", "工具", "游戏",
            "教育", "购物", "金融", "出行", "健康", "办公", "系统", "其他"
        };

        var invalid = appCategories
            .Where(kv => !validCategories.Contains(kv.Value))
            .ToList();

        if (invalid.Any())
        {
            var worst = invalid.First();
            return (false,
                $"INV-M09 FAIL: app {worst.Key} has invalid category '{worst.Value}'");
        }
        return (true, "INV-M09 PASS");
    }

    // ========== 扩展不变量（Phase1新增）==========

    /// <summary>
    /// INV-M10: Session DurationMs 一致性 - DurationMs == (EndUtc - StartUtc).TotalMilliseconds 容差1ms
    /// 不变量: |durationMs - (end-start).TotalMilliseconds| <= 1
    /// </summary>
    public static (bool pass, string detail) CheckSessionDurationConsistency(
        List<(string packageName, DateTimeOffset start, DateTimeOffset? end, long durationMs)> sessions)
    {
        foreach (var s in sessions)
        {
            if (s.end is null) continue;
            var expected = (s.end.Value - s.start).TotalMilliseconds;
            var diff = Math.Abs(s.durationMs - expected);
            if (diff > 1.0)
                return (false, $"INV-M10 FAIL: session {s.packageName} durationMs {s.durationMs} != expected {expected:F0}, diff {diff:F1}");
        }
        return (true, "INV-M10 PASS");
    }

    /// <summary>
    /// INV-M11: Prorated fallback 秒数不超过原始 TotalTimeVisibleMs/1000
    /// 不变量: proratedSeconds <= originalMs/1000 + 1
    /// </summary>
    public static (bool pass, string detail) CheckProratedNotExceedOriginal(
        List<(string packageName, long totalTimeVisibleMs, long proratedSeconds)> summaries)
    {
        foreach (var s in summaries)
        {
            var max = s.totalTimeVisibleMs / 1000.0 + 1.0;
            if (s.proratedSeconds > max)
                return (false, $"INV-M11 FAIL: {s.packageName} prorated {s.proratedSeconds}s > original {max:F1}s");
        }
        return (true, "INV-M11 PASS");
    }

    /// <summary>
    /// INV-M12: 完整性 Completeness 在 [0,1] 区间内
    /// 不变量: 0 <= completeness <= 1
    /// 容差: 允许浮点误差1e-9
    /// </summary>
    public static (bool pass, string detail) CheckCompletenessRange(double completeness)
    {
        if (completeness < -1e-9 || completeness > 1.0 + 1e-9)
            return (false, $"INV-M12 FAIL: completeness {completeness:F3} out of [0,1]");
        return (true, "INV-M12 PASS");
    }

    /// <summary>
    /// INV-M13: TopApps 排行按前景时长降序
    /// 不变量: ranking[i].seconds >= ranking[i+1].seconds for all i
    /// </summary>
    public static (bool pass, string detail) CheckRankingMonotonic(
        List<(string packageName, double foregroundSeconds)> ranking)
    {
        for (int i = 1; i < ranking.Count; i++)
        {
            if (ranking[i].foregroundSeconds > ranking[i - 1].foregroundSeconds + 1e-9)
                return (false, $"INV-M13 FAIL: ranking not monotonic at index {i}: {ranking[i-1].foregroundSeconds:F1} < {ranking[i].foregroundSeconds:F1}");
        }
        return (true, "INV-M13 PASS");
    }

    /// <summary>
    /// INV-M14: 每小时去重后秒数不超过桶长度（3600s）且非负
    /// 不变量: 0 <= bucketSeconds <= bucketDurationSeconds * 1.05
    /// </summary>
    public static (bool pass, string detail) CheckBucketSecondsBounded(
        Dictionary<string, double> bucketSeconds, double bucketDurationSeconds = 3600.0, double tolerance = 0.05)
    {
        var maxAllowed = bucketDurationSeconds * (1 + tolerance);
        foreach (var kv in bucketSeconds)
        {
            if (kv.Value < -1e-9)
                return (false, $"INV-M14 FAIL: bucket {kv.Key} negative {kv.Value:F1}s");
            if (kv.Value > maxAllowed)
                return (false, $"INV-M14 FAIL: bucket {kv.Key} has {kv.Value:F1}s > max {maxAllowed:F1}s");
        }
        return (true, "INV-M14 PASS");
    }

    /// <summary>
    /// INV-M15: DailyAverage 一致性 - dailyAverage * dayCount 接近 total
    /// 不变量: |dailyAverage * dayCount - total| <= dayCount * 1
    /// </summary>
    public static (bool pass, string detail) CheckDailyAverageConsistency(long totalSeconds, long dailyAverageSeconds, int dayCount)
    {
        if (dayCount <= 0) return (true, "INV-M15 PASS");
        var expected = dailyAverageSeconds * dayCount;
        var diff = Math.Abs(expected - totalSeconds);
        var tolerance = dayCount * 1.0 + dayCount; // 1s per day + rounding
        if (diff > tolerance)
            return (false, $"INV-M15 FAIL: dailyAverage {dailyAverageSeconds} * {dayCount} = {expected} != total {totalSeconds}, diff {diff:F1} > {tolerance:F1}");
        return (true, "INV-M15 PASS");
    }

    /// <summary>
    /// INV-M16: TotalTimeVisibleMs 非负且单条不超过8小时 (阈值来源: MobileUsageAggregationService 异常过滤 8h)
    /// 不变量: 0 <= totalTimeVisibleMs <= 8*3600*1000
    /// </summary>
    public static (bool pass, string detail) CheckTotalTimeVisibleMsBounded(
        List<(string packageName, long totalTimeVisibleMs)> summaries, long maxMs = 8L * 3600 * 1000)
    {
        foreach (var s in summaries)
        {
            if (s.totalTimeVisibleMs < 0)
                return (false, $"INV-M16 FAIL: {s.packageName} totalTimeVisibleMs {s.totalTimeVisibleMs} negative");
            if (s.totalTimeVisibleMs > maxMs)
                return (false, $"INV-M16 FAIL: {s.packageName} totalTimeVisibleMs {s.totalTimeVisibleMs} > max {maxMs} (8h)");
        }
        return (true, "INV-M16 PASS");
    }

    /// <summary>
    /// INV-M17: SourceKind 合法性 - 必须为已知枚举值之一
    /// 不变量: sourceKind ∈ {queryUsageStats, fallback, summary, usage-stats-fallback, ...} 且长度 (0,64]
    /// 阈值: 长度>0 && <=64 且匹配白名单或含 fallback/summary/events
    /// </summary>
    public static (bool pass, string detail) CheckSourceKindValid(List<string> sourceKinds)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "queryUsageStats", "fallback", "summary", "events", "usage-stats-fallback", "usage-summary"
        };
        foreach (var sk in sourceKinds)
        {
            if (string.IsNullOrWhiteSpace(sk) || sk.Length > 64)
                return (false, $"INV-M17 FAIL: sourceKind '{sk}' invalid length");
            if (!valid.Contains(sk) && !sk.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                && !sk.Contains("summary", StringComparison.OrdinalIgnoreCase) && !sk.Contains("events", StringComparison.OrdinalIgnoreCase))
                return (false, $"INV-M17 FAIL: sourceKind '{sk}' not in valid set");
        }
        return (true, "INV-M17 PASS");
    }
}
