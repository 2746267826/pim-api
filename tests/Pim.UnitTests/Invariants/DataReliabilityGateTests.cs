using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Pim.Core.Operations;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 数据可信度门禁（#260 第 4 点 / 验收标准 5）：尺子红，报告不得绿；无数据或过期不得静默判健康。
/// </summary>
public class DataReliabilityGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static DataReliabilityInspectionReport ReportWith(DateTimeOffset inspectedAt, params (string Code, string Status)[] rules) => new(
        InspectedAtUtc: inspectedAt,
        Version: 1,
        ElapsedMilliseconds: 5,
        Status: "green",
        RedCount: rules.Count(r => r.Status == "red"),
        YellowCount: rules.Count(r => r.Status == "yellow"),
        GreenCount: rules.Count(r => r.Status == "green"),
        UnknownCount: rules.Count(r => r.Status == "unknown"),
        TotalViolations: 0,
        NewViolations: 0,
        HistoricalViolations: 0,
        Notices: new Dictionary<string, string>(),
        Rules: rules.Select(rule => new DataReliabilityRuleReport(
            Code: rule.Code,
            InvariantCode: $"INV-{rule.Code}",
            Key: $"{rule.Code}_INV",
            Order: 1,
            Name: rule.Code,
            Group: "SelfConsistency",
            GroupLabel: "数据自洽",
            Status: rule.Status,
            StatusLabel: rule.Status,
            Detail: string.Empty,
            CurrentValue: 0,
            CurrentValueUnit: "条",
            CurrentValueLabel: null,
            Threshold: "阈值",
            Criterion: "判据",
            Rationale: "理由",
            RelatedIssues: Array.Empty<int>(),
            TotalViolations: 0,
            NewViolations: 0,
            HistoricalViolations: 0,
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
            ScanTruncated: false)).ToArray(),
        Message: string.Empty);

    private static DataReliabilityGate BuildGate(DataReliabilityInspectionReport? report)
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        if (report != null)
        {
            store.Publish(report);
        }

        return new DataReliabilityGate(store, new FixedTimeProvider());
    }

    [Fact]
    public void Evaluate_RedRule_YieldsCritical()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "red"), ("S2", "green")));

        var verdict = gate.Evaluate(new[] { "S1", "S2" });

        Assert.Equal(PimHealthStatus.Critical, verdict.Status);
        Assert.Equal(new[] { "S1" }, verdict.RedRules);
        Assert.Empty(verdict.YellowRules);
        Assert.Contains("S1", verdict.Message);
    }

    [Fact]
    public void Evaluate_OnlyYellowRules_YieldsWarning()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "green"), ("S5", "yellow")));

        var verdict = gate.Evaluate(new[] { "S1", "S5" });

        Assert.Equal(PimHealthStatus.Warning, verdict.Status);
        Assert.Empty(verdict.RedRules);
        Assert.Equal(new[] { "S5" }, verdict.YellowRules);
    }

    [Fact]
    public void Evaluate_AllGreen_YieldsHealthy()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "green"), ("S4", "green")));

        var verdict = gate.Evaluate(new[] { "S1", "S4" });

        Assert.Equal(PimHealthStatus.Healthy, verdict.Status);
        Assert.Equal(Now.AddMinutes(-5), verdict.InspectedAtUtc);
    }

    [Fact]
    public void Evaluate_WithoutInspection_YieldsUnknownWithExplicitMessage()
    {
        var gate = BuildGate(null);

        var verdict = gate.Evaluate(new[] { "S1" });

        Assert.Equal(PimHealthStatus.Unknown, verdict.Status);
        Assert.Contains("尚未体检", verdict.Message);
    }

    [Fact]
    public void Evaluate_StaleResult_YieldsUnknownWithExplicitMessage()
    {
        var gate = BuildGate(ReportWith(Now.AddHours(-40), ("S1", "red")));

        var verdict = gate.Evaluate(new[] { "S1" });

        Assert.Equal(PimHealthStatus.Unknown, verdict.Status);
        Assert.Contains("已过期", verdict.Message);
    }

    /// <summary>
    /// 未知（数据源缺失 / 未接线 / 取数超时）绝不等于通过：选中的尺子里有未知时，门禁不得判 Healthy，
    /// 否则 PC/手机质量报告会拿一条根本没跑出结论的尺子当绿色背书。
    /// </summary>
    [Fact]
    public void Evaluate_UnknownRule_NeverYieldsHealthy()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S2", "unknown"), ("S1", "green")));

        var verdict = gate.Evaluate(new[] { "S1", "S2" });

        Assert.Equal(PimHealthStatus.Unknown, verdict.Status);
        Assert.Equal(new[] { "S2" }, verdict.UnknownRules);
        Assert.Contains("未能判定", verdict.Message);
        Assert.DoesNotContain("均通过", verdict.Message);
    }

    [Fact]
    public void Evaluate_RedBeatsYellowBeatsUnknown()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "red"), ("S2", "yellow"), ("S3", "unknown")));

        var verdict = gate.Evaluate(new[] { "S1", "S2", "S3" });

        Assert.Equal(PimHealthStatus.Critical, verdict.Status);
        Assert.Equal(new[] { "S1" }, verdict.RedRules);
        Assert.Equal(new[] { "S2" }, verdict.YellowRules);
        Assert.Equal(new[] { "S3" }, verdict.UnknownRules);
    }

    [Fact]
    public void Evaluate_IgnoresRulesOutsideTheRequestedSet()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "green"), ("S10", "red")));

        var pcOnly = gate.Evaluate(new[] { "S1", "S2" });
        var mobileOnly = gate.Evaluate(new[] { "S10" });

        Assert.Equal(PimHealthStatus.Healthy, pcOnly.Status);
        Assert.Equal(PimHealthStatus.Critical, mobileOnly.Status);
    }

    [Fact]
    public void Evaluate_IsCaseInsensitive()
    {
        var gate = BuildGate(ReportWith(Now.AddMinutes(-5), ("S1", "red")));

        Assert.Equal(PimHealthStatus.Critical, gate.Evaluate(new[] { "s1" }).Status);
    }
}
