using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 体检结果的进程内缓存（#260）。
/// 出数层全程只读、不写任何表，因此"最近一次结果"只能靠内存承载；进程重启后第一次读取会重新体检。
/// </summary>
public interface IDataReliabilityInspectionStore
{
    /// <summary>最近一次体检结果；从未体检过时为 null。</summary>
    DataReliabilityInspectionReport? Latest { get; }

    /// <summary>历史体检结果，最新在前，最多保留 <see cref="InMemoryDataReliabilityInspectionStore.HistoryCapacity"/> 条。</summary>
    IReadOnlyList<DataReliabilityInspectionReport> History { get; }

    /// <summary>
    /// 发布一次体检结果：分配单调递增的 <c>Version</c>，并对比历史基线补齐每条尺子的存量趋势。
    /// </summary>
    DataReliabilityInspectionReport Publish(DataReliabilityInspectionReport report);
}

/// <summary>
/// 线程安全的进程内实现。注意：不落库、不落文件，符合"体检全程只读"的硬约束。
/// </summary>
public sealed class InMemoryDataReliabilityInspectionStore : IDataReliabilityInspectionStore
{
    /// <summary>历史保留条数：足够看趋势，又不会无界增长。</summary>
    public const int HistoryCapacity = 20;

    /// <summary>趋势基线至少要比本次体检早这么久，避免同一分钟内的两次体检被当成"环比"。</summary>
    private static readonly TimeSpan MinimumTrendBaselineAge = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly List<DataReliabilityInspectionReport> _history = new();
    private long _version;

    public DataReliabilityInspectionReport? Latest
    {
        get
        {
            lock (_gate)
            {
                return _history.Count > 0 ? _history[0] : null;
            }
        }
    }

    public IReadOnlyList<DataReliabilityInspectionReport> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public DataReliabilityInspectionReport Publish(DataReliabilityInspectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            var baseline = SelectTrendBaseline(report.InspectedAtUtc);
            var rules = report.Rules
                .Select(rule => ApplyTrend(rule, baseline))
                .ToArray();

            var published = report with
            {
                Version = ++_version,
                Rules = rules
            };

            _history.Insert(0, published);
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveRange(HistoryCapacity, _history.Count - HistoryCapacity);
            }

            return published;
        }
    }

    /// <summary>
    /// 选趋势基线：优先取"比本次体检至少早 1 小时的最新一条"，没有就退化为紧邻的上一条。
    /// 都没有（首次体检）则返回 null，趋势标记为 unknown。
    /// </summary>
    private DataReliabilityInspectionReport? SelectTrendBaseline(DateTimeOffset inspectedAtUtc)
    {
        if (_history.Count == 0)
        {
            return null;
        }

        var cutoff = inspectedAtUtc - MinimumTrendBaselineAge;
        foreach (var candidate in _history)
        {
            if (candidate.InspectedAtUtc <= cutoff)
            {
                return candidate;
            }
        }

        return _history[0];
    }

    /// <summary>用基线计算存量趋势：减少/增加/持平；没有基线时为 unknown。</summary>
    private static DataReliabilityRuleReport ApplyTrend(
        DataReliabilityRuleReport rule,
        DataReliabilityInspectionReport? baseline)
    {
        if (baseline == null)
        {
            return rule with { Trend = "unknown", TrendDelta = null, TrendBaselineUtc = null };
        }

        var previous = baseline.Rules.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, rule.Code, StringComparison.OrdinalIgnoreCase));

        if (previous == null)
        {
            return rule with { Trend = "unknown", TrendDelta = null, TrendBaselineUtc = baseline.InspectedAtUtc };
        }

        var delta = rule.HistoricalViolations - previous.HistoricalViolations;
        var trend = delta < 0 ? "decreasing" : delta > 0 ? "increasing" : "flat";

        return rule with
        {
            Trend = trend,
            TrendDelta = delta,
            TrendBaselineUtc = baseline.InspectedAtUtc
        };
    }
}
