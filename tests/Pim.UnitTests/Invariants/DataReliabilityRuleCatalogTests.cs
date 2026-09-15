using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 13 条尺子元数据总表（#260）：判据原文、阈值口径、关联 issue 必须真实可用，
/// 且体检字典键必须与既有 DataReliabilityQualityInspector 的键格式保持一致。
/// </summary>
public class DataReliabilityRuleCatalogTests
{
    private static readonly Dictionary<string, string> ExpectedInvariantCodes = new()
    {
        ["S1"] = "INV-P16",
        ["S2"] = "INV-P17",
        ["S3"] = "INV-P18",
        ["S4"] = "INV-C18",
        ["S5"] = "INV-P19",
        ["S6"] = "INV-P20",
        ["S7"] = "INV-P21",
        ["S8"] = "INV-C19",
        ["S9"] = "INV-C20",
        ["S10"] = "INV-C21",
        ["S11"] = "INV-M21",
        ["S12"] = "INV-M22",
        ["S13"] = "INV-P22"
    };

    [Fact]
    public void Catalog_ContainsExactlyThirteenRulesInOrder()
    {
        Assert.Equal(13, DataReliabilityRuleCatalog.All.Count);
        Assert.Equal(
            Enumerable.Range(1, 13).Select(i => $"S{i}").ToArray(),
            DataReliabilityRuleCatalog.All.Select(rule => rule.Code).ToArray());
        Assert.Equal(
            Enumerable.Range(1, 13).ToArray(),
            DataReliabilityRuleCatalog.All.Select(rule => rule.Order).ToArray());
    }

    [Fact]
    public void Catalog_KeysMatchLegacyInspectorDictionaryKeys()
    {
        var legacyKeys = new[]
        {
            "S1_INV-P16", "S2_INV-P17", "S3_INV-P18", "S4_INV-C18", "S5_INV-P19",
            "S6_INV-P20", "S7_INV-P21", "S8_INV-C19", "S9_INV-C20", "S10_INV-C21",
            "S11_INV-M21", "S12_INV-M22", "S13_INV-P22"
        };

        Assert.Equal(legacyKeys, DataReliabilityRuleCatalog.All.Select(DataReliabilityRuleCatalog.BuildKey).ToArray());

        foreach (var rule in DataReliabilityRuleCatalog.All)
        {
            Assert.Equal(ExpectedInvariantCodes[rule.Code], rule.InvariantCode);
        }
    }

    [Fact]
    public void Catalog_GroupsFollowEpicThreeLayers()
    {
        var byGroup = DataReliabilityRuleCatalog.All.GroupBy(rule => rule.GroupLabel).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(3, byGroup.Count);
        Assert.Equal(5, byGroup["数据自洽"]);
        Assert.Equal(4, byGroup["覆盖完整"]);
        Assert.Equal(4, byGroup["链路健康"]);

        Assert.All(DataReliabilityRuleCatalog.All, rule =>
            Assert.Equal(rule.Group switch
            {
                DataReliabilityGroup.SelfConsistency => "数据自洽",
                DataReliabilityGroup.Coverage => "覆盖完整",
                _ => "链路健康"
            }, rule.GroupLabel));
    }

    /// <summary>
    /// 判据原文、阈值、理由不能是占位文案：设置页下钻弹窗要把它们原样展示给用户，
    /// 上一版实现用"按 EPIC 阈值执行"这类占位串充数，属于对用户的静默欺骗。
    /// </summary>
    [Fact]
    public void Catalog_CriterionThresholdAndRationaleAreRealContent()
    {
        foreach (var rule in DataReliabilityRuleCatalog.All)
        {
            Assert.True(rule.Criterion.Length >= 20, $"{rule.Code} 判据原文过短: '{rule.Criterion}'");
            Assert.True(rule.Threshold.Length >= 10, $"{rule.Code} 阈值过短: '{rule.Threshold}'");
            Assert.True(rule.Rationale.Length >= 20, $"{rule.Code} 设定理由过短: '{rule.Rationale}'");

            Assert.DoesNotContain("按 EPIC 阈值执行", rule.Threshold);
            Assert.DoesNotContain("该判据用于识别数据质量风险", rule.Rationale);
            Assert.DoesNotContain("满足对应不变量判据", rule.Criterion);
        }
    }

    [Fact]
    public void Catalog_KnownDefectRulesCarryTheirRelatedIssues()
    {
        Assert.Equal(new[] { 249 }, DataReliabilityRuleCatalog.Find("S1")!.RelatedIssues);
        Assert.Equal(new[] { 251 }, DataReliabilityRuleCatalog.Find("S2")!.RelatedIssues);
        Assert.Empty(DataReliabilityRuleCatalog.Find("S3")!.RelatedIssues);
        Assert.Equal(new[] { 246 }, DataReliabilityRuleCatalog.Find("S4")!.RelatedIssues);
        Assert.Equal(new[] { 252 }, DataReliabilityRuleCatalog.Find("S6")!.RelatedIssues);
        Assert.Equal(new[] { 252 }, DataReliabilityRuleCatalog.Find("S7")!.RelatedIssues);
        Assert.Contains(236, DataReliabilityRuleCatalog.Find("S8")!.RelatedIssues);
        Assert.Contains(239, DataReliabilityRuleCatalog.Find("S8")!.RelatedIssues);
        Assert.Equal(new[] { 244 }, DataReliabilityRuleCatalog.Find("S9")!.RelatedIssues);
        Assert.Equal(new[] { 234 }, DataReliabilityRuleCatalog.Find("S10")!.RelatedIssues);
        Assert.Equal(new[] { 241 }, DataReliabilityRuleCatalog.Find("S11")!.RelatedIssues);
        Assert.Equal(new[] { 247 }, DataReliabilityRuleCatalog.Find("S12")!.RelatedIssues);
        Assert.Equal(new[] { 250 }, DataReliabilityRuleCatalog.Find("S13")!.RelatedIssues);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndRejectsUnknownCodes()
    {
        Assert.Equal("S1", DataReliabilityRuleCatalog.Find("s1")!.Code);
        Assert.Equal("S13", DataReliabilityRuleCatalog.Find(" S13 ")!.Code);
        Assert.Null(DataReliabilityRuleCatalog.Find("S99"));
        Assert.Null(DataReliabilityRuleCatalog.Find("INV-P16"));
        Assert.Null(DataReliabilityRuleCatalog.Find(null));
        Assert.Null(DataReliabilityRuleCatalog.Find("   "));
    }

    [Fact]
    public void StatusMapping_CoversAllFourStates()
    {
        Assert.Equal("red", DataReliabilityRuleCatalog.NormalizeStatus(InvariantStatus.Fail));
        Assert.Equal("yellow", DataReliabilityRuleCatalog.NormalizeStatus(InvariantStatus.Warning));
        Assert.Equal("green", DataReliabilityRuleCatalog.NormalizeStatus(InvariantStatus.Pass));
        Assert.Equal("unknown", DataReliabilityRuleCatalog.NormalizeStatus(InvariantStatus.Unknown));

        Assert.Equal("红", DataReliabilityRuleCatalog.StatusLabel("red"));
        Assert.Equal("黄", DataReliabilityRuleCatalog.StatusLabel("yellow"));
        Assert.Equal("绿", DataReliabilityRuleCatalog.StatusLabel("green"));
        Assert.Equal("未知", DataReliabilityRuleCatalog.StatusLabel("unknown"));
        Assert.Equal("未知", DataReliabilityRuleCatalog.StatusLabel("something-else"));
    }

    [Fact]
    public void Catalog_CodesAndIssuesAreUniqueAndPositive()
    {
        Assert.Equal(13, DataReliabilityRuleCatalog.All.Select(rule => rule.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(13, DataReliabilityRuleCatalog.All.Select(rule => rule.InvariantCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(DataReliabilityRuleCatalog.All, rule => Assert.All(rule.RelatedIssues, issue => Assert.True(issue > 0)));
    }

    /// <summary>
    /// 尺子目录与判据实现的一致性：每条目录项都必须真的对应一个判据方法，而不是"靠注释保持一致"。
    /// 做法是对每条尺子喂空输入（各判据都会返回 UNKNOWN），断言返回的文案带的就是该目录项的 INV 编号。
    /// 若将来判据换编号、目录写错编号或漏挂判据，这条断言会立刻变红。
    /// </summary>
    [Fact]
    public void Catalog_EveryRuleIsBackedByItsJudgementMethod()
    {
        var judgementDetailByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S1"] = DataReliabilityInvariants.CheckS1_NoOverlap(new List<EventTimeSpan>()).Detail,
            ["S2"] = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(new List<LongEventCandidate>()).Detail,
            ["S3"] = DataReliabilityInvariants.CheckS3_DailyDurationBounded(new List<DailyActiveDuration>()).Detail,
            ["S4"] = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(new List<BusinessRecordKey>()).Detail,
            ["S5"] = DataReliabilityInvariants.CheckS5_ClockTrustworthy(new List<ClockEventItem>()).Detail,
            ["S6"] = DataReliabilityInvariants.CheckS6_OfflineDeclared(new DeviceActivityTrace()).Detail,
            ["S7"] = DataReliabilityInvariants.CheckS7_TimelineGapMarked(new List<TimelineInterval>()).Detail,
            ["S8"] = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(new List<DayBoundarySample>()).Detail,
            ["S9"] = DataReliabilityInvariants.CheckS9_GapHasSignal(new CoverageSignalReport { OnlineDurationSeconds = 0 }).Detail,
            ["S10"] = DataReliabilityInvariants.CheckS10_TaskHasOutput(new List<BackgroundTaskRun>()).Detail,
            ["S11"] = DataReliabilityInvariants.CheckS11_StatusSemantics(new List<BatchSyncStatusRecord>()).Detail,
            ["S12"] = DataReliabilityInvariants.CheckS12_DerivedTableActive(new List<DerivedTableStatus>()).Detail,
            ["S13"] = DataReliabilityInvariants.CheckS13_SingleInstance(new List<CollectionHeartbeat>()).Detail
        };

        Assert.Equal(13, judgementDetailByCode.Count);

        foreach (var rule in DataReliabilityRuleCatalog.All)
        {
            Assert.True(judgementDetailByCode.TryGetValue(rule.Code, out var detail),
                $"尺子 {rule.Code} 没有对应的判据方法");

            Assert.StartsWith(rule.InvariantCode, detail, StringComparison.Ordinal);
            Assert.StartsWith(rule.Code, DataReliabilityRuleCatalog.BuildKey(rule), StringComparison.Ordinal);
        }
    }
}
