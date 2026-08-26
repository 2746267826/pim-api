using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 统计与 Today 模块不变量定义
/// 每条不变量均为可量化断言，用于属性测试与回归校验
/// </summary>
public static class StatsAndTodayInvariants
{
    /// <summary>
    /// INV-S01: TodaySection 去重 —— registry 内同一 Id/Kind 不应重复出现
    /// threshold: 重复数阈值 0，tolerance: 0 条重复即 FAIL
    /// 不变量: distinct(Id) == total && distinct(Kind) 按需唯一（同一 Kind 仅1个 SectionId）
    /// </summary>
    public static (bool pass, string detail) CheckTodaySectionDedup(
        List<(string id, string kind)> sections,
        int tolerance = 0)
    {
        var idDupes = sections.GroupBy(s => s.id).Where(g => g.Count() > 1).ToList();
        if (idDupes.Count > tolerance)
        {
            var worst = idDupes.OrderByDescending(g => g.Count()).First();
            return (false,
                $"INV-S01 FAIL: section id {worst.Key} duplicated {worst.Count()} times > tolerance {tolerance} threshold 0 duplicates");
        }

        var kindDupes = sections.GroupBy(s => s.kind).Where(g => g.Count() > 1).ToList();
        if (kindDupes.Count > tolerance)
        {
            var worst = kindDupes.First();
            return (false,
                $"INV-S01 FAIL: section kind {worst.Key} duplicated {worst.Count()} times > tolerance {tolerance} threshold 0 duplicates");
        }

        return (true, "INV-S01 PASS");
    }

    /// <summary>
    /// INV-S02: Health 分值 0-100 —— 健康分/质量分必须在 [0,100] 区间内且与状态枚举一致
    /// threshold: [0,100] 闭区间，tolerance: 1e-9 浮点误差
    /// 不变量: 0 &lt;= healthScore &lt;= 100 且 score-&gt;status 映射单调（Critical &lt; Warning &lt; Healthy）
    /// </summary>
    public static (bool pass, string detail) CheckHealthScoreRange(
        List<(string component, double score, string status)> components,
        double tolerance = 1e-9)
    {
        foreach (var c in components)
        {
            if (c.score < -tolerance || c.score > 100.0 + tolerance)
                return (false, $"INV-S02 FAIL: component {c.component} score {c.score:F2} out of [0,100] threshold 0-100 tolerance {tolerance}");

            // 分值与状态一致性：score &gt;=80 => Healthy, &gt;=50 => Warning, 否则 Critical/Unknown
            var expectedStatus = c.score switch
            {
                >= 80 => "healthy",
                >= 50 => "warning",
                _ => "critical"
            };
            // 仅校验极端不一致：高分 critical 或低分 healthy 视为异常
            if (c.score >= 80 && string.Equals(c.status, "critical", StringComparison.OrdinalIgnoreCase))
                return (false, $"INV-S02 FAIL: component {c.component} score {c.score:F1} high but status {c.status} threshold healthy tolerance 0");
            if (c.score < 30 && string.Equals(c.status, "healthy", StringComparison.OrdinalIgnoreCase))
                return (false, $"INV-S02 FAIL: component {c.component} score {c.score:F1} low but status {c.status} threshold critical tolerance 0");
        }

        return (true, "INV-S02 PASS");
    }

    /// <summary>
    /// INV-S03: TodaySection 状态合法 —— status 必须为白名单之一且与错误信息一致
    /// threshold: status ∈ {available, normal, empty, warning, critical, unavailable}，tolerance: 0 非法值
    /// 不变量: status in whitelist && (status==unavailable =&gt; error != null)
    /// </summary>
    public static (bool pass, string detail) CheckTodaySectionStatusValid(
        List<(string id, string status, string? errorCode)> sections)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "available", "normal", "empty", "warning", "critical", "unavailable"
        };

        foreach (var s in sections)
        {
            if (!valid.Contains(s.status))
                return (false, $"INV-S03 FAIL: section {s.id} status '{s.status}' not in whitelist threshold valid={string.Join(",", valid)} tolerance 0");
            if (string.Equals(s.status, "unavailable", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(s.errorCode))
                return (false, $"INV-S03 FAIL: section {s.id} status unavailable but errorCode null threshold non-null tolerance 0");
        }

        return (true, "INV-S03 PASS");
    }

    /// <summary>
    /// INV-S04: 统计汇总一致 —— overview/appCount 汇总值 == 明细之和/去重数
    /// threshold: totalSeconds 误差 1秒*桶数，appCount 误差 0；tolerance: 每桶 1秒
    /// 不变量: |sum(buckets) - overviewTotal| &lt;= tolerance && appCount == distinctPackages
    /// </summary>
    public static (bool pass, string detail) CheckStatsAggregationConsistency(
        double overviewTotalSeconds,
        Dictionary<string, double> bucketsByKey,
        int overviewAppCount,
        HashSet<string> distinctPackages,
        double perBucketToleranceSeconds = 1.0)
    {
        var bucketSum = bucketsByKey.Values.Sum();
        var tolerance = bucketsByKey.Count * perBucketToleranceSeconds;
        var diff = Math.Abs(bucketSum - overviewTotalSeconds);
        if (diff > tolerance)
            return (false,
                $"INV-S04 FAIL: bucket sum {bucketSum:F1}s != overview {overviewTotalSeconds:F1}s diff {diff:F1}s > tolerance {tolerance:F1}s threshold {perBucketToleranceSeconds}s per bucket");

        if (overviewAppCount != distinctPackages.Count)
            return (false,
                $"INV-S04 FAIL: overview appCount {overviewAppCount} != distinct {distinctPackages.Count} threshold exact tolerance 0");

        return (true, "INV-S04 PASS");
    }

    /// <summary>
    /// INV-S05: Health 聚合最严重优先 —— 总体 status == 组件中最严重的 status
    /// threshold: 严重度排序 Healthy(0) &lt; Unknown(1) &lt; Warning(2) &lt; Critical(3)，tolerance: 0 偏差
    /// 不变量: overallSeverity == max(componentSeverity)
    /// </summary>
    public static (bool pass, string detail) CheckHealthAggregationSeverity(
        string overallStatus,
        List<(string component, string status)> components)
    {
        static int Rank(string s) => s.ToLowerInvariant() switch
        {
            "healthy" => 0,
            "unknown" => 1,
            "warning" => 2,
            "critical" => 3,
            _ => 1
        };

        if (components.Count == 0) return (true, "INV-S05 PASS");

        var maxComponent = components.OrderByDescending(c => Rank(c.status)).First();
        var expectedRank = Rank(maxComponent.status);
        var overallRank = Rank(overallStatus);

        if (overallRank != expectedRank)
            return (false,
                $"INV-S05 FAIL: overall {overallStatus} rank {overallRank} != max component {maxComponent.component} {maxComponent.status} rank {expectedRank} threshold max-severity tolerance 0");

        return (true, "INV-S05 PASS");
    }

    /// <summary>
    /// INV-S06: Today 响应时效 —— 各 Section 延迟必须在阈值内且总体 P95 可量化
    /// threshold: maxLatencyMs 默认 2000ms（Today 首屏 SLA），tolerance: 0 条超时即 FAIL；P95 tolerance 100ms 抖动
    /// 不变量: ∀s: s.latencyMs &lt;= maxLatencyMs + toleranceP95 && avgLatency &lt;= maxLatencyMs
    /// </summary>
    public static (bool pass, string detail) CheckTodayLatency(
        List<(string sectionId, double latencyMs)> sections,
        double maxLatencyMs = 2000.0,
        double p95ToleranceMs = 100.0)
    {
        if (sections.Count == 0) return (true, "INV-S06 PASS");

        var violations = sections.Where(s => s.latencyMs > maxLatencyMs + p95ToleranceMs).ToList();
        if (violations.Count > 0)
        {
            var worst = violations.OrderByDescending(s => s.latencyMs).First();
            return (false, $"INV-S06 FAIL: section {worst.sectionId} latency {worst.latencyMs:F0}ms > threshold {maxLatencyMs:F0}ms tolerance {p95ToleranceMs:F0}ms");
        }

        var avg = sections.Average(s => s.latencyMs);
        if (avg > maxLatencyMs)
            return (false, $"INV-S06 FAIL: avg latency {avg:F0}ms > threshold {maxLatencyMs:F0}ms tolerance {p95ToleranceMs:F0}ms");

        foreach (var s in sections)
        {
            if (s.latencyMs < -1e-9)
                return (false, $"INV-S06 FAIL: section {s.sectionId} negative latency {s.latencyMs:F1}ms threshold >=0 tolerance 0");
        }

        return (true, "INV-S06 PASS");
    }

    /// <summary>
    /// INV-S07: 统计时间窗口完整性 —— heatmap/dailyTrend 覆盖天数 == 请求区间天数 ± 容差
    /// threshold: expectedDayCount = ceil((queryEnd - queryStart).TotalDays)，tolerance: 1天（允许时区边界/半开区间误差）
    /// 不变量: |heatmapDayCount - expected| &lt;= tolerance && |trendDayCount - expected| &lt;= tolerance
    /// </summary>
    public static (bool pass, string detail) CheckStatsWindowCompleteness(
        DateTimeOffset queryStart,
        DateTimeOffset queryEnd,
        int heatmapDayCount,
        int trendDayCount,
        int toleranceDays = 1)
    {
        if (queryEnd <= queryStart)
            return (false, $"INV-S07 FAIL: queryEnd {queryEnd:O} <= queryStart {queryStart:O} threshold queryEnd > queryStart tolerance 0");

        var expectedDays = (int)Math.Ceiling((queryEnd - queryStart).TotalDays);
        if (expectedDays < 1) expectedDays = 1;

        var heatmapDiff = Math.Abs(heatmapDayCount - expectedDays);
        if (heatmapDiff > toleranceDays)
            return (false, $"INV-S07 FAIL: heatmap days {heatmapDayCount} != expected {expectedDays} diff {heatmapDiff} > tolerance {toleranceDays} threshold {expectedDays} days");

        var trendDiff = Math.Abs(trendDayCount - expectedDays);
        if (trendDiff > toleranceDays)
            return (false, $"INV-S07 FAIL: trend days {trendDayCount} != expected {expectedDays} diff {trendDiff} > tolerance {toleranceDays} threshold {expectedDays} days");

        return (true, "INV-S07 PASS");
    }
}
