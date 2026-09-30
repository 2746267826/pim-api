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

    /// <summary>
    /// 在 <paramref name="windowStart"/>–<paramref name="windowEnd"/>（半开区间）内消解重叠并汇总活跃段。
    /// 只有与窗口有交集的记录参与；跨界记录按重叠部分裁剪，不整条丢弃、也不重复计满。
    /// </summary>
    public static List<Segment> Resolve(
        IEnumerable<PcDetailRecord> records,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (windowEnd <= windowStart)
            return [];

        var candidates = new List<PcActivityOverlapResolver.Candidate>();
        var owners = new List<PcDetailRecord>();

        foreach (var record in records)
        {
            if (PcActivityOverlapResolver.IsInactive(record.RecordType))
                continue;
            if ((record.DurationSeconds ?? 0) <= 0)
                continue;
            if (!TryGetInterval(record, out var start, out var end))
                continue;
            if (end <= windowStart || start >= windowEnd)
                continue;

            candidates.Add(new PcActivityOverlapResolver.Candidate(
                start,
                end,
                record.RecordType,
                record.ClassificationConfidence ?? 0,
                StableKey(record)));
            owners.Add(record);
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

    /// <summary>段时长合计（秒）＝该窗口内候选区间的并集长度。</summary>
    public static double SumSeconds(IEnumerable<Segment> segments)
    {
        double total = 0;
        foreach (var segment in segments)
            total += (segment.End - segment.Start).TotalSeconds;
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
