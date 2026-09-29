using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 分档口径（WO-RELIABILITY-WINDOW-20260928 REQ-2 / REQ-4）：违规按**考核线**分成
/// 「窗内」（业务时间 ≥ 考核线，决定红/黄/绿）与「历史欠账」（考核线之前，只计数 / 可下钻 / 可导出）。
///
/// <para>
/// 本文件原名沿用 #260 的"新增 / 存量"验收（最近 24 小时为新增），现已按 7 天考核窗重写：
/// 分界仍按事件的**业务时间**算（不是入库时间），只是把分界从"体检时刻 − 24h"换成"考核线"。
/// </para>
/// </summary>
public class DataReliabilityNewAndStockTests
{
    private static readonly DateTime ReferenceNowUtc = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>考核线之外（远超默认 7 天考核窗）。</summary>
    private static readonly DateTime OldEnoughToBeDebtUtc = ReferenceNowUtc.AddDays(-10);

    [Fact]
    public void S1_OverlapWithinWindow_CountsAsWindow_AndTenDaysAgoCountsAsDebt()
    {
        var events = new List<EventTimeSpan>
        {
            // 窗内：两条 window 事件重叠，重叠结束于考核线之后 → 窗内违规
            new() { EventId = "recent-a", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddHours(-1), EndTime = ReferenceNowUtc.AddHours(-1).AddMinutes(10) },
            new() { EventId = "recent-b", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddHours(-1).AddMinutes(5), EndTime = ReferenceNowUtc.AddHours(-1).AddMinutes(15) },

            // 10 天前：两条 window 事件重叠 → 历史欠账
            new() { EventId = "old-a", DeviceId = "dev-1", EventType = "window", StartTime = OldEnoughToBeDebtUtc, EndTime = OldEnoughToBeDebtUtc.AddMinutes(10) },
            new() { EventId = "old-b", DeviceId = "dev-1", EventType = "window", StartTime = OldEnoughToBeDebtUtc.AddMinutes(5), EndTime = OldEnoughToBeDebtUtc.AddMinutes(15) }
        };

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Equal(InvariantStatus.Fail, result.Status);

        // 结构化违规清单同时给出两条，且标好了各自的「窗内 / 历史欠账」归属（D6：沿用 isNew 字段语义）。
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.Id == "recent-a" && v.Fields["isNew"] == "true");
        Assert.Contains(result.Violations, v => v.Id == "old-a" && v.Fields["isNew"] == "false");

        // 文案不得再出现旧术语「新增 / 存量」（REQ-7 / AC-7.6 的后端侧）。
        Assert.Contains("窗内", result.Detail, StringComparison.Ordinal);
        Assert.Contains("历史欠账", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("新增", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("存量", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void S2_OverlongEventWithinWindow_CountsAsWindow_AndTenDaysAgoCountsAsDebt()
    {
        var events = new List<LongEventCandidate>
        {
            new()
            {
                EventId = "recent-long",
                DeviceId = "dev-1",
                EventType = "window",
                StartTime = ReferenceNowUtc.AddHours(-2),
                EndTime = ReferenceNowUtc.AddHours(-1),
                Keystrokes = 0
            },
            new()
            {
                EventId = "old-long",
                DeviceId = "dev-1",
                EventType = "window",
                StartTime = OldEnoughToBeDebtUtc,
                EndTime = OldEnoughToBeDebtUtc.AddHours(1),
                Keystrokes = 0
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Contains(result.Violations, v => v.Id == "recent-long" && v.Fields["isNew"] == "true");
        Assert.Contains(result.Violations, v => v.Id == "old-long" && v.Fields["isNew"] == "false");
    }

    /// <summary>AC-4.3：S4 的违规项必须能区分窗内 / 欠账（现状缺 <c>isNew</c> 标记，本单补齐）。</summary>
    [Fact]
    public void S4_DuplicateWithinWindow_CountsAsWindow_AndTenDaysAgoCountsAsDebt()
    {
        var recent = ReferenceNowUtc.AddHours(-1);

        var records = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForLocation("dev-1", OldEnoughToBeDebtUtc, 31.23, 121.47),
            BusinessRecordKey.ForLocation("dev-1", OldEnoughToBeDebtUtc, 31.23, 121.47),
            BusinessRecordKey.ForLocation("dev-1", recent, 31.24, 121.48),
            BusinessRecordKey.ForLocation("dev-1", recent, 31.24, 121.48)
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.All(result.Violations, v => Assert.Equal("Location", v.Fields["domain"]));

        // S4 此前完全没有分档标记：现在每条违规项都要带 isNew。
        Assert.Equal(result.Violations.Count, result.Violations.Count(v => v.Fields.ContainsKey("isNew")));
        Assert.Contains(result.Violations, v => v.Fields["isNew"] == "true");
        Assert.Contains(result.Violations, v => v.Fields["isNew"] == "false");
    }

    /// <summary>分界用的是事件业务时间：把"旧违规"重新入库（入库时间在窗内）不得把它算成窗内。</summary>
    [Fact]
    public void S1_ClassificationIgnoresIngestionTime()
    {
        var oldEventStart = ReferenceNowUtc.AddDays(-10);

        var events = new List<EventTimeSpan>
        {
            new() { EventId = "reimported-a", DeviceId = "dev-1", EventType = "window", StartTime = oldEventStart, EndTime = oldEventStart.AddMinutes(10) },
            new() { EventId = "reimported-b", DeviceId = "dev-1", EventType = "window", StartTime = oldEventStart.AddMinutes(5), EndTime = oldEventStart.AddMinutes(15) }
        };

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(0, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
    }

    /// <summary>考核窗时长可配置（AssessmentWindowHours，默认 168 = 7 天），不是写死的 24 小时。</summary>
    [Fact]
    public void ClassificationRespectsConfiguredAssessmentWindow()
    {
        var events = new List<EventTimeSpan>
        {
            new() { EventId = "ten-days-a", DeviceId = "dev-1", EventType = "window", StartTime = OldEnoughToBeDebtUtc, EndTime = OldEnoughToBeDebtUtc.AddMinutes(10) },
            new() { EventId = "ten-days-b", DeviceId = "dev-1", EventType = "window", StartTime = OldEnoughToBeDebtUtc.AddMinutes(5), EndTime = OldEnoughToBeDebtUtc.AddMinutes(15) }
        };

        // 默认考核窗 168h（7 天）：10 天前 → 历史欠账
        var defaultWindow = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(0, defaultWindow.WindowViolations);
        Assert.Equal(1, defaultWindow.HistoricalViolations);

        // 把考核窗放宽到 720h（30 天）：同一批违规变成窗内
        var wideWindow = DataReliabilityInvariants.CheckS1_NoOverlap(
            events,
            new InvariantOptions { AssessmentWindowHours = 720 },
            referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(1, wideWindow.WindowViolations);
        Assert.Equal(0, wideWindow.HistoricalViolations);

        // 收窄到 24h：连"1 小时前"也会…… 不，1 小时仍在 24h 内；这里用 2 天前的违规验证收窄方向。
        var twoDaysAgo = ReferenceNowUtc.AddDays(-2);
        var narrowEvents = new List<EventTimeSpan>
        {
            new() { EventId = "two-days-a", DeviceId = "dev-1", EventType = "window", StartTime = twoDaysAgo, EndTime = twoDaysAgo.AddMinutes(10) },
            new() { EventId = "two-days-b", DeviceId = "dev-1", EventType = "window", StartTime = twoDaysAgo.AddMinutes(5), EndTime = twoDaysAgo.AddMinutes(15) }
        };

        var narrowWindow = DataReliabilityInvariants.CheckS1_NoOverlap(
            narrowEvents,
            new InvariantOptions { AssessmentWindowHours = 24 },
            referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(0, narrowWindow.WindowViolations);
        Assert.Equal(1, narrowWindow.HistoricalViolations);
    }

    /// <summary>
    /// 定位域的业务键里含精确经纬度，判据内部照常按键分组，但对外（样例 + 导出）只能出现不可逆摘要。
    /// </summary>
    [Fact]
    public void S4_DoesNotExposePreciseCoordinatesInSamplesOrViolationIds()
    {
        var when = ReferenceNowUtc.AddHours(-1);
        var records = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForLocation("dev-1", when, 31.230416, 121.473701),
            BusinessRecordKey.ForLocation("dev-1", when, 31.230416, 121.473701)
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(1, result.TotalViolations);
        var sample = Assert.Single(result.Samples);
        var violation = Assert.Single(result.Violations);

        // 六位小数的经纬度一旦出现就说明脱敏失效。
        Assert.DoesNotMatch(@"\d+\.\d{6}", sample);
        Assert.DoesNotMatch(@"\d+\.\d{6}", violation.Id);
        Assert.DoesNotContain("31.23", sample);
        Assert.DoesNotContain("121.47", violation.Id);

        // 摘要必须稳定且不可逆：16 位十六进制，且同一业务键两次得到同一个值。
        Assert.Matches("^[0-9A-F]{16}$", violation.Id);
        var again = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records, referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(violation.Id, Assert.Single(again.Violations).Id);
    }

    [Fact]
    public void DescribeViolationSplit_RendersBothBuckets()
    {
        var result = InvariantResult.Failure("x", totalViolations: 5, windowViolations: 2, historicalViolations: 3);

        var text = DataReliabilityInvariants.DescribeViolationSplit(result);

        Assert.Equal("窗内 2 / 历史欠账 3", text);
        Assert.DoesNotContain("新增", text, StringComparison.Ordinal);
        Assert.DoesNotContain("存量", text, StringComparison.Ordinal);
    }
}
