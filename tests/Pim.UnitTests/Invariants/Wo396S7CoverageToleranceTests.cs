using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// WO-ISSUES-396-400-20261007 · REQ-1（#396）：S7 覆盖判定必须按「多段 gap 合并后的**并集**是否盖住整个洞」。
/// <list type="bullet">
///   <item><description>相邻 gap 分片之间的毫秒级合并缝隙必须被合并（生产实测 1 毫秒）；</description></item>
///   <item><description>合并后的 coverage 端点与洞端点之差 ≤ 1 秒视为已覆盖（生产实测 26 毫秒）；</description></item>
///   <item><description>差得更多（10 秒 / 30 分钟）仍判未覆盖 —— 容差上限 1 秒（工单 D-1，10 秒那例属历史欠账不承诺）。</description></item>
/// </list>
/// 生产形状取自工单 references/C §3：洞 <c>[10-06 14:51:54.500Z ~ 10-07 03:18:58.363Z]</c>，
/// coverage 两块拼成 <c>[14:51:54.500 → 19:59:59.999]</c> 与 <c>[20:00:00.000 → 03:18:58.337]</c>。
/// 对应 AC-1.2 / AC-1.3。
/// </summary>
public class Wo396S7CoverageToleranceTests
{
    /// <summary>洞的起点：生产实测 2026-10-06T14:51:54.500Z。</summary>
    private static readonly DateTime HoleStart = new(2026, 10, 6, 14, 51, 54, 500, DateTimeKind.Utc);

    /// <summary>洞的右端点：生产实测 2026-10-07T03:18:58.363Z（与 HoleStart 相差 747.1 分钟）。</summary>
    private static readonly DateTime HoleEnd = new(2026, 10, 7, 3, 18, 58, 363, DateTimeKind.Utc);

    [Fact]
    public void Ac1_2_TwoGapChunksWithOneMillisecondSeamAnd26msEndpointDelta_IsCovered()
    {
        // AC-1.2：洞被两段首尾相差 1 毫秒的 gap 分片覆盖（合并后端点还差 26 毫秒）。
        // 旧实现只看**单个** coverage 段是否完整包住洞、容差只有 1 毫秒 —— 两段都盖不住整个洞，
        // 判定报红（生产假红的第 1 处）。
        var seam = new DateTime(2026, 10, 6, 19, 59, 59, 999, DateTimeKind.Utc); // 19:59:59.999
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "PC-01", StartTime = HoleStart.AddMinutes(-30), EndTime = HoleStart, EventType = "window" },
            new() { DeviceId = "PC-01", StartTime = HoleStart, EndTime = seam, IsGap = true, EventType = "gap" },
            // 下一片从 20:00:00.000 开始 —— 与上一片之间有 1 毫秒缝隙；
            // 合并后的 coverage 末端 03:18:58.337 与洞右端点差 26 毫秒。
            new() { DeviceId = "PC-01", StartTime = new DateTime(2026, 10, 6, 20, 0, 0, 0, DateTimeKind.Utc), EndTime = HoleEnd.AddMilliseconds(-26), IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = HoleEnd, EndTime = HoleEnd.AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            intervals, referenceTimeUtc: HoleEnd.AddDays(1));

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
        Assert.Equal(0, result.WindowViolations);
    }

    [Fact]
    public void Ac1_2b_SingleGapChunkWithinOneSecondEndpointDelta_IsCovered()
    {
        // 单段 coverage 与洞端点差 999 毫秒（≤1 秒容差）→ 视为已覆盖。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "PC-01", StartTime = HoleStart.AddMinutes(-30), EndTime = HoleStart, EventType = "window" },
            new() { DeviceId = "PC-01", StartTime = HoleStart, EndTime = HoleEnd.AddMilliseconds(-999), IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = HoleEnd, EndTime = HoleEnd.AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            intervals, referenceTimeUtc: HoleEnd.AddDays(1));

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void Ac1_3_HoleCoveredOnlyNinetyPercent_IsNotCovered()
    {
        // AC-1.3：洞只被覆盖 90%，末段差 30 分钟 → 必须判未覆盖并计入违规。
        var holeStart = HoleStart;
        var holeEnd = HoleStart.AddMinutes(90);
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "PC-01", StartTime = holeStart.AddMinutes(-10), EndTime = holeStart, EventType = "window" },
            // 只盖住 [0, 60min]，比 90 分钟的洞短 30 分钟。
            new() { DeviceId = "PC-01", StartTime = holeStart, EndTime = holeStart.AddMinutes(60), IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = holeEnd, EndTime = holeEnd.AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            intervals, referenceTimeUtc: holeEnd.AddDays(1));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Equal(1, result.WindowViolations);
        Assert.Contains("90.0m", result.Detail);
    }

    [Fact]
    public void Ac1_4_TenSecondEndpointDelta_StaysUncovered()
    {
        // 容差上限是 1 秒：coverage 末端比洞右端点短 10 秒时仍判未覆盖。
        // 生产历史欠账 i=5741 [10-05 14:20:48.755Z ~ 10-06 01:43:51.230Z] 就是这一形态，
        // 工单 D-1 明确不承诺吸收它。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "PC-01", StartTime = HoleStart.AddMinutes(-30), EndTime = HoleStart, EventType = "window" },
            new() { DeviceId = "PC-01", StartTime = HoleStart, EndTime = HoleEnd.AddSeconds(-10), IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = HoleEnd, EndTime = HoleEnd.AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            intervals, referenceTimeUtc: HoleEnd.AddDays(1));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void Ac1_4b_SeamWiderThanOneSecond_IsNotMerged()
    {
        // 分片之间 5 秒的空档超过合并缝隙上限（1 秒）→ 不是"拼接缝隙"，覆盖不完整。
        var seamLeft = HoleStart.AddMinutes(30);
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "PC-01", StartTime = HoleStart.AddMinutes(-30), EndTime = HoleStart, EventType = "window" },
            new() { DeviceId = "PC-01", StartTime = HoleStart, EndTime = seamLeft, IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = seamLeft.AddSeconds(5), EndTime = HoleEnd, IsGap = true, EventType = "gap" },
            new() { DeviceId = "PC-01", StartTime = HoleEnd, EndTime = HoleEnd.AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(
            intervals, referenceTimeUtc: HoleEnd.AddDays(1));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }
}
