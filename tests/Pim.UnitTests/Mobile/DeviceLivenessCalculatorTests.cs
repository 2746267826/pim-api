using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Liveness;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// REQ-13 静默与覆盖率口径的纯函数验证（AC-13.1 / AC-13.2 / AC-7.4 / AC-11.1 / AC-7.5）。
/// 口径只有一份实现（<see cref="DeviceLivenessCalculator"/>），页面/摘要/体检都从这里取数。
/// </summary>
public sealed class DeviceLivenessCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RangeStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RangeEnd = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    private static LivenessEvidence Heartbeat(DateTimeOffset at) =>
        new(at, LivenessEvidenceSources.Heartbeat);

    private static LivenessEvidence Batch(DateTimeOffset at) =>
        new(at, LivenessEvidenceSources.SyncBatch);

    [Fact]
    public void Summarize_WithoutEvidence_ReturnsNoDataConclusionAndNoCoverage()
    {
        // AC-7.5 / AC-11.4 / AC-10.2：没有存活证据时必须显示"无数据/未上报"，且不能返回 0% 当结论。
        var summary = DeviceLivenessCalculator.Summarize(
            Array.Empty<LivenessEvidence>(),
            null,
            RangeStart,
            RangeEnd);

        Assert.False(summary.HasData);
        Assert.Equal(DeviceLivenessRules.NoDataConclusion, summary.Conclusion);
        Assert.Null(summary.CoverageByHour);
        Assert.Null(summary.CoverageByExpectedHeartbeat);
        Assert.Null(summary.LastEventAtUtc);
    }

    [Fact]
    public void Summarize_CoverageByHour_MatchesExposedNumeratorAndDenominator()
    {
        // AC-13.1：两个覆盖率都要能由同一批数据按定义复算，因此必须暴露分子/分母。
        var evidence = new[]
        {
            Heartbeat(RangeStart.AddHours(1)),
            Heartbeat(RangeStart.AddHours(1).AddMinutes(30)),
            Heartbeat(RangeStart.AddHours(5)),
        };

        var summary = DeviceLivenessCalculator.Summarize(evidence, null, RangeStart, RangeEnd);

        Assert.Equal(7 * 24, summary.TotalHours);
        Assert.Equal(2, summary.ObservedHours);
        Assert.Equal(2d / (7 * 24), summary.CoverageByHour!.Value, 4);
    }

    [Fact]
    public void Summarize_CoverageByExpectedHeartbeat_UsesConfiguredCadenceAndIsCappedAtOne()
    {
        // 口径分母 = 区间分钟数 / 15（既有周期同步节奏），上限 100%。
        var expected = (int)Math.Ceiling((RangeEnd - RangeStart).TotalMinutes / 15d);
        var heartbeats = Enumerable.Range(0, 10)
            .Select(i => Heartbeat(RangeStart.AddMinutes(i * 15)))
            .ToArray();

        var summary = DeviceLivenessCalculator.Summarize(heartbeats, null, RangeStart, RangeEnd);

        Assert.Equal(15, summary.ExpectedHeartbeatIntervalMinutes);
        Assert.Equal(expected, summary.ExpectedHeartbeats);
        Assert.Equal(10, summary.ObservedHeartbeats);
        Assert.Equal(Math.Round(10d / expected, 4), summary.CoverageByExpectedHeartbeat!.Value, 4);

        var dense = Enumerable.Range(0, expected + 50)
            .Select(i => Heartbeat(RangeStart.AddMinutes(i)))
            .ToArray();
        var capped = DeviceLivenessCalculator.Summarize(dense, null, RangeStart, RangeEnd);
        Assert.Equal(1d, capped.CoverageByExpectedHeartbeat!.Value);
    }

    [Fact]
    public void ClassifySilence_IsIndependentOfTimeOfDay()
    {
        // AC-13.2：夜间（23:30-8:00）与白天使用同一条静默判定线。
        // 判定只看时长，因此同一时长在夜间与白天必须得到同一个标色。
        Assert.Equal(SilenceSeverities.None, DeviceLivenessCalculator.ClassifySilence(29.9));
        Assert.Equal(SilenceSeverities.Warning, DeviceLivenessCalculator.ClassifySilence(30));
        Assert.Equal(SilenceSeverities.Critical, DeviceLivenessCalculator.ClassifySilence(60));
        Assert.Equal(SilenceSeverities.Critical, DeviceLivenessCalculator.ClassifySilence(600));

        var nightStart = new DateTimeOffset(2026, 9, 1, 23, 30, 0, TimeSpan.Zero);
        var nightSummary = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(nightStart), Heartbeat(nightStart.AddMinutes(30)) },
            null,
            nightStart,
            nightStart.AddMinutes(30));

        var dayStart = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var daySummary = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(dayStart), Heartbeat(dayStart.AddMinutes(30)) },
            null,
            dayStart,
            dayStart.AddMinutes(30));

        Assert.Equal(SilenceSeverities.Warning, nightSummary.LongestSilenceSeverity);
        Assert.Equal(SilenceSeverities.Warning, daySummary.LongestSilenceSeverity);
        Assert.Equal(nightSummary.LongestSilenceMinutes, daySummary.LongestSilenceMinutes);
    }

    [Fact]
    public void Summarize_SilenceBelowThirtyMinutesIsNotMarked()
    {
        // AC-7.4：<30 分钟的缺口不标记；≥30 分钟标黄；≥1 小时标红。
        var start = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var shortGap = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(start), Heartbeat(start.AddMinutes(29)) },
            null,
            start,
            start.AddMinutes(29));

        Assert.Empty(shortGap.Silences);
        Assert.Equal(SilenceSeverities.None, shortGap.LongestSilenceSeverity);
        Assert.Equal(0, shortGap.LongestSilenceMinutes);
        Assert.False(shortGap.HasSilenceOverOneHour);

        var longGap = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(start), Heartbeat(start.AddMinutes(90)) },
            null,
            start,
            start.AddMinutes(90));

        var silence = Assert.Single(longGap.Silences);
        Assert.Equal(90, silence.Minutes, 3);
        Assert.Equal(SilenceSeverities.Critical, silence.Severity);
        Assert.Equal(90, longGap.LongestSilenceMinutes);
        Assert.True(longGap.HasSilenceOverOneHour);
    }

    [Fact]
    public void Summarize_TrailingSilenceToRangeEndIsCounted()
    {
        // 设备"从此不再醒来"时，最后一颗心搏到区间终点同样是一段静默，不能被漏掉。
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var summary = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(start) },
            null,
            start,
            start.AddMinutes(53.2 * 60));

        Assert.True(summary.HasSilenceOverOneHour);
        Assert.Equal(3192, summary.LongestSilenceMinutes);
        Assert.Equal(SilenceSeverities.Critical, summary.LongestSilenceSeverity);
    }

    [Fact]
    public void Summarize_CauseDistributionKeepsInferenceForUnknownCauses()
    {
        // AC-7.1：死因分布含"未知 + 推断依据"。
        var causes = new[]
        {
            new LivenessCauseCount("low-memory", "内存不足被系统回收", 2),
            new LivenessCauseCount("unknown", "系统未给出原因", 1, "未能识别的退出原因：REASON_42"),
        };

        var summary = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(RangeStart) },
            causes,
            RangeStart,
            RangeEnd);

        Assert.Equal(2, summary.Causes.Count);
        Assert.Equal(2, summary.Causes[0].Count);
        var unknown = summary.Causes.Single(item => item.Cause == "unknown");
        Assert.NotNull(unknown.Inference);
        Assert.Contains("REASON_42", unknown.Inference);
    }

    [Fact]
    public void Summarize_ExcludesEvidenceOutsideRange()
    {
        var summary = DeviceLivenessCalculator.Summarize(
            new[] { Heartbeat(RangeStart.AddDays(-1)), Heartbeat(RangeEnd.AddMinutes(1)) },
            null,
            RangeStart,
            RangeEnd);

        Assert.False(summary.HasData);
    }

    [Fact]
    public void Summarize_CountsSyncBatchArrivalAsAuxiliaryEvidence()
    {
        // REQ-13：静默以心搏为主、同步批次为辅；批次到达本身是可信存活证据。
        var summary = DeviceLivenessCalculator.Summarize(
            new[] { Batch(RangeStart.AddHours(2)) },
            null,
            RangeStart,
            RangeEnd);

        Assert.True(summary.HasData);
        Assert.Equal(0, summary.ObservedHeartbeats);
        Assert.Equal(1, summary.ObservedHours);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Summarize_RejectsNonPositiveRange(int minutes)
    {
        var end = RangeStart.AddMinutes(minutes);
        Assert.ThrowsAny<ArgumentException>(() =>
            DeviceLivenessCalculator.Summarize(Array.Empty<LivenessEvidence>(), null, RangeStart, end));
    }

    [Fact]
    public void Definitions_MentionBothCoverageAlgorithms()
    {
        // AC-13.1：页面要能读到两种覆盖率的定义。
        Assert.Contains("整点小时", DeviceLivenessRules.CoverageByHourDefinition);
        Assert.Contains("应有心跳", DeviceLivenessRules.CoverageByExpectedHeartbeatDefinition);
        Assert.Contains("15", DeviceLivenessRules.CoverageByExpectedHeartbeatDefinition);
    }

    // ===================== REQ-18：叫醒兑现率 =====================

    /// <summary>
    /// AC-18.3（反面语义）：**缺少实际时刻的记录不计入分母**。
    /// 把设备关机当成「未兑现」会让一次出差关机把兑现率砸到很低，
    /// 而那是设备状态不是保活失效——口径错了会直接误导判断。
    /// </summary>
    [Fact]
    public void Fulfillment_ExcludesRecordsWithoutActualTimeFromDenominator()
    {
        var result = DeviceLivenessCalculator.ComputeFulfillment(new[]
        {
            new AlarmFulfillmentSample(Now, 0),
            new AlarmFulfillmentSample(Now, null),
            new AlarmFulfillmentSample(Now, null),
        });

        Assert.NotNull(result);
        Assert.Equal(1, result!.Considered);
        Assert.Equal(2, result.ExcludedNoActualTime);
        Assert.Equal(1d, result.Rate);
    }

    /// <summary>AC-18.2：没有可判定记录时兑现率为 null，页面不得显示为 0%。</summary>
    [Fact]
    public void Fulfillment_IsNullWhenNothingWasExecuted()
    {
        var onlyNotExecuted = DeviceLivenessCalculator.ComputeFulfillment(new[]
        {
            new AlarmFulfillmentSample(Now, null),
        });
        Assert.NotNull(onlyNotExecuted);
        Assert.Null(onlyNotExecuted!.Rate);

        var none = DeviceLivenessCalculator.ComputeFulfillment(Array.Empty<AlarmFulfillmentSample>());
        Assert.Null(none);
    }

    /// <summary>AC-18.2：没有闹钟数据的设备（samples 为 null）不得被算成 0%。</summary>
    [Fact]
    public void Fulfillment_IsNullWhenDeviceHasNoAlarmData()
    {
        Assert.Null(DeviceLivenessCalculator.ComputeFulfillment(null));
    }

    /// <summary>AC-18.1 / 判定线：≤15 分钟算按时，&gt;15 分钟不算（与 AC-17.1 同源）。</summary>
    [Fact]
    public void Fulfillment_UsesTheFifteenMinuteLine()
    {
        var result = DeviceLivenessCalculator.ComputeFulfillment(new[]
        {
            new AlarmFulfillmentSample(Now, 15),
            new AlarmFulfillmentSample(Now, 15.1),
        });

        Assert.Equal(1, result!.Fulfilled);
        Assert.Equal(2, result.Considered);
        Assert.Equal(0.5d, result.Rate);
        Assert.Equal(15, DeviceLivenessRules.FulfillmentOnTimeMinutes);
    }

    /// <summary>确实执行但都不按时 → 0%，与「无数据」（null）区分开。</summary>
    [Fact]
    public void Fulfillment_IsZeroWhenExecutedButNeverOnTime()
    {
        var result = DeviceLivenessCalculator.ComputeFulfillment(new[]
        {
            new AlarmFulfillmentSample(Now, 40),
            new AlarmFulfillmentSample(Now, 30),
        });

        Assert.Equal(0d, result!.Rate);
        Assert.Equal(0, result.Fulfilled);
        Assert.Equal(2, result.Considered);
    }

    /// <summary>AC-18.3：口径说明必须写明「不计入分母」，页面上要能读到。</summary>
    [Fact]
    public void Fulfillment_DefinitionStatesTheDenominatorRule()
    {
        Assert.Contains("不计入分母", DeviceLivenessRules.FulfillmentDefinition);
    }
}
