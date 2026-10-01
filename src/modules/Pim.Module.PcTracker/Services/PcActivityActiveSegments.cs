using System.Globalization;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

/// <summary>
/// 把明细记录消解成「互不重叠的活跃段」并裁剪到给定窗口（#363）。
/// <para>
/// 仓库里只有唯一一套重叠消解口径 <see cref="PcActivityOverlapResolver"/>
/// （优先级 <c>window &gt; web-page &gt; input-minute</c>，<c>gap/idle/afk</c> 不参与）。
/// 本类只是把明细记录（<see cref="PcDetailRecord"/>）适配成它的候选，
/// 再按调用方的窗口裁剪，绝不自创优先级或另写一套合并逻辑。
/// </para>
/// <para>
/// 每个段恰好归属一条记录：同一时刻只有一条记录计费，因此
/// 「段时长合计 = 窗口内所有候选区间的并集长度」，
/// 逐块统计天然满足「不超过块时长 / 单日不超过 24 小时」。
/// </para>
/// </summary>
public static class PcActivityActiveSegments
{
    /// <summary>消解并裁剪后的活跃段；<see cref="Record"/> 是该段的归属记录。</summary>
    public readonly record struct Segment(DateTimeOffset Start, DateTimeOffset End, PcDetailRecord Record);

    /// <summary>预解析后的记录区间（已过滤未活动/非正时长），可跨多个窗口复用，避免重复解析时间字符串。</summary>
    public readonly record struct Interval(PcDetailRecord Record, DateTimeOffset Start, DateTimeOffset End);

    /// <summary>
    /// 采样对之间的间隔上限：超过它即为「采样断档」，与
    /// <see cref="KeystatsDeltaCalculator.GapThresholdMinutes"/> 同源。
    /// </summary>
    public static readonly TimeSpan MaxSamplingInterval =
        TimeSpan.FromMinutes(KeystatsDeltaCalculator.GapThresholdMinutes);

    /// <summary>
    /// 跨断档的 input-minute 记录最多计入的活跃时长。
    /// <para>采样只证明「这段空白里有过输入」，不能证明空白本身就是活动时段；
    /// 整段计入会让活跃分钟随请求范围膨胀（REQ-1 / #375：同一业务日 722 → 1145 → 1440）。</para>
    /// </summary>
    public static readonly TimeSpan SamplingGapActiveBudget = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 把明细记录解析成可复用的区间列表（过滤 <c>gap/idle/afk</c> 与非正时长）。
    /// activity-analysis 要按 24 个块反复取数，解析一次即可。
    /// </summary>
    public static List<Interval> ParseIntervals(IEnumerable<PcDetailRecord> records)
    {
        var intervals = new List<Interval>();
        foreach (var record in records)
        {
            if (PcActivityOverlapResolver.IsInactive(record.RecordType))
                continue;
            if ((record.DurationSeconds ?? 0) <= 0)
                continue;
            if (!TryGetActiveInterval(record, out var start, out var end) || end <= start)
                continue;

            intervals.Add(new Interval(record, start, end));
        }

        return intervals;
    }

    /// <summary>
    /// 记录在「活跃分钟」口径下的区间：与 <see cref="TryGetInterval"/> 相同，
    /// 但跨过采样断档的 <c>input-minute</c> 记录（区间长度 &gt; <see cref="MaxSamplingInterval"/>）
    /// 截断为「起点 + <see cref="SamplingGapActiveBudget"/>」—— 断档本身不计为活跃（REQ-1 / AC-1.3）。
    /// <para>明细接口（<c>/pc/detail</c>）仍按记录自身的 <c>DurationSeconds</c> 返回起止时刻，
    /// 本方法只影响活跃时长汇总，不改明细契约。</para>
    /// </summary>
    public static bool TryGetActiveInterval(PcDetailRecord record, out DateTimeOffset start, out DateTimeOffset end)
    {
        if (!TryGetInterval(record, out start, out end))
            return false;

        if (string.Equals(record.RecordType, "input-minute", StringComparison.OrdinalIgnoreCase)
            && end - start > MaxSamplingInterval)
        {
            end = start + SamplingGapActiveBudget;
        }

        return true;
    }

    /// <summary>
    /// 在 <paramref name="windowStart"/>–<paramref name="windowEnd"/>（半开区间）内消解重叠并汇总活跃段。
    /// 只有与窗口有交集的记录参与；跨界记录按重叠部分裁剪，不整条丢弃、也不重复计满。
    /// </summary>
    public static List<Segment> Resolve(
        IReadOnlyList<Interval> intervals,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (windowEnd <= windowStart || intervals.Count == 0)
            return [];

        var candidates = new List<PcActivityOverlapResolver.Candidate>();
        var owners = new List<PcDetailRecord>();

        foreach (var interval in intervals)
        {
            if (interval.End <= windowStart || interval.Start >= windowEnd)
                continue;

            candidates.Add(new PcActivityOverlapResolver.Candidate(
                interval.Start,
                interval.End,
                interval.Record.RecordType,
                interval.Record.ClassificationConfidence ?? 0,
                StableKey(interval.Record)));
            owners.Add(interval.Record);
        }

        if (candidates.Count == 0)
            return [];

        var resolved = PcActivityOverlapResolver.Resolve(candidates);
        var segments = new List<Segment>(resolved.Count);
        foreach (var segment in resolved)
        {
            var start = segment.Start < windowStart ? windowStart : segment.Start;
            var end = segment.End > windowEnd ? windowEnd : segment.End;
            if (end <= start)
                continue;

            segments.Add(new Segment(start, end, owners[segment.WinnerIndex]));
        }

        return segments;
    }

    /// <summary>记录列表 → 消解后的活跃段（内部先解析区间；多个窗口反复调用时请改用 <see cref="ParseIntervals"/>）。</summary>
    public static List<Segment> Resolve(
        IEnumerable<PcDetailRecord> records,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
        => Resolve(ParseIntervals(records), windowStart, windowEnd);

    /// <summary>段时长合计（秒）＝该窗口内候选区间的并集长度。</summary>
    public static double SumSeconds(IEnumerable<Segment> segments)
    {
        double total = 0;
        foreach (var segment in segments)
            total += (segment.End - segment.Start).TotalSeconds;
        return total;
    }

    /// <summary>
    /// 段与 <paramref name="from"/>–<paramref name="to"/> 的重叠时长合计（秒）。
    /// <para>
    /// 把整段时长直接加进每个有交集的小时桶会**重复计费**（一条跨 07:30–08:30 的段会让
    /// 07 点与 08 点两个桶都记满 60 分钟），因此按桶裁剪后的求和必须走这里。
    /// </para>
    /// </summary>
    public static double SumOverlapSeconds(IEnumerable<Segment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            return 0;

        double total = 0;
        foreach (var segment in segments)
        {
            var start = segment.Start < from ? from : segment.Start;
            var end = segment.End > to ? to : segment.End;
            if (end > start)
                total += (end - start).TotalSeconds;
        }

        return total;
    }

    /// <summary>记录的时间区间：优先用显式 <c>End</c>，缺失时按 <c>Start + DurationSeconds</c> 推算。</summary>
    public static bool TryGetInterval(PcDetailRecord record, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;

        if (!TryParse(record.Start, out start))
            return false;

        if (record.End is not null && TryParse(record.End, out var explicitEnd))
            end = explicitEnd;
        else
            end = start.AddSeconds(record.DurationSeconds ?? 0);

        return true;
    }

    private static bool TryParse(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);

    /// <summary>
    /// 消解器需要一个批次内稳定且唯一的键来保证结果确定、可复现；
    /// 明细记录没有数据库 record_key 时用「类型 + 区间 + 应用/域名」兜底。
    /// </summary>
    private static string StableKey(PcDetailRecord record)
        => record.RecordKey
           ?? $"{record.RecordType}|{record.Start}|{record.End}|{record.AppName}|{record.BrowserAppName}|{record.Domain}|{record.Title}";
}
