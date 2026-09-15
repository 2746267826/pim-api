using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// #260 验收标准 4：造一条"3 天前的违规" → 计入存量；造一条"今天的违规" → 计入新增。
/// 分界必须按事件的**业务时间**算（T4：最近 24 小时为新增），而不是按入库时间。
/// </summary>
public class DataReliabilityNewAndStockTests
{
    private static readonly DateTime ReferenceNowUtc = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void S1_OverlapToday_CountsAsNew_AndThreeDaysAgoCountsAsStock()
    {
        var events = new List<EventTimeSpan>
        {
            // 今天：两条 window 事件重叠，重叠结束于最近 24 小时内 → 新增
            new() { EventId = "today-a", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddHours(-1), EndTime = ReferenceNowUtc.AddHours(-1).AddMinutes(10) },
            new() { EventId = "today-b", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddHours(-1).AddMinutes(5), EndTime = ReferenceNowUtc.AddHours(-1).AddMinutes(15) },

            // 3 天前：两条 window 事件重叠 → 存量
            new() { EventId = "old-a", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddDays(-3), EndTime = ReferenceNowUtc.AddDays(-3).AddMinutes(10) },
            new() { EventId = "old-b", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddDays(-3).AddMinutes(5), EndTime = ReferenceNowUtc.AddDays(-3).AddMinutes(15) }
        };

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Equal(InvariantStatus.Fail, result.Status);

        // 结构化违规清单同时给出两条，且标好了各自的新增/存量归属。
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.Id == "today-a" && v.Fields["isNew"] == "true");
        Assert.Contains(result.Violations, v => v.Id == "old-a" && v.Fields["isNew"] == "false");
    }

    [Fact]
    public void S2_OverlongEventToday_CountsAsNew_AndThreeDaysAgoCountsAsStock()
    {
        var events = new List<LongEventCandidate>
        {
            new()
            {
                EventId = "today-long",
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
                StartTime = ReferenceNowUtc.AddDays(-3),
                EndTime = ReferenceNowUtc.AddDays(-3).AddHours(1),
                Keystrokes = 0
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Contains(result.Violations, v => v.Id == "today-long");
        Assert.Contains(result.Violations, v => v.Id == "old-long");
    }

    [Fact]
    public void S4_DuplicateToday_CountsAsNew_AndThreeDaysAgoCountsAsStock()
    {
        var today = ReferenceNowUtc.AddHours(-1);
        var threeDaysAgo = ReferenceNowUtc.AddDays(-3);

        var records = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForLocation("dev-1", threeDaysAgo, 31.23, 121.47),
            BusinessRecordKey.ForLocation("dev-1", threeDaysAgo, 31.23, 121.47),
            BusinessRecordKey.ForLocation("dev-1", today, 31.24, 121.48),
            BusinessRecordKey.ForLocation("dev-1", today, 31.24, 121.48)
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records, referenceTimeUtc: ReferenceNowUtc);

        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.All(result.Violations, v => Assert.Equal("Location", v.Fields["domain"]));
    }

    /// <summary>分界用的是事件业务时间：把"旧违规"重新入库（入库时间在窗口内）不得把它算成新增。</summary>
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

        Assert.Equal(0, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
    }

    /// <summary>分界窗口可配置（RecentWindowHours），不是写死的 24 小时。</summary>
    [Fact]
    public void ClassificationRespectsConfiguredRecentWindow()
    {
        var events = new List<EventTimeSpan>
        {
            new() { EventId = "two-days-a", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddDays(-2), EndTime = ReferenceNowUtc.AddDays(-2).AddMinutes(10) },
            new() { EventId = "two-days-b", DeviceId = "dev-1", EventType = "window", StartTime = ReferenceNowUtc.AddDays(-2).AddMinutes(5), EndTime = ReferenceNowUtc.AddDays(-2).AddMinutes(15) }
        };

        var defaultWindow = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(0, defaultWindow.NewViolations);
        Assert.Equal(1, defaultWindow.HistoricalViolations);

        var wideWindow = DataReliabilityInvariants.CheckS1_NoOverlap(
            events,
            new InvariantOptions { RecentWindowHours = 72 },
            referenceTimeUtc: ReferenceNowUtc);
        Assert.Equal(1, wideWindow.NewViolations);
        Assert.Equal(0, wideWindow.HistoricalViolations);
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
        var result = InvariantResult.Failure("x", totalViolations: 5, newViolations: 2, historicalViolations: 3);

        Assert.Equal("新增 2 / 存量 3", DataReliabilityInvariants.DescribeViolationSplit(result));
    }
}
