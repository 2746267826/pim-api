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

    /// <summary>
    /// 复审回归（Minor）：**所有** double 阈值都必须做有限值校验，不能只管新增的那一个。
    /// NaN 不满足任何比较运算（`NaN &lt;= 0` 为 false），只写范围比较会把它放行；
    /// 随后该阈值参与的所有比较都返回 false。+∞ 同理（例如 S7 阈值设为 ∞ 会让所有空洞都不违规）。
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvariantOptions_NonFiniteDoubleThresholds_AreRejected(double badValue)
    {
        // 逐字段把每一个 double 阈值设成非有限值，断言都被拦下。
        var setters = new (string Name, Action<InvariantOptions, double> Set)[]
        {
            (nameof(InvariantOptions.MinInputDensityPerMinute), (o, v) => o.MinInputDensityPerMinute = v),
            (nameof(InvariantOptions.LongEventThresholdMinutes), (o, v) => o.LongEventThresholdMinutes = v),
            (nameof(InvariantOptions.UndeclaredOfflineGapMinutes), (o, v) => o.UndeclaredOfflineGapMinutes = v),
            (nameof(InvariantOptions.MaxUploadLagP99Minutes), (o, v) => o.MaxUploadLagP99Minutes = v),
            (nameof(InvariantOptions.MobileSummaryLagHours), (o, v) => o.MobileSummaryLagHours = v),
            (nameof(InvariantOptions.RecentWindowHours), (o, v) => o.RecentWindowHours = v),
            (nameof(InvariantOptions.MaxDailyActiveHours), (o, v) => o.MaxDailyActiveHours = v),
            (nameof(InvariantOptions.AwakeWindowHours), (o, v) => o.AwakeWindowHours = v),
            (nameof(InvariantOptions.AwakeWindowWarningRatio), (o, v) => o.AwakeWindowWarningRatio = v),
            (nameof(InvariantOptions.CoverageRedRatio), (o, v) => o.CoverageRedRatio = v),
            (nameof(InvariantOptions.CoverageYellowRatio), (o, v) => o.CoverageYellowRatio = v),
            (nameof(InvariantOptions.ClockSkewToleranceMinutes), (o, v) => o.ClockSkewToleranceMinutes = v),
            (nameof(InvariantOptions.TimelineGapThresholdMinutes), (o, v) => o.TimelineGapThresholdMinutes = v),
            (nameof(InvariantOptions.InstanceOverlapToleranceSeconds), (o, v) => o.InstanceOverlapToleranceSeconds = v),
            (nameof(InvariantOptions.Tolerance), (o, v) => o.Tolerance = v)
        };

        foreach (var (name, set) in setters)
        {
            var options = new InvariantOptions();
            set(options, badValue);

            Assert.False(options.Validate(out var error), $"{name} 设为 {badValue} 后本应校验失败");
            Assert.NotNull(error);
            Assert.Contains(name, error!);

            // 回退路径也必须生效：非法配置一律回退默认值并标注
            var (resolved, fallback, note) = InvariantOptions.Resolve(options);
            Assert.True(fallback, $"{name} 非法时应当回退默认值");
            Assert.NotNull(note);
            Assert.NotNull(resolved);
        }
    }

    /// <summary>
    /// 复审回归：阈值非有限时必须**回退默认值**，绝不能让 ∞/NaN 直接进入判定
    /// （否则 S7 所有空洞都会"不违规"，形成静默漏报）。
    /// </summary>
    [Fact]
    public void InvariantOptions_NonFiniteGapThreshold_FallsBackSoHolesStillDetected()
    {
        var options = new InvariantOptions { TimelineGapThresholdMinutes = double.PositiveInfinity };
        var (resolved, fallback, _) = InvariantOptions.Resolve(options);

        Assert.True(fallback);
        Assert.Equal(15.0, resolved.TimelineGapThresholdMinutes);

        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc), EndTime = new DateTime(2026, 7, 6, 10, 30, 0, DateTimeKind.Utc) },
            new() { DeviceId = "DEV-1", StartTime = new DateTime(2026, 7, 6, 11, 0, 0, DateTimeKind.Utc), EndTime = new DateTime(2026, 7, 6, 11, 30, 0, DateTimeKind.Utc) }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals, options);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
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
