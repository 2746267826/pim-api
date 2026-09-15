namespace Pim.Module.PcTracker.Services;

/// <summary>
/// PC 时间线 v2 的时间块重叠消解（#237 / EPIC #254 组 3）。
/// <para>
/// 背景：分类快照由存在重叠的采集事件派生（见 #249），因此同一时刻可能有多条快照，
/// 直接平铺返回会让「同一时段被画成多块」，且任何按块累加的统计系统性偏高
/// （#237 实测 09-13：380 块中 325 块互相重叠，内容合计 8.49h &gt; 跨度 5.62h）。
/// </para>
/// <para>
/// 口径：扫描线切分（sweep line）。把输入区间按端点切成若干互不重叠的半开段
/// <c>[start, end)</c>，每一段归属一个「胜出」候选；胜出顺序为
/// <b>置信度高者优先 → 原始时长长者 → 稳定键序小者</b>（末项保证结果确定、可复现）。
/// 相邻且归属相同的段合并为一个块；短于 <c>minimumDuration</c> 的碎片段丢弃
/// （噪声下限，与 <see cref="PcActivityAggregationService"/> 的 60 秒口径一致）。
/// </para>
/// <para>
/// <b>调用方契约</b>：<see cref="Candidate.StableKey"/> 必须在一批候选中唯一
/// （时间线场景传 <c>record_key</c>，该列有唯一索引
/// <c>ux_pc_activity_classifications_record_key</c>）。满足该契约时胜出规则是<b>全序</b>，
/// 因此输出与候选的输入顺序无关；只有违反契约（两个候选四项字段全同）时，
/// 才会退回「先到者胜」的输入序，此时两者对调用方已不可区分。
/// </para>
/// <para>
/// 结果保证：块按 start 升序、两两不重叠（<c>blocks[i].Start &gt;= blocks[i-1].End</c>），
/// 且时长合计 ≤ 输入区间的并集跨度。这是<b>纯函数</b>，不依赖数据库。
/// </para>
/// </summary>
public static class PcTimelineOverlapResolver
{
    /// <summary>一个待消解的候选区间（通常是业务日内与快照相交的部分）。</summary>
    /// <param name="StableKey">稳定且<b>批次内唯一</b>的键（时间线场景为 record_key），用于让胜出规则成为全序。</param>
    public readonly record struct Candidate(
        DateTimeOffset Start,
        DateTimeOffset End,
        double Confidence,
        string StableKey);

    /// <summary>消解后的互不重叠时间块；<see cref="WinnerIndex"/> 指向输入候选的下标。</summary>
    public readonly record struct Segment(DateTimeOffset Start, DateTimeOffset End, int WinnerIndex);

    /// <summary>
    /// 把 <paramref name="candidates"/> 消解为互不重叠的时间块。
    /// <paramref name="minimumDuration"/> 为碎片段下限（&lt;= 0 时不过滤）。
    /// </summary>
    public static List<Segment> Resolve(IReadOnlyList<Candidate> candidates, TimeSpan minimumDuration)
    {
        var ordered = new List<int>(candidates.Count);
        var boundaries = new List<DateTimeOffset>(candidates.Count * 2);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].End <= candidates[i].Start)
                continue;
            ordered.Add(i);
            boundaries.Add(candidates[i].Start);
            boundaries.Add(candidates[i].End);
        }

        if (ordered.Count == 0)
            return [];

        ordered.Sort((left, right) =>
        {
            var byStart = candidates[left].Start.CompareTo(candidates[right].Start);
            if (byStart != 0) return byStart;
            var byEnd = candidates[left].End.CompareTo(candidates[right].End);
            return byEnd != 0 ? byEnd : left.CompareTo(right);
        });
        boundaries.Sort();

        var pieces = new List<Segment>();
        var active = new List<int>();
        var next = 0;

        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var cursor = boundaries[i];

            // 半开区间：终点落在 cursor 的候选不再覆盖 [cursor, next)；起点落在 cursor 的开始覆盖。
            active.RemoveAll(index => candidates[index].End <= cursor);
            while (next < ordered.Count && candidates[ordered[next]].Start <= cursor)
            {
                active.Add(ordered[next]);
                next++;
            }

            var segmentEnd = boundaries[i + 1];
            if (segmentEnd <= cursor || active.Count == 0)
                continue;

            var winner = SelectWinner(candidates, active);
            Append(pieces, new Segment(cursor, segmentEnd, winner));
        }

        return DropSlivers(pieces, minimumDuration);
    }

    /// <summary>胜出规则：置信度高 → 原始时区长 → 稳定键序小（确定性兜底）。</summary>
    private static int SelectWinner(IReadOnlyList<Candidate> candidates, List<int> active)
    {
        var best = active[0];
        for (var i = 1; i < active.Count; i++)
        {
            var challenger = active[i];
            if (IsBetter(candidates[challenger], candidates[best]))
                best = challenger;
        }
        return best;
    }

    private static bool IsBetter(in Candidate challenger, in Candidate incumbent)
    {
        if (challenger.Confidence != incumbent.Confidence)
            return challenger.Confidence > incumbent.Confidence;

        var challengerTicks = (challenger.End - challenger.Start).Ticks;
        var incumbentTicks = (incumbent.End - incumbent.Start).Ticks;
        if (challengerTicks != incumbentTicks)
            return challengerTicks > incumbentTicks;

        return string.CompareOrdinal(challenger.StableKey, incumbent.StableKey) < 0;
    }

    /// <summary>相邻且归属相同的段合并（例如高置信块被低置信块打断后重新接上）。</summary>
    private static void Append(List<Segment> pieces, Segment segment)
    {
        if (pieces.Count > 0)
        {
            var last = pieces[^1];
            if (last.WinnerIndex == segment.WinnerIndex && last.End == segment.Start)
            {
                pieces[^1] = last with { End = segment.End };
                return;
            }
        }
        pieces.Add(segment);
    }

    /// <summary>丢弃短于噪声下限的碎片段；<b>不</b>把碎片时间并给邻块（避免把时间算到错误的应用上）。</summary>
    private static List<Segment> DropSlivers(List<Segment> pieces, TimeSpan minimumDuration)
    {
        if (minimumDuration <= TimeSpan.Zero)
            return pieces;

        var kept = new List<Segment>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (piece.End - piece.Start >= minimumDuration)
                kept.Add(piece);
        }
        return kept;
    }
}
