using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

public class InvariantOptionsAndMigrationTests
{
    [Fact]
    public void InvariantOptions_ValidValues_ResolvedWithoutFallback()
    {
        var options = new InvariantOptions
        {
            LongEventThresholdMinutes = 45.0,
            MinInputDensityPerMinute = 2.0,
            MaxDailyActiveHours = 20.0,
            AwakeWindowHours = 18.0,
            AwakeWindowWarningRatio = 0.85,
            ClockSkewToleranceMinutes = 10.0,
            UndeclaredOfflineGapMinutes = 40.0,
            MaxUploadLagP99Minutes = 25.0,
            TimelineGapThresholdMinutes = 20.0,
            CoverageRedRatio = 0.90,
            CoverageYellowRatio = 0.98,
            RecentWindowHours = 48.0,
            MaxSampleCount = 10
        };

        var (resolved, fallback, note) = InvariantOptions.Resolve(options);

        Assert.False(fallback);
        Assert.Null(note);
        Assert.Equal(45.0, resolved.LongEventThresholdMinutes);
        Assert.Equal(0.90, resolved.CoverageRedRatio);
    }

    [Fact]
    public void InvariantOptions_InvalidValues_FallbacksGracefullyWithNotes()
    {
        // 传入非法配置：超长事件阈值 <= 0，覆盖率 > 1.0，采样上限 <= 0
        var invalidOptions = new InvariantOptions
        {
            LongEventThresholdMinutes = -5.0,
            CoverageRedRatio = 1.5,
            MaxDailyActiveHours = 30.0, // 超过物理 24 小时
            MaxSampleCount = -1
        };

        var (resolved, fallback, note) = InvariantOptions.Resolve(invalidOptions);

        Assert.True(fallback);
        Assert.NotNull(note);
        Assert.Contains("回退", note);

        // 回退到安全默认值
        Assert.Equal(30.0, resolved.LongEventThresholdMinutes);
        Assert.Equal(0.95, resolved.CoverageRedRatio);
        Assert.Equal(24.0, resolved.MaxDailyActiveHours);
        Assert.Equal(10, resolved.MaxSampleCount);
    }

    [Fact]
    public void CheckS2_WithDynamicOptions_AdjustsThresholdAccurately()
    {
        var start = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<LongEventCandidate>
        {
            // 40 分钟事件，无任何输入证据
            new()
            {
                DeviceId = "PC-1",
                EventType = "window",
                StartTime = start,
                EndTime = start.AddMinutes(40),
                Keystrokes = 0,
                MouseClicks = 0
            }
        };

        // 默认阈值 30 分钟：40m > 30m，判为未收尾 (FAIL)
        var defaultResult = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events);
        Assert.False(defaultResult.Pass);

        // 修改配置阈值为 60 分钟：40m <= 60m，不视为超长 (PASS)
        var relaxedOptions = new InvariantOptions { LongEventThresholdMinutes = 60.0 };
        var relaxedResult = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, relaxedOptions);
        Assert.True(relaxedResult.Pass);
    }

    [Fact]
    public async Task Inspector_WhenDbContextNull_ReportsUnhealthyAndAllUnknown()
    {
        // 验证体检服务在未接线或数据库不可用时：坚决判红/未知，绝不得报健康假绿灯
        var optionsMock = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(
            null,
            optionsMock,
            NullLogger<DataReliabilityQualityInspector>.Instance);

        var report = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        Assert.Equal("data_reliability", inspector.CheckName);
        Assert.False(report.IsHealthy); // 绝不得伪造健康
        Assert.True(report.IssueCount >= 13);
        Assert.NotNull(report.Details);
        Assert.Contains("S1_INV-P16", report.Details.Keys);
        Assert.Contains("S11_INV-M21", report.Details.Keys);
        Assert.Contains("S12_INV-M22", report.Details.Keys);
        Assert.Contains("S13_INV-P22", report.Details.Keys);

        // 全部 13 项判据标记为 ⚪ UNKNOWN
        Assert.StartsWith("⚪ UNKNOWN", report.Details["S1_INV-P16"]);
        Assert.StartsWith("⚪ UNKNOWN", report.Details["S12_INV-M22"]);
    }
}
