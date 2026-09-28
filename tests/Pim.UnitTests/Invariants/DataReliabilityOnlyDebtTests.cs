using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// WO-RELIABILITY-WINDOW-20260928 REQ-2 / REQ-3：8 条分档尺子统一为「窗内判色、窗外记账」。
///
/// <para>
/// 这些用例**只用基线已有的 API**（<c>referenceTimeUtc</c> + 各判据方法），因此可以在"改动前的实现"上编译并运行——
/// 用来取得 AC-9.3 要的两段证据：旧实现上失败（仅欠账被判黄 / 判红），新实现上通过（仅欠账 → 绿 + 欠账计数）。
/// </para>
///
/// <para>
/// 分档时间字段沿用现状（AC-1.7），所以每条尺子的"业务时间"都按其自身字段摆放：
/// S1 重叠结束、S2 事件结束、S4 业务记录时间、S5 服务端接收时间、S6 空档结束、S7 空洞结束、
/// S11 批次窗口起点、S13 冲突发生时刻。
/// </para>
/// </summary>
public class DataReliabilityOnlyDebtTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>窗外（历史欠账）：10 天前，远超默认 7 天考核窗。</summary>
    private static readonly DateTime DebtTimeUtc = NowUtc.AddDays(-10);

    /// <summary>窗内：1 小时前。</summary>
    private static readonly DateTime WindowTimeUtc = NowUtc.AddHours(-1);

    #region AC-3.1 ~ AC-3.8：每条尺子「仅欠账 → 绿 + 欠账计数」

    /// <summary>AC-3.1：S1 仅欠账 → 绿 + 欠账计数（现状为红）。</summary>
    [Fact]
    public void S1_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(S1Overlaps(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P16");
        AssertInvariantContainsBothCounts(result);
    }

    /// <summary>AC-3.2：S2 仅欠账 → 绿 + 欠账计数（现状为红）。</summary>
    [Fact]
    public void S2_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(S2Unclosed(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P17");
    }

    /// <summary>AC-3.3：S4 仅欠账 → 绿 + 欠账计数（现状为红）。</summary>
    [Fact]
    public void S4_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(S4Duplicates(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-C18");
    }

    /// <summary>AC-3.4：S5 仅欠账 → 绿（现状为黄）。</summary>
    [Fact]
    public void S5_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(S5Skew(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P19");
    }

    /// <summary>AC-3.5：S6 仅欠账 → 绿 + 欠账计数（现状为黄）。</summary>
    [Fact]
    public void S6_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-1",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (DebtTimeUtc, DebtTimeUtc.AddMinutes(10)),
                    (DebtTimeUtc.AddHours(2), DebtTimeUtc.AddHours(2).AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P20");
    }

    /// <summary>AC-3.6：S7 仅欠账 → 绿（现状为黄）。</summary>
    [Fact]
    public void S7_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(S7UnmarkedHole(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P21");
    }

    /// <summary>AC-3.7：S11 仅欠账 → 绿（现状为黄）。</summary>
    [Fact]
    public void S11_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(S11InconsistentBatch(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-M21");
    }

    /// <summary>AC-3.8：S13 仅欠账 → 绿（现状为黄）。</summary>
    [Fact]
    public void S13_OnlyDebt_IsGreenWithDebtCount()
    {
        var result = DataReliabilityInvariants.CheckS13_SingleInstance(S13ConcurrentInstances(DebtTimeUtc), referenceTimeUtc: NowUtc);

        AssertGreenWithDebt(result, expectedDebt: 1, "INV-P22");
    }

    #endregion

    #region AC-2.2 / AC-3.9：窗内违规照常判色，且不再有"总数 > 0 即判红"

    [Fact]
    public void S1_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(S1Overlaps(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
        Assert.Equal(0, result.HistoricalViolations);
    }

    [Fact]
    public void S2_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(S2Unclosed(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S4_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(S4Duplicates(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S5_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(S5Skew(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S6_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-1",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (WindowTimeUtc.AddHours(-2), WindowTimeUtc.AddHours(-2).AddMinutes(10)),
                    (WindowTimeUtc, WindowTimeUtc.AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S7_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(S7UnmarkedHole(WindowTimeUtc.AddHours(-2)), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S11_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(S11InconsistentBatch(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    [Fact]
    public void S13_WindowViolation_IsRed()
    {
        var result = DataReliabilityInvariants.CheckS13_SingleInstance(S13ConcurrentInstances(WindowTimeUtc), referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    /// <summary>
    /// AC-3.9（反面）：S1 / S2 / S4 不得再存在"总违规数 &gt; 0 即判红"的路径——
    /// 总数 &gt; 0 但**全部**是历史欠账时必须为绿。
    /// </summary>
    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S4")]
    public void S1S2S4_TotalPositiveButAllDebt_IsNeverRed(string ruleCode)
    {
        var result = ruleCode switch
        {
            "S1" => DataReliabilityInvariants.CheckS1_NoOverlap(S1Overlaps(DebtTimeUtc), referenceTimeUtc: NowUtc),
            "S2" => DataReliabilityInvariants.CheckS2_OverlongEventEvidence(S2Unclosed(DebtTimeUtc), referenceTimeUtc: NowUtc),
            _ => DataReliabilityInvariants.CheckS4_BusinessKeyUnique(S4Duplicates(DebtTimeUtc), referenceTimeUtc: NowUtc)
        };

        Assert.True(result.TotalViolations > 0, "夹具必须真的造出违规，否则这条反面用例什么都没证明");
        Assert.Equal(0, result.WindowViolations);
        Assert.Equal(InvariantStatus.Pass, result.Status);
        Assert.NotEqual(InvariantStatus.Fail, result.Status);
    }

    #endregion

    #region AC-2.6：同一违规只归属一边，不重复计数

    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S4")]
    [InlineData("S5")]
    [InlineData("S6")]
    [InlineData("S7")]
    [InlineData("S11")]
    [InlineData("S13")]
    public void WindowPlusDebt_EqualsTotal_NoDoubleCounting(string ruleCode)
    {
        var result = MixedFixture(ruleCode);

        Assert.Equal(1, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(result.WindowViolations + result.HistoricalViolations, result.TotalViolations);
    }

    #endregion

    #region AC-3.5 / AC-3.8：设备级合并不得把"仅欠账"合成为黄或未知

    [Fact]
    public void DeviceMerge_OnlyDebtAcrossDevices_StaysGreenAndSumsDebt()
    {
        var deviceA = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-A",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (DebtTimeUtc, DebtTimeUtc.AddMinutes(10)),
                    (DebtTimeUtc.AddHours(2), DebtTimeUtc.AddHours(2).AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        var deviceB = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-B",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (DebtTimeUtc.AddHours(1), DebtTimeUtc.AddHours(1).AddMinutes(10)),
                    (DebtTimeUtc.AddHours(4), DebtTimeUtc.AddHours(4).AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        var merged = DataReliabilityInvariants.CombineDeviceVerdicts("INV-P20", new[] { deviceA, deviceB });

        Assert.Equal(InvariantStatus.Pass, merged.Status);
        Assert.NotEqual(InvariantStatus.Warning, merged.Status);
        Assert.NotEqual(InvariantStatus.Unknown, merged.Status);
        Assert.Equal(0, merged.WindowViolations);
        Assert.Equal(2, merged.HistoricalViolations);
        Assert.Equal(2, merged.TotalViolations);
    }

    [Fact]
    public void DeviceMerge_OneDeviceWindowViolation_IsRed()
    {
        var debtOnly = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-A",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (DebtTimeUtc, DebtTimeUtc.AddMinutes(10)),
                    (DebtTimeUtc.AddHours(2), DebtTimeUtc.AddHours(2).AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        var windowViolation = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-B",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (WindowTimeUtc.AddHours(-2), WindowTimeUtc.AddHours(-2).AddMinutes(10)),
                    (WindowTimeUtc, WindowTimeUtc.AddMinutes(10))
                }
            },
            referenceTimeUtc: NowUtc);

        var merged = DataReliabilityInvariants.CombineDeviceVerdicts("INV-P20", new[] { debtOnly, windowViolation });

        Assert.Equal(InvariantStatus.Fail, merged.Status);
        Assert.Equal(1, merged.WindowViolations);
        Assert.Equal(1, merged.HistoricalViolations);
    }

    #endregion

    #region 反面：窗内问题不得因为"窗外还有更差的样本"而被折成欠账

    /// <summary>
    /// S6 的上传滞后 p99 是**聚合统计量**。若整批算一个 p99、再用"最差一条样本"的时间归边，
    /// 窗外只要存在一条更差的样本，窗内那批样本的 p99 超标就会被整笔记成历史欠账 → 尺子变绿。
    /// 本用例构造"窗外滞后 1000 分钟 + 窗内滞后 40 分钟（> 30 分钟阈值）"，要求窗内仍然判红。
    /// </summary>
    [Fact]
    public void S6_WindowP99Exceeds_WhileWorstSampleIsDebt_StillRed()
    {
        var debtSample = new DeviceActivityTrace
        {
            DeviceId = "PC-1",
            EventIntervals = new (DateTime, DateTime)[] { (DebtTimeUtc, DebtTimeUtc.AddMinutes(10)) },
            UploadLagSamples = new[]
            {
                new UploadLagSample { EventTime = DebtTimeUtc, CreatedAt = DebtTimeUtc.AddMinutes(1000) }
            }
        };

        var mixedTrace = new DeviceActivityTrace
        {
            DeviceId = "PC-1",
            EventIntervals = new (DateTime, DateTime)[] { (WindowTimeUtc.AddHours(-1), WindowTimeUtc) },
            UploadLagSamples = new[]
            {
                // 窗外：滞后 1000 分钟（"最差一条"落在欠账侧）
                new UploadLagSample { EventTime = DebtTimeUtc, CreatedAt = DebtTimeUtc.AddMinutes(1000) },
                // 窗内：滞后 40 分钟（窗内这一批的 p99 已经超过 30 分钟阈值）
                new UploadLagSample { EventTime = WindowTimeUtc.AddMinutes(-40), CreatedAt = WindowTimeUtc }
            }
        };

        // 先确认两个分支本身都成立：单看窗外样本只有欠账，单看窗内样本（同样是 40 分钟）必须判红。
        var debtOnly = DataReliabilityInvariants.CheckS6_OfflineDeclared(debtSample, referenceTimeUtc: NowUtc);
        Assert.Equal(InvariantStatus.Pass, debtOnly.Status);
        Assert.Equal(1, debtOnly.HistoricalViolations);

        var windowOnly = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-1",
                EventIntervals = new (DateTime, DateTime)[] { (WindowTimeUtc.AddHours(-1), WindowTimeUtc) },
                UploadLagSamples = new[]
                {
                    new UploadLagSample { EventTime = WindowTimeUtc.AddMinutes(-40), CreatedAt = WindowTimeUtc }
                }
            },
            referenceTimeUtc: NowUtc);
        Assert.Equal(InvariantStatus.Fail, windowOnly.Status);

        var mixed = DataReliabilityInvariants.CheckS6_OfflineDeclared(mixedTrace, referenceTimeUtc: NowUtc);

        Assert.Equal(InvariantStatus.Fail, mixed.Status);
        Assert.Equal(1, mixed.WindowViolations);
        Assert.Equal(1, mixed.HistoricalViolations);
        Assert.Equal(2, mixed.TotalViolations);
        Assert.Equal(mixed.WindowViolations + mixed.HistoricalViolations, mixed.TotalViolations);
    }

    #endregion

    #region AC-4.2：S4 / S5 / S11 的违规项必须带分档标记

    /// <summary>
    /// AC-4.2（反面 AC-4.3）：违规项的分档标记此前只有 S1/S2/S6/S7/S13 有；
    /// S4 / S5 / S11 必须补齐，否则下钻与导出无法区分窗内 / 欠账。
    /// </summary>
    [Theory]
    [InlineData("S4-window")]
    [InlineData("S4-debt")]
    [InlineData("S5-window")]
    [InlineData("S5-debt")]
    [InlineData("S11-window")]
    [InlineData("S11-debt")]
    public void S4S5S11_Violations_CarrySplitMarker(string scenario)
    {
        var wantsWindow = scenario.EndsWith("-window", StringComparison.Ordinal);
        var at = wantsWindow ? WindowTimeUtc : DebtTimeUtc;
        var expectedMarker = wantsWindow ? "true" : "false";

        var result = scenario.Split('-')[0] switch
        {
            "S4" => DataReliabilityInvariants.CheckS4_BusinessKeyUnique(S4Duplicates(at), referenceTimeUtc: NowUtc),
            "S5" => DataReliabilityInvariants.CheckS5_ClockTrustworthy(S5Skew(at), referenceTimeUtc: NowUtc),
            _ => DataReliabilityInvariants.CheckS11_StatusSemantics(S11InconsistentBatch(at), referenceTimeUtc: NowUtc)
        };

        var violation = Assert.Single(result.Violations);
        Assert.True(violation.Fields.ContainsKey("isNew"), $"{scenario} 的违规项缺少分档标记 isNew");
        Assert.Equal(expectedMarker, violation.Fields["isNew"]);
        Assert.Contains(wantsWindow ? "窗内" : "历史欠账", Assert.Single(result.Samples), StringComparison.Ordinal);
    }

    #endregion

    #region REQ-7 / AC-7.6：分档文案不再出现"新增 / 存量"

    [Fact]
    public void DescribeViolationSplit_UsesWindowAndDebtTermsOnly()
    {
        var result = InvariantResult.Failure("x", totalViolations: 5, windowViolations: 2, historicalViolations: 3);

        var text = DataReliabilityInvariants.DescribeViolationSplit(result);

        Assert.Equal("窗内 2 / 历史欠账 3", text);
        Assert.DoesNotContain("新增", text, StringComparison.Ordinal);
        Assert.DoesNotContain("存量", text, StringComparison.Ordinal);
    }

    #endregion

    #region 断言与夹具

    private static void AssertGreenWithDebt(InvariantResult result, int expectedDebt, string invariantCode)
    {
        Assert.Equal(InvariantStatus.Pass, result.Status);
        Assert.True(result.Pass);
        Assert.Equal(0, result.WindowViolations);
        Assert.Equal(expectedDebt, result.HistoricalViolations);
        Assert.Equal(expectedDebt, result.TotalViolations);
        Assert.StartsWith($"{invariantCode} PASS", result.Detail);
        // 欠账必须可下钻 / 可导出：绿色不代表把违规项丢掉。
        Assert.NotEmpty(result.Violations);
        Assert.NotEmpty(result.Samples);
    }

    /// <summary>每条尺子的结论都要能同时读到窗内与欠账两个计数（REQ-4）。</summary>
    private static void AssertInvariantContainsBothCounts(InvariantResult result)
    {
        Assert.Contains("历史欠账", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("新增", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("存量", result.Detail, StringComparison.Ordinal);
    }

    private static List<EventTimeSpan> S1Overlaps(DateTime at) => new()
    {
        new() { EventId = "a1", DeviceId = "PC-1", EventType = "window", StartTime = at, EndTime = at.AddMinutes(10) },
        new() { EventId = "a2", DeviceId = "PC-1", EventType = "window", StartTime = at.AddMinutes(5), EndTime = at.AddMinutes(15) }
    };

    private static List<LongEventCandidate> S2Unclosed(DateTime at) => new()
    {
        new()
        {
            EventId = "long-1",
            DeviceId = "PC-1",
            EventType = "window",
            StartTime = at,
            EndTime = at.AddMinutes(45),
            Keystrokes = 0,
            MouseClicks = 0,
            AppName = "idle.exe"
        }
    };

    private static List<BusinessRecordKey> S4Duplicates(DateTime at) => new()
    {
        BusinessRecordKey.ForPc("PC-1", at, 60, "active", "chrome.exe", "Chrome", "inst-1"),
        BusinessRecordKey.ForPc("PC-1", at, 60, "active", "chrome.exe", "Chrome", "inst-1")
    };

    private static List<ClockEventItem> S5Skew(DateTime receivedAt) => new()
    {
        new()
        {
            EventId = "clock-1",
            DeviceId = "PC-1",
            EventTime = receivedAt.AddMinutes(10),
            ServerReceivedTime = receivedAt
        }
    };

    private static List<TimelineInterval> S7UnmarkedHole(DateTime at, string deviceId = "PC-1") => new()
    {
        new() { DeviceId = deviceId, StartTime = at, EndTime = at.AddMinutes(10), IsGap = false, EventType = "window" },
        new() { DeviceId = deviceId, StartTime = at.AddHours(2), EndTime = at.AddHours(2).AddMinutes(10), IsGap = false, EventType = "window" }
    };

    private static List<BatchSyncStatusRecord> S11InconsistentBatch(DateTime windowStart) => new()
    {
        new()
        {
            BatchId = "BATCH-1",
            FailedCount = 0,
            RejectedCount = 10,
            TotalCount = 100,
            Status = "failed",
            WindowStartUtc = windowStart
        }
    };

    private static List<CollectionHeartbeat> S13ConcurrentInstances(DateTime at) => new()
    {
        new() { DeviceId = "PC-1", Timestamp = at, DurationSeconds = 600.2, InstanceId = "inst-A", SessionId = 1 },
        new() { DeviceId = "PC-1", Timestamp = at.AddSeconds(600), DurationSeconds = 60, InstanceId = "inst-B", SessionId = 2 }
    };

    /// <summary>一条窗内 + 一条窗外，用于验证"不重不漏"（AC-2.6）。</summary>
    private static InvariantResult MixedFixture(string ruleCode) => ruleCode switch
    {
        "S1" => DataReliabilityInvariants.CheckS1_NoOverlap(
            S1Overlaps(WindowTimeUtc).Concat(S1Overlaps(DebtTimeUtc)).ToList(), referenceTimeUtc: NowUtc),
        "S2" => DataReliabilityInvariants.CheckS2_OverlongEventEvidence(
            S2Unclosed(WindowTimeUtc).Concat(S2Unclosed(DebtTimeUtc)).ToList(), referenceTimeUtc: NowUtc),
        "S4" => DataReliabilityInvariants.CheckS4_BusinessKeyUnique(
            S4Duplicates(WindowTimeUtc).Concat(S4Duplicates(DebtTimeUtc)).ToList(), referenceTimeUtc: NowUtc),
        "S5" => DataReliabilityInvariants.CheckS5_ClockTrustworthy(
            S5Skew(WindowTimeUtc).Concat(S5Skew(DebtTimeUtc)).ToList(), referenceTimeUtc: NowUtc),
        "S6" => DataReliabilityInvariants.CheckS6_OfflineDeclared(
            new DeviceActivityTrace
            {
                DeviceId = "PC-1",
                EventIntervals = new (DateTime, DateTime)[]
                {
                    (DebtTimeUtc, DebtTimeUtc.AddMinutes(10)),
                    (DebtTimeUtc.AddHours(2), DebtTimeUtc.AddHours(2).AddMinutes(10)),
                    (WindowTimeUtc.AddHours(-2), WindowTimeUtc.AddHours(-2).AddMinutes(10)),
                    (WindowTimeUtc, WindowTimeUtc.AddMinutes(10))
                },
                // 两组之间跨了 10 天：必须声明为正常离线，否则那段更长的空档会再多算一处违规，
                // 让"窗内 1 + 欠账 1 = 总数 2"失去意义。
                Declarations = new[]
                {
                    new OfflineDeclaration
                    {
                        DeviceId = "PC-1",
                        StartTime = DebtTimeUtc.AddHours(2).AddMinutes(10),
                        EndTime = WindowTimeUtc.AddHours(-2),
                        Reason = "shutdown"
                    }
                }
            },
            referenceTimeUtc: NowUtc),
        "S7" => DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            S7UnmarkedHole(DebtTimeUtc, "PC-DEBT").Concat(S7UnmarkedHole(WindowTimeUtc, "PC-WINDOW")).ToList(), referenceTimeUtc: NowUtc),
        "S11" => DataReliabilityInvariants.CheckS11_StatusSemantics(
            S11InconsistentBatch(WindowTimeUtc)
                .Concat(S11InconsistentBatch(DebtTimeUtc).Select(b => new BatchSyncStatusRecord
                {
                    BatchId = "BATCH-2",
                    FailedCount = b.FailedCount,
                    RejectedCount = b.RejectedCount,
                    TotalCount = b.TotalCount,
                    Status = b.Status,
                    WindowStartUtc = b.WindowStartUtc
                }))
                .ToList(),
            referenceTimeUtc: NowUtc),
        _ => DataReliabilityInvariants.CheckS13_SingleInstance(
            S13ConcurrentInstances(DebtTimeUtc)
                .Concat(S13ConcurrentInstances(WindowTimeUtc))
                .Select((h, index) => new CollectionHeartbeat
                {
                    DeviceId = index < 2 ? "PC-DEBT" : "PC-WINDOW",
                    Timestamp = h.Timestamp,
                    DurationSeconds = h.DurationSeconds,
                    InstanceId = h.InstanceId,
                    SessionId = h.SessionId
                })
                .ToList(),
            referenceTimeUtc: NowUtc)
    };

    #endregion
}
