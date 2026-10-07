using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// WO-ISSUES-396-400-20261007 · REQ-2（#397）：S6 的「上传滞后 p99」必须以
/// <b>该条事件何时可以上传</b>（= 事件区间**结束**时刻）为基准，
/// 不得把「事件区间起点到区间结束」这段采集时长算成链路延迟。
/// <para>
/// 生产实测命中样本：<c>timestamp=2026-10-06T04:52:41.229Z</c>、<c>duration=1800s</c>、
/// <c>created_at=2026-10-06T05:23:03.032Z</c> —— 按区间起点算滞后 30.4 分钟（贴线报红），
/// 按区间结束算只有 22 秒（链路健康）。阈值 30 分钟不变。
/// </para>
/// 对应 AC-2.2。
/// </summary>
public class Wo397UploadLagBasisTests
{
    private static readonly DateTime T = new(2026, 10, 6, 4, 52, 41, 229, DateTimeKind.Utc);

    /// <summary>构造只含一条事件区间的 trace：区间内无空档，S6 只剩上传滞后一项可判。</summary>
    private static DeviceActivityTrace Trace(DateTime intervalStart, double durationSeconds, double lagMinutes)
    {
        var intervalEnd = intervalStart.AddSeconds(durationSeconds);
        return new DeviceActivityTrace
        {
            DeviceId = "PC-01",
            EventIntervals = new List<(DateTime, DateTime)> { (intervalStart, intervalEnd) },
            Declarations = Array.Empty<OfflineDeclaration>(),
            UploadLagSamples = new List<UploadLagSample>
            {
                new() { UploadableAt = intervalEnd, CreatedAt = intervalEnd.AddMinutes(lagMinutes) }
            }
        };
    }

    [Fact]
    public void Ac2_2_FortyMinuteChainBacklog_IsFlaggedWithWorstLag()
    {
        // AC-2.2 上半：可上传时刻 → 服务端接收时刻相差 40 分钟 → 判违规并给出 worstLagMinutes。
        var trace = Trace(T, 1800, 40);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            trace, referenceTimeUtc: T.AddDays(1));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("upload-lag-p99", violation.Fields["kind"]);
        Assert.Equal("40.0", violation.Fields["worstLagMinutes"]);
    }

    [Fact]
    public void Ac2_2_FiveMinuteLag_IsNotFlagged()
    {
        // AC-2.2 下半：同差为 5 分钟 → 不违规。
        var trace = Trace(T, 1800, 5);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            trace, referenceTimeUtc: T.AddDays(1));

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void Ac2_2_ProductionSample_ThirtyMinuteSliceUploadedIn22Seconds_IsNotLag()
    {
        // 生产命中样本：1800 秒切片在区间结束后 22 秒完成上传。
        // 旧口径按区间**起点**算得 30.4 分钟 → 超过 30 分钟阈值 → 假红；
        // 新口径按区间**结束**算得 22 秒 → 不违规。
        var trace = Trace(T, 1800, 22.0 / 60.0);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            trace, referenceTimeUtc: T.AddDays(1));

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);

        // 反向确认：同一份输入若按区间**起点**计滞后是 1800 秒 + 22 秒 = 30.37 分钟（> 30 分钟阈值），
        // 这正是本次从指标里去掉的那个量 —— 它不是链路延迟。
        double legacyLagMinutes = ((T.AddSeconds(1800).AddSeconds(22)) - T).TotalMinutes;
        Assert.True(legacyLagMinutes > 30.0, $"按区间起点计的滞后应超过阈值，实际 {legacyLagMinutes:F2} 分钟");
    }

    [Fact]
    public void Ac2_2_LagDoesNotScaleWithIntervalDuration()
    {
        // 两条事件的"区间结束 → 接收"都只差 20 分钟，但区间自身时长相差 6 倍。
        // 基准是区间结束，所以 p99 必须都是 20 分钟；若把区间自身时长算进去，
        // 后一条会变成 60 分钟并被判违规。
        var second = T.AddMinutes(20);
        var trace = new DeviceActivityTrace
        {
            DeviceId = "PC-01",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (T, T.AddMinutes(10)),
                (second, second.AddMinutes(40))
            },
            Declarations = Array.Empty<OfflineDeclaration>(),
            UploadLagSamples = new List<UploadLagSample>
            {
                new() { UploadableAt = T.AddMinutes(10), CreatedAt = T.AddMinutes(30) },
                new() { UploadableAt = second.AddMinutes(40), CreatedAt = second.AddMinutes(60) }
            }
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(
            trace, referenceTimeUtc: T.AddDays(1));

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }
}
