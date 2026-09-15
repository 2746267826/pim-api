using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// S2 三态分布（#260 §3）：设置页要展示"操作活跃 / 观看活跃 / 疑似未收尾"的时长占比，
/// 归类顺序必须与 <see cref="DataReliabilityInvariants.CheckS2_OverlongEventEvidence"/> 完全一致，
/// 否则面板会与判据打架（EPIC #254 G4：判据只有一份实现）。
/// </summary>
public class DataReliabilityThreeStateTests
{
    private static readonly DateTime Base = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private static LongEventCandidate Event(
        double minutes,
        string type = "window",
        long keystrokes = 0,
        long mouseClicks = 0,
        bool isMediaActive = false,
        bool isAudible = false,
        bool isGapOrOffline = false,
        string deviceId = "dev-1",
        string? app = null)
    {
        return new LongEventCandidate
        {
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            EventType = type,
            StartTime = Base,
            EndTime = Base.AddMinutes(minutes),
            Keystrokes = keystrokes,
            MouseClicks = mouseClicks,
            IsMediaActive = isMediaActive,
            IsAudible = isAudible,
            IsGapOrOffline = isGapOrOffline,
            AppName = app
        };
    }

    [Fact]
    public void EmptyInput_ReturnsAllZeros()
    {
        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(new List<LongEventCandidate>());

        Assert.Equal(0d, distribution.InputActiveSeconds);
        Assert.Equal(0d, distribution.MediaActiveSeconds);
        Assert.Equal(0d, distribution.SuspectedUnclosedSeconds);
        Assert.Equal(0d, distribution.TotalSeconds);
        Assert.Equal(0d, distribution.DeclaredGapSeconds);
        Assert.Equal(0, distribution.InputActiveCount);
        Assert.Equal(0, distribution.MediaActiveCount);
        Assert.Equal(0, distribution.SuspectedUnclosedCount);
    }

    [Fact]
    public void EventsBelowLongLine_AreIgnored()
    {
        var events = new[]
        {
            Event(30, keystrokes: 3000),   // 恰好 30 分钟：不超线，按 CheckS2 的口径忽略
            Event(29, keystrokes: 3000)
        };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(0d, distribution.TotalSeconds);
        Assert.Equal(0d, distribution.DeclaredGapSeconds);
    }

    [Fact]
    public void DeclaredGap_IsExcludedFromTheThreeStates()
    {
        var events = new[] { Event(536, type: "gap", isGapOrOffline: true) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(536 * 60d, distribution.DeclaredGapSeconds);
        Assert.Equal(0d, distribution.TotalSeconds);
    }

    [Fact]
    public void DeclaredGapWins_EvenWhenItCarriesInputDensity()
    {
        // 顺序与 CheckS2 一致：先判"明确空档"，再判操作活跃。
        var events = new[] { Event(120, type: "shutdown", keystrokes: 12000, isGapOrOffline: true) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(120 * 60d, distribution.DeclaredGapSeconds);
        Assert.Equal(0d, distribution.InputActiveSeconds);
        Assert.Equal(0, distribution.InputActiveCount);
    }

    [Fact]
    public void InputDensityWins_OverMediaActivity()
    {
        // 顺序与 CheckS2 一致：操作活跃优先于观看活跃。
        var events = new[] { Event(60, keystrokes: 600, isMediaActive: true) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(60 * 60d, distribution.InputActiveSeconds);
        Assert.Equal(0d, distribution.MediaActiveSeconds);
        Assert.Equal(0d, distribution.SuspectedUnclosedSeconds);
    }

    [Fact]
    public void MediaActivity_CountsAsWatchingWithoutInput()
    {
        var events = new[] { Event(90, mouseClicks: 10, isMediaActive: true) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(90 * 60d, distribution.MediaActiveSeconds);
        Assert.Equal(1, distribution.MediaActiveCount);
        Assert.Equal(0d, distribution.SuspectedUnclosedSeconds);
    }

    [Fact]
    public void AudibleAloneAlsoCountsAsWatching()
    {
        var events = new[] { Event(45, isAudible: true) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(45 * 60d, distribution.MediaActiveSeconds);
        Assert.Equal(0d, distribution.SuspectedUnclosedSeconds);
    }

    [Fact]
    public void NoEvidenceAtAll_CountsAsSuspectedUnclosed()
    {
        var events = new[] { Event(444, type: "idle", keystrokes: 6) };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(444 * 60d, distribution.SuspectedUnclosedSeconds);
        Assert.Equal(1, distribution.SuspectedUnclosedCount);
        Assert.Equal(0d, distribution.InputActiveSeconds);
    }

    [Fact]
    public void TotalSeconds_IsTheSumOfTheThreeStatesOnly()
    {
        var events = new[]
        {
            Event(159, keystrokes: 13426),                       // 操作活跃
            Event(90, isMediaActive: true),                      // 观看活跃
            Event(444, type: "idle", keystrokes: 6),             // 疑似未收尾
            Event(536, type: "gap", isGapOrOffline: true)        // 明确空档：不进 TotalSeconds
        };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(159 * 60d, distribution.InputActiveSeconds);
        Assert.Equal(90 * 60d, distribution.MediaActiveSeconds);
        Assert.Equal(444 * 60d, distribution.SuspectedUnclosedSeconds);
        Assert.Equal(536 * 60d, distribution.DeclaredGapSeconds);
        Assert.Equal((159 + 90 + 444) * 60d, distribution.TotalSeconds);
        Assert.Equal(1, distribution.InputActiveCount);
        Assert.Equal(1, distribution.MediaActiveCount);
        Assert.Equal(1, distribution.SuspectedUnclosedCount);
    }

    /// <summary>
    /// EPIC #254 §5 的实测样例：valorant 159 分钟 / 13,426 次按键 → 操作活跃；
    /// gap 536 分钟 / 0 采样 → 明确的空档；idle 444 分钟 / 6 次按键 → 疑似未收尾。
    /// </summary>
    [Fact]
    public void EpicSampledEvents_ReproduceTheDocumentedClassification()
    {
        var events = new[]
        {
            Event(159, app: "VALORANT", keystrokes: 13426),
            Event(536, type: "gap", isGapOrOffline: true),
            Event(444, type: "idle", keystrokes: 6)
        };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);

        Assert.Equal(159 * 60d, distribution.InputActiveSeconds);
        Assert.Equal(536 * 60d, distribution.DeclaredGapSeconds);
        Assert.Equal(444 * 60d, distribution.SuspectedUnclosedSeconds);
        Assert.Equal(0d, distribution.MediaActiveSeconds);
    }

    [Fact]
    public void CustomOptions_ChangeTheClassificationBoundaries()
    {
        var options = new InvariantOptions
        {
            LongEventThresholdMinutes = 60,
            MinInputDensityPerMinute = 10
        };

        var events = new[]
        {
            Event(45, keystrokes: 4500),   // 低于自定义超长线：忽略
            Event(120, keystrokes: 600)    // 密度 5 < 10：不算操作活跃 → 疑似未收尾
        };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events, options);

        Assert.Equal(120 * 60d, distribution.SuspectedUnclosedSeconds);
        Assert.Equal(0d, distribution.InputActiveSeconds);
    }

    [Fact]
    public void Classification_IsConsistentWithCheckS2Violations()
    {
        // 同一批数据：判据判出的违规数必须等于三态里的"疑似未收尾"条数。
        var referenceTime = Base.AddDays(1);
        var events = new[]
        {
            Event(159, keystrokes: 13426),
            Event(536, type: "gap", isGapOrOffline: true),
            Event(444, type: "idle", keystrokes: 6),
            Event(90, isMediaActive: true),
            Event(61) // 无任何证据
        };

        var distribution = DataReliabilityInvariants.ClassifyS2ThreeStates(events);
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(
            events,
            options: null,
            referenceTimeUtc: referenceTime);

        Assert.Equal(2, distribution.SuspectedUnclosedCount);
        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(InvariantStatus.Fail, result.Status);
    }
}
