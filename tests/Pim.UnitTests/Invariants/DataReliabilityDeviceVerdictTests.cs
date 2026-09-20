using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// S6 这类"逐设备判定"尺子的结论合并（#260）：合并语义必须保守 ——
/// 任一台设备报红即整条报红；有设备无法判定时整条记未知，绝不替没数据的设备背书。
/// </summary>
public class DataReliabilityDeviceVerdictTests
{
    private static InvariantResult Pass() =>
        InvariantResult.Success("INV-P20 PASS: 该设备无声明空档与上传延迟均在指标内");

    private static InvariantResult Fail(string device, int gaps) =>
        InvariantResult.Failure(
            $"INV-P20 FAIL: 设备 {device} 检测到 {gaps} 处无声明空档",
            gaps,
            gaps,
            0,
            new[] { $"Device={device}: 存在 {gaps} 处无声明空档" },
            null,
            null,
            violations: new[]
            {
                new InvariantViolation(
                    $"{device}:undeclared-gap:0",
                    device,
                    new DateTime(2026, 9, 14, 1, 0, 0, DateTimeKind.Utc),
                    new Dictionary<string, string> { ["gapMinutes"] = "120.0" })
            });

    private static InvariantResult Unknown(string reason) => InvariantResult.Unknown($"INV-P20 UNKNOWN: {reason}");

    [Fact]
    public void AllDevicesPass_IsPass()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), Pass(), Pass() });

        Assert.Equal(InvariantStatus.Pass, result.Status);
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("3 台设备", result.Detail);
    }

    [Fact]
    public void AnyDeviceFails_IsFailAndAggregatesCounts()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), Fail("dev-a", 2), Fail("dev-b", 3) });

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(5, result.TotalViolations);
        Assert.Equal(2, result.Samples.Count);
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains("覆盖 2 台设备", result.Detail);
    }

    /// <summary>有设备通过、也有设备无法判定时，整条必须记未知 —— 不能因为部分设备健康就报绿。</summary>
    [Fact]
    public void MixedPassAndUnknown_IsUnknown()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), Unknown("设备 dev-b 缺失心跳时序") });

        Assert.Equal(InvariantStatus.Unknown, result.Status);
        Assert.False(result.Pass);
        Assert.Contains("部分设备无数据可判定", result.Detail);
    }

    [Fact]
    public void AllDevicesUnknown_IsUnknown()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Unknown("没有事件"), Unknown("没有事件") });

        Assert.Equal(InvariantStatus.Unknown, result.Status);
        Assert.Contains("数据源为空或未接线", result.Detail);
    }

    [Fact]
    public void EmptyInput_IsUnknown()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts("INV-P20", Array.Empty<InvariantResult>());

        Assert.Equal(InvariantStatus.Unknown, result.Status);
        Assert.False(result.Pass);
    }

    [Fact]
    public void FailBeatsUnknown()
    {
        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Unknown("没有事件"), Fail("dev-a", 1) });

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void SamplesRespectConfiguredCap()
    {
        var results = new List<InvariantResult>();
        for (int i = 0; i < 20; i++)
        {
            results.Add(Fail($"dev-{i}", 1));
        }

        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            results,
            new InvariantOptions { MaxSampleCount = 3 });

        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(3, result.Violations.Count);
        Assert.Equal(20, result.TotalViolations);
    }

    [Fact]
    public void DeviceLevelWarning_IsNotFoldedIntoPass()
    {
        // 回归防线（#254 S6）：逐设备判定在"仅有存量违规"时返回 Warning（生产路径是
        // InvariantResult.Failure(..., isWarning: true)，因此带得住真实违规计数）。
        // 合并时若不看 Warning，整条尺子会被折成绿灯 —— 面板显示"全绿"而实际存在存量欠账。
        var warning = InvariantResult.Failure(
            "INV-P20 WARN: 设备 dev-a 检测到 29 处存量违规",
            29,
            0,
            29,
            new[] { "Device=dev-a: 29 处无声明空档" },
            isWarning: true);

        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), warning });

        Assert.Equal(InvariantStatus.Warning, result.Status);
        Assert.True(result.IsWarning);
        Assert.False(result.IsFail);
        Assert.Equal(0, result.NewViolations);
        Assert.Equal(29, result.TotalViolations);
        Assert.Equal(29, result.HistoricalViolations);
    }

    [Fact]
    public void DeviceLevelWarning_WhenAnotherDeviceFails_StaysFail()
    {
        // 有设备真报红时，整条仍须是红线（黄线不得盖住红线）
        var warning = InvariantResult.Failure("INV-P20 WARN: 存量违规", 5, 0, 5, new[] { "dev-a: 存量" }, isWarning: true);

        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), warning, Fail("dev-b", 2) });

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.False(result.IsWarning);
        Assert.Equal(2, result.TotalViolations);
    }

    [Fact]
    public void DeviceLevelWarning_WhenAnotherDeviceUnknown_ReportsUnknown()
    {
        // 无法判定的设备优先于黄线：不能因为"有没有声明的设备"就替没数据的设备背书
        var warning = InvariantResult.Failure("INV-P20 WARN: 存量违规", 5, 0, 5, new[] { "dev-a: 存量" }, isWarning: true);

        var result = DataReliabilityInvariants.CombineDeviceVerdicts(
            "INV-P20",
            new[] { Pass(), warning, Unknown("设备 dev-c 无数据") });

        Assert.Equal(InvariantStatus.Unknown, result.Status);
    }
}
