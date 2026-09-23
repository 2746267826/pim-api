using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pim.Core.Liveness;

/// <summary>
/// 静默与覆盖率的**唯一口径实现**（REQ-13）：纯函数、无数据库/时钟依赖，
/// 服务端摘要、Web「设备存活」页与体检数据项都从这里取数，避免三处各算一套。
/// <para>口径要点：</para>
/// <list type="bullet">
///   <item><description>存活证据 = 心搏（主）∪ 同步批次到达（辅）。使用事件、缺口回补窗口都不算（AC-7.6 / AC-13.3）。</description></item>
///   <item><description>静默判定线只有一条（30 分钟），夜间与白天完全同一套代码（AC-13.2）。</description></item>
///   <item><description>两个覆盖率都暴露分子/分母，可被复算（AC-13.1）。</description></item>
/// </list>
/// </summary>
public static class DeviceLivenessCalculator
{
    private static readonly TimeSpan ExpectedHeartbeatInterval =
        TimeSpan.FromMinutes(DeviceLivenessRules.ExpectedHeartbeatIntervalMinutes);

    /// <summary>
    /// 计算单设备存活摘要。
    /// </summary>
    /// <param name="evidence">存活证据（未排序/含重复时间都可以）。</param>
    /// <param name="causeEvents">死因事件（进程退出 / 强停），用于死因分布。</param>
    /// <param name="rangeStartUtc">区间起点（含）。</param>
    /// <param name="rangeEndUtc">区间终点（不含）。</param>
    public static DeviceLivenessSummary Summarize(
        IEnumerable<LivenessEvidence> evidence,
        IEnumerable<LivenessCauseCount>? causeEvents,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEndUtc), "区间终点必须晚于起点。");
        }

        var ordered = (evidence ?? Array.Empty<LivenessEvidence>())
            .Where(item => item.AtUtc >= rangeStartUtc && item.AtUtc < rangeEndUtc)
            .OrderBy(item => item.AtUtc)
            .ToList();

        var heartbeats = ordered.Count(item => item.Source == LivenessEvidenceSources.Heartbeat);

        var totalHours = CountHourBuckets(rangeStartUtc, rangeEndUtc);
        var observedHours = ordered
            .Select(item => FloorToHour(item.AtUtc))
            .Distinct()
            .Count();
        // 有效证据落在最后一个（可能不完整的）小时桶里时，观测小时数不应超过区间小时数。
        if (observedHours > totalHours)
        {
            observedHours = totalHours;
        }

        double totalMinutes = (rangeEndUtc - rangeStartUtc).TotalMinutes;
        var expectedHeartbeats = Math.Max(
            1,
            (int)Math.Ceiling(totalMinutes / DeviceLivenessRules.ExpectedHeartbeatIntervalMinutes));

        var silences = BuildSilences(ordered, rangeStartUtc, rangeEndUtc, totalMinutes);

        var longest = silences
            .OrderByDescending(window => window.Minutes)
            .ThenBy(window => window.StartUtc)
            .FirstOrDefault();

        var hasData = ordered.Count > 0;
        var hasCriticalSilence = silences.Any(window => window.Severity == SilenceSeverities.Critical);

        var causes = (causeEvents ?? Array.Empty<LivenessCauseCount>())
            .GroupBy(item => item.Cause, StringComparer.Ordinal)
            .Select(group => new LivenessCauseCount(
                group.Key,
                group.First().Label,
                group.Sum(item => item.Count),
                group.Select(item => item.Inference).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Cause, StringComparer.Ordinal)
            .ToList();

        var lastEvidenceAt = ordered.Count > 0 ? ordered[^1].AtUtc : (DateTimeOffset?)null;

        return new DeviceLivenessSummary(
            HasData: hasData,
            Conclusion: BuildConclusion(hasData, longest, silences),
            CoverageByHour: hasData ? Math.Round((double)observedHours / totalHours, 4) : null,
            CoverageByExpectedHeartbeat:
                hasData ? Math.Min(1d, Math.Round((double)heartbeats / expectedHeartbeats, 4)) : null,
            ObservedHours: observedHours,
            TotalHours: totalHours,
            ObservedHeartbeats: heartbeats,
            ExpectedHeartbeats: expectedHeartbeats,
            ExpectedHeartbeatIntervalMinutes: DeviceLivenessRules.ExpectedHeartbeatIntervalMinutes,
            LongestSilenceMinutes: longest is null ? 0 : (int)Math.Floor(longest.Minutes),
            LongestSilenceStartUtc: longest?.StartUtc,
            LongestSilenceEndUtc: longest?.EndUtc,
            LongestSilenceSeverity: longest?.Severity ?? SilenceSeverities.None,
            HasSilenceOverOneHour: hasCriticalSilence
                || (longest is not null && longest.Minutes >= DeviceLivenessRules.SilenceCriticalMinutes),
            Silences: silences,
            Causes: causes,
            LastEventAtUtc: lastEvidenceAt,
            CoverageByHourDefinition: DeviceLivenessRules.CoverageByHourDefinition,
            CoverageByExpectedHeartbeatDefinition: DeviceLivenessRules.CoverageByExpectedHeartbeatDefinition);
    }

    /// <summary>
    /// 把一条「单次静默时长」归类成标色（AC-7.4）。
    /// 判定只看时长，不看它发生在夜间还是白天（AC-13.2）。
    /// </summary>
    public static string ClassifySilence(double minutes)
    {
        if (minutes >= DeviceLivenessRules.SilenceCriticalMinutes)
        {
            return SilenceSeverities.Critical;
        }

        if (minutes >= DeviceLivenessRules.SilenceWarningMinutes)
        {
            return SilenceSeverities.Warning;
        }

        return SilenceSeverities.None;
    }

    /// <summary>
    /// 只有标色为 warning/critical 的静默才会出现在页面上；&lt;30 分钟的缺口不标记（AC-7.4）。
    /// 区间起点到第一颗证据、最后一颗证据到区间终点同样构成静默（设备从此不再醒来）。
    /// </summary>
    private static IReadOnlyList<SilenceWindow> BuildSilences(
        IReadOnlyList<LivenessEvidence> ordered,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        double totalMinutes)
    {
        var windows = new List<SilenceWindow>();

        if (ordered.Count == 0)
        {
            // 整个区间都是静默：只有 ≥30 分钟才标记，否则页面显示为空列表（但 HasData=false 已给出明确空态）。
            AddWindow(windows, rangeStartUtc, rangeEndUtc, totalMinutes);
            return windows;
        }

        AddWindow(windows, rangeStartUtc, ordered[0].AtUtc, (ordered[0].AtUtc - rangeStartUtc).TotalMinutes);

        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1].AtUtc;
            var current = ordered[index].AtUtc;
            AddWindow(windows, previous, current, (current - previous).TotalMinutes);
        }

        AddWindow(windows, ordered[^1].AtUtc, rangeEndUtc, (rangeEndUtc - ordered[^1].AtUtc).TotalMinutes);
        return windows;
    }

    private static void AddWindow(
        List<SilenceWindow> windows,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        double minutes)
    {
        if (endUtc <= startUtc || minutes < DeviceLivenessRules.SilenceWarningMinutes)
        {
            return;
        }

        windows.Add(new SilenceWindow(startUtc, endUtc, minutes, ClassifySilence(minutes)));
    }

    private static string BuildConclusion(
        bool hasData,
        SilenceWindow? longest,
        IReadOnlyList<SilenceWindow> silences)
    {
        if (!hasData)
        {
            // AC-7.5 / AC-8.2 / AC-10.2 / AC-11.4：没有可信存活证据时绝不报平安。
            return DeviceLivenessRules.NoDataConclusion;
        }

        if (longest is null)
        {
            return "存活连续：区间内没有 ≥30 分钟的静默。";
        }

        var longestMinutes = (int)Math.Floor(longest.Minutes);
        if (longest.Severity == SilenceSeverities.Critical)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "存活有缺口：最长静默 {0} 分钟，区间内共 {1} 段 ≥30 分钟静默。",
                longestMinutes,
                silences.Count);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "存活有短暂中断：最长静默 {0} 分钟（未达 1 小时）。",
            longestMinutes);
    }

    private static int CountHourBuckets(DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc)
    {
        var start = FloorToHour(rangeStartUtc);
        var end = FloorToHour(rangeEndUtc);
        var hours = (int)Math.Ceiling((end - start).TotalHours);
        // 终点正好落在整点上时，最后那个整点属于区间之外，不计入分母。
        if (rangeEndUtc == end && hours > 0)
        {
            hours--;
        }

        return Math.Max(1, hours);
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
