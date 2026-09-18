namespace Pim.Module.PcTracker.Services;

/// <summary>
/// PC 分类记录的优先级重叠消解（#301）。
/// <para>
/// 背景：<c>pc_activity_classifications</c> 的快照由相互重叠的采集事件派生 ——
/// 前台 <c>window</c> 记录会覆盖同一时段的 <c>web-page</c> 与逐分钟 <c>input-minute</c> 记录，
/// 另有表示「无活动」的 <c>gap</c> / <c>idle</c> / <c>afk</c> 块。任何「逐条相加」的统计
/// 都会系统性偏高：实测 2026-09-17 分类合计 1,622 分钟（约 27 小时）> 24 小时物理上限。
/// </para>
/// <para>
/// 口径：把输入区间按端点切成互不重叠的半开段 <c>[start, end)</c>，每段唯一归属一个候选。
/// 胜出顺序为 <b>记录类型优先级 → 置信度 → 原始时长 → 稳定键序</b>（末项保证结果确定、可复现）。
/// 相邻且归属相同的段合并；时长合计 ≤ 输入区间的并集跨度，因此单日不会超过 24 小时。
/// </para>
/// <para>
/// 「未活动」类型（<see cref="IsInactive"/>）不参与统计，调用方应先过滤掉。
/// 这是<b>纯函数</b>，不依赖数据库，供分类分布与生产力统计共用同一口径。
/// </para>
/// </summary>
public static class PcActivityOverlapResolver
{
    /// <summary>
    /// 记录类型优先级：数值越大越优先，同一时刻只归属优先者。
    /// 前台窗口记录 &gt; 网页记录 &gt; 输入分钟记录。
    /// </summary>
    private static readonly Dictionary<string, int> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["window"] = 3,
        ["currentwindow"] = 3,
        ["web-page"] = 2,
        ["input-minute"] = 1,
    };

    /// <summary>「未活动」记录类型：不参与分类分布与生产力统计（#301）。</summary>
    private static readonly HashSet<string> InactiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gap", "idle", "afk",
    };

    /// <summary>一个待消解的分类区间。</summary>
    /// <param name="StableKey">
    /// 稳定且<b>批次内唯一</b>的键（分类记录场景为 <c>record_key</c>，该列有唯一索引
    /// <c>ux_pc_activity_classifications_record_key</c>），用于让胜出规则成为全序。
    /// </param>
    public readonly record struct Candidate(
        DateTimeOffset Start,
        DateTimeOffset End,
        string RecordType,
        double Confidence,
        string StableKey);

    /// <summary>消解后的互不重叠时间块；<see cref="WinnerIndex"/> 指向输入候选的下标。</summary>
    public readonly record struct Segment(DateTimeOffset Start, DateTimeOffset End, int WinnerIndex);

    /// <summary>该记录类型是否表示「未活动」（gap / idle / afk），不参与统计。</summary>
    public static bool IsInactive(string? recordType)
        => !string.IsNullOrWhiteSpace(recordType) && InactiveTypes.Contains(recordType);

    /// <summary>记录类型优先级；未登记的类型按最低优先级处理（不与已知类型争抢时段）。</summary>
    public static int PriorityOf(string? recordType)
        => !string.IsNullOrWhiteSpace(recordType) && Priorities.TryGetValue(recordType, out var priority)
            ? priority
            : 1;

    /// <summary>
    /// 统一的胜出判定（<b>全仓库唯一口径</b>）：记录类型优先级高 → 置信度高 →
    /// 原始时区长 → 稳定键序小（确定性兜底）。
    /// <para>
    /// 分类分布、生产力统计与时间线 v2 共用本判定，避免同一时刻在不同接口归属不同分类
    /// （review 发现：两套 resolver 曾分别以「优先级」和「置信度」为首要键）。
    /// <paramref name="recordType"/> 为空表示调用方没有类型信息，此时退化为
    /// 「置信度 → 时长 → 稳定键」——与该场景原有的历史行为一致。
    /// </para>
    /// </summary>
    public static bool Beats(
        string? challengerRecordType, double challengerConfidence, TimeSpan challengerDuration, string challengerStableKey,
        string? incumbentRecordType, double incumbentConfidence, TimeSpan incumbentDuration, string incumbentStableKey)
    {
        var challengerPriority = PriorityOf(challengerRecordType);
        var incumbentPriority = PriorityOf(incumbentRecordType);
        if (challengerPriority != incumbentPriority)
            return challengerPriority > incumbentPriority;

        if (challengerConfidence != incumbentConfidence)
            return challengerConfidence > incumbentConfidence;

        if (challengerDuration.Ticks != incumbentDuration.Ticks)
            return challengerDuration.Ticks > incumbentDuration.Ticks;

        return string.CompareOrdinal(challengerStableKey, incumbentStableKey) < 0;
    }

    /// <summary>
    /// 把候选区间消解为互不重叠的时间块（按 start 升序）。
    /// 零长 / 负长区间被丢弃；结果保证 <c>segments[i].Start &gt;= segments[i-1].End</c>。
    /// </summary>
    public static List<Segment> Resolve(IReadOnlyList<Candidate> candidates)
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

        var segments = new List<Segment>();
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

            Append(segments, new Segment(cursor, segmentEnd, SelectWinner(candidates, active)));
        }

        return segments;
    }

    /// <summary>胜出规则：记录类型优先级高 → 置信度高 → 原始时区长 → 稳定键序小（确定性兜底）。</summary>
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
        => Beats(
            challenger.RecordType, challenger.Confidence, challenger.End - challenger.Start, challenger.StableKey,
            incumbent.RecordType, incumbent.Confidence, incumbent.End - incumbent.Start, incumbent.StableKey);

    /// <summary>相邻且归属相同的段合并（例如高优先记录打断后重新接上）。</summary>
    private static void Append(List<Segment> segments, Segment segment)
    {
        if (segments.Count > 0)
        {
            var last = segments[^1];
            if (last.WinnerIndex == segment.WinnerIndex && last.End == segment.Start)
            {
                segments[^1] = last with { End = segment.End };
                return;
            }
        }
        segments.Add(segment);
    }
}
