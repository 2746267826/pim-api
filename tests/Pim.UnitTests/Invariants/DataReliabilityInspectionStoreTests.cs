using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 体检结果进程内缓存（#260）：版本单调、历史上限、存量趋势基线选择与线程安全。
/// 出数层全程只读，缓存只能落在内存里，因此这些行为是"最近一次结果"能不能用的前提。
/// </summary>
public class DataReliabilityInspectionStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    private static DataReliabilityRuleReport Rule(string code, int historical, int total = 0) => new(
        Code: code,
        InvariantCode: $"INV-{code}",
        Key: $"{code}_INV",
        Order: int.Parse(code[1..]),
        Name: $"尺子 {code}",
        Group: "SelfConsistency",
        GroupLabel: "数据自洽",
        Status: historical > 0 ? "yellow" : "green",
        StatusLabel: historical > 0 ? "黄" : "绿",
        Detail: string.Empty,
        CurrentValue: historical,
        CurrentValueUnit: "条",
        CurrentValueLabel: null,
        Threshold: "阈值",
        Criterion: "判据",
        Rationale: "理由",
        RelatedIssues: Array.Empty<int>(),
        TotalViolations: total,
        NewViolations: 0,
        HistoricalViolations: historical,
        EarliestOccurrenceUtc: null,
        LatestOccurrenceUtc: null,
        Samples: Array.Empty<string>(),
        ThresholdFallback: false,
        ThresholdNote: null,
        CoveredLayers: null,
        Trend: "unknown",
        TrendDelta: null,
        TrendBaselineUtc: null,
        ThreeState: null,
        ScanTruncated: false);

    private static DataReliabilityInspectionReport Report(DateTimeOffset inspectedAt, params DataReliabilityRuleReport[] rules) => new(
        InspectedAtUtc: inspectedAt,
        Version: 0,
        ElapsedMilliseconds: 12,
        Status: "green",
        RedCount: 0,
        YellowCount: rules.Length,
        GreenCount: 0,
        UnknownCount: 0,
        TotalViolations: rules.Sum(r => r.TotalViolations),
        NewViolations: 0,
        HistoricalViolations: rules.Sum(r => r.HistoricalViolations),
        Notices: new Dictionary<string, string>(),
        Rules: rules,
        Message: "ok");

    [Fact]
    public void Publish_AssignsMonotonicVersionAndBecomesLatest()
    {
        var store = new InMemoryDataReliabilityInspectionStore();

        Assert.Null(store.Latest);

        var first = store.Publish(Report(Base, Rule("S1", 5)));
        var second = store.Publish(Report(Base.AddHours(2), Rule("S1", 5)));

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, store.Latest!.Version);
        Assert.Equal(2, store.History.Count);
        Assert.Equal(2, store.History[0].Version);
    }

    [Fact]
    public void Publish_BoundsHistoryToCapacity()
    {
        var store = new InMemoryDataReliabilityInspectionStore();

        for (int i = 0; i < InMemoryDataReliabilityInspectionStore.HistoryCapacity + 7; i++)
        {
            store.Publish(Report(Base.AddHours(i), Rule("S1", i)));
        }

        Assert.Equal(InMemoryDataReliabilityInspectionStore.HistoryCapacity, store.History.Count);
        Assert.Equal(InMemoryDataReliabilityInspectionStore.HistoryCapacity + 7, store.Latest!.Version);
    }

    [Fact]
    public void FirstInspection_HasNoTrendBaseline()
    {
        var store = new InMemoryDataReliabilityInspectionStore();

        var published = store.Publish(Report(Base, Rule("S1", 9)));

        Assert.Equal("unknown", published.Rules[0].Trend);
        Assert.Null(published.Rules[0].TrendDelta);
        Assert.Null(published.Rules[0].TrendBaselineUtc);
    }

    [Fact]
    public void Trend_DecreasingWhenHistoricalViolationsDrop()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 20)));

        var published = store.Publish(Report(Base.AddHours(3), Rule("S1", 8)));

        Assert.Equal("decreasing", published.Rules[0].Trend);
        Assert.Equal(-12, published.Rules[0].TrendDelta);
        Assert.Equal(Base, published.Rules[0].TrendBaselineUtc);
    }

    [Fact]
    public void Trend_IncreasingWhenHistoricalViolationsGrow()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 3)));

        var published = store.Publish(Report(Base.AddHours(3), Rule("S1", 11)));

        Assert.Equal("increasing", published.Rules[0].Trend);
        Assert.Equal(8, published.Rules[0].TrendDelta);
    }

    [Fact]
    public void Trend_FlatWhenUnchanged()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 4)));

        var published = store.Publish(Report(Base.AddHours(5), Rule("S1", 4)));

        Assert.Equal("flat", published.Rules[0].Trend);
        Assert.Equal(0, published.Rules[0].TrendDelta);
    }

    /// <summary>基线优先取"至少早 1 小时"的最新一条，而不是紧邻的上一条，避免把同一段时间内的抖动当成环比。</summary>
    [Fact]
    public void TrendBaseline_PrefersReportAtLeastOneHourOlder()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 30)));
        store.Publish(Report(Base.AddMinutes(90), Rule("S1", 25)));

        // 本次体检时间 Base+2h：cutoff = Base+1h，Base+90min 太近，应回退到 Base。
        var published = store.Publish(Report(Base.AddHours(2), Rule("S1", 10)));

        Assert.Equal(Base, published.Rules[0].TrendBaselineUtc);
        Assert.Equal(-20, published.Rules[0].TrendDelta);
    }

    /// <summary>历史里没有任何"早于 1 小时"的记录时，退化为紧邻的上一条。</summary>
    [Fact]
    public void TrendBaseline_FallsBackToPreviousReportWhenNoOlderBaselineExists()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 30)));

        var published = store.Publish(Report(Base.AddMinutes(5), Rule("S1", 20)));

        Assert.Equal(Base, published.Rules[0].TrendBaselineUtc);
        Assert.Equal(-10, published.Rules[0].TrendDelta);
    }

    [Fact]
    public void Trend_IsComputedPerRule()
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        store.Publish(Report(Base, Rule("S1", 10), Rule("S2", 1)));

        var published = store.Publish(Report(Base.AddHours(2), Rule("S1", 4), Rule("S2", 6)));

        Assert.Equal("decreasing", published.Rules.Single(r => r.Code == "S1").Trend);
        Assert.Equal("increasing", published.Rules.Single(r => r.Code == "S2").Trend);
    }

    [Fact]
    public async Task Publish_IsThreadSafe()
    {
        var store = new InMemoryDataReliabilityInspectionStore();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            store.Publish(Report(Base.AddMinutes(index), Rule("S1", index))))));

        Assert.Equal(32, store.Latest!.Version);
        Assert.Equal(InMemoryDataReliabilityInspectionStore.HistoryCapacity, store.History.Count);
        Assert.Equal(
            Enumerable.Range(1, InMemoryDataReliabilityInspectionStore.HistoryCapacity).Select(i => (long)(33 - i)),
            store.History.Select(item => item.Version));
    }
}
