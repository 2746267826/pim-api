using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 尺子组二 · 覆盖完整（S6–S9）单元测试
/// 验收标准覆盖:
/// 1. 4 条判据均有独立单元测试（每条含至少 1 pass + 1 fail）
/// 2. S6 测试：无声明空档 2 小时 → 失败；关机声明 + 空档 8 小时 → 通过
/// 3. S7 测试：20 分钟未标记空洞 → 失败；补 gap 事件后 → 通过
/// 4. S8 测试：跨 04:00 界线的事件判定准确；本地 02:00 事件三层归属日一致
/// 5. S9 测试：覆盖率 60% 时报告必须为红，不得为正常
/// </summary>
public class DataReliabilityGroupTwoTests
{
    private readonly DateTime _baseUtc = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    #region S6: 设备必须自己声明下线 (INV-P20)

    [Fact]
    public void S6_UndeclaredTwoHoursGap_Fails()
    {
        // 2 小时无事件且无声明
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-PC1",
            EventTimes = new List<DateTime>
            {
                _baseUtc,
                _baseUtc.AddHours(2)
            },
            Declarations = new List<OfflineDeclaration>() // 无下线声明
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("无声明空档", result.Detail);
    }

    [Fact]
    public void S6_DeclaredEightHoursGap_Passes()
    {
        // 关机声明 + 空档 8 小时
        var gapStart = _baseUtc;
        var gapEnd = _baseUtc.AddHours(8);

        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-PC1",
            EventTimes = new List<DateTime>
            {
                gapStart,
                gapEnd
            },
            Declarations = new List<OfflineDeclaration>
            {
                new()
                {
                    DeviceId = "DEV-PC1",
                    StartTime = gapStart,
                    EndTime = gapEnd,
                    Reason = "shutdown"
                }
            }
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S6_UploadLagP99ExceedsThreshold_Fails()
    {
        // 采集事件时间与入库 created_at 滞后超过 30 分钟 (p99)
        var lagSamples = new List<(DateTime EventTime, DateTime CreatedAt)>();
        for (int i = 0; i < 100; i++)
        {
            var eventTime = _baseUtc.AddMinutes(i);
            // 99% 的样本滞后 45 分钟 (> 30m)
            var createdAt = eventTime.AddMinutes(45);
            lagSamples.Add((eventTime, createdAt));
        }

        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-LAGGY",
            EventTimes = new List<DateTime> { _baseUtc, _baseUtc.AddMinutes(10) },
            UploadLagSamples = lagSamples
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Contains("上传滞后 p99", result.Detail);
    }

    #endregion

    #region S7: 断档必须在时间轴上被标记 (INV-P21)

    [Fact]
    public void S7_UnmarkedTwentyMinutesHole_Fails()
    {
        // 20 分钟未标记空洞 (> 15m 阈值)
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(30), IsGap = false },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(50), EndTime = _baseUtc.AddMinutes(80), IsGap = false }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("未标记空洞", result.Detail);
    }

    [Fact]
    public void S7_FilledWithGapInterval_Passes()
    {
        // 中间 20 分钟被标记为 gap 事件
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(30), IsGap = false },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(30), EndTime = _baseUtc.AddMinutes(50), IsGap = true, EventType = "gap" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(50), EndTime = _baseUtc.AddMinutes(80), IsGap = false }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S7_TenMinutesHoleUnderThreshold_Passes()
    {
        // 10 分钟空洞 (<= 15m 阈值)，不判违规
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(30), IsGap = false },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(40), EndTime = _baseUtc.AddMinutes(60), IsGap = false }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.True(result.Pass);
    }

    #endregion

    #region S8: 日界一致（三层） (INV-C19)

    [Fact]
    public void S8_Local0200_ConsistentThreeLayers_Passes()
    {
        // 2026-03-10 02:00:00 (Asia/Shanghai) -> UTC 2026-03-09 18:00:00
        // 在 04:00 起算的规则下，02:00 属于前一天的夜间业务日 (2026-03-09)
        var eventUtc = new DateTime(2026, 3, 9, 18, 0, 0, DateTimeKind.Utc);
        var expectedDay = DataReliabilityInvariants.ComputeBusinessDayString(eventUtc);
        Assert.Equal("2026-03-09", expectedDay);

        var samples = new List<DayBoundarySample>
        {
            new()
            {
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-03-09",
                QueryWindowDate = "2026-03-09",
                PageDisplayDate = "2026-03-09"
            }
        };

        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S8_Local0200_InconsistentLayers_Fails()
    {
        // 2026-03-10 02:00:00 Shanghai -> 业务日应为 2026-03-09
        // 若某个接口或页面误切到了 2026-03-10，则三层不一致报错
        var eventUtc = new DateTime(2026, 3, 9, 18, 0, 0, DateTimeKind.Utc);
        var samples = new List<DayBoundarySample>
        {
            new()
            {
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-03-09",
                QueryWindowDate = "2026-03-10", // 错误：使用自然日 10 号
                PageDisplayDate = "2026-03-09"
            }
        };

        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("三层不一致", result.Detail);
    }

    [Fact]
    public void S8_Local0500_BelongsToCurrentDay_Passes()
    {
        // 2026-03-10 05:00:00 Shanghai -> UTC 2026-03-09 21:00:00
        // 过了 04:00，属于 2026-03-10 业务日
        var eventUtc = new DateTime(2026, 3, 9, 21, 0, 0, DateTimeKind.Utc);
        var expectedDay = DataReliabilityInvariants.ComputeBusinessDayString(eventUtc);
        Assert.Equal("2026-03-10", expectedDay);

        var samples = new List<DayBoundarySample>
        {
            new()
            {
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-03-10",
                QueryWindowDate = "2026-03-10",
                PageDisplayDate = "2026-03-10"
            }
        };

        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples);

        Assert.True(result.Pass);
        Assert.Equal("DataField,QueryWindow,PageDisplay", result.CoveredLayers);
    }

    [Fact]
    public void S8_OnlyDataFieldProvided_PassesWithDataFieldCoveredLayer()
    {
        // 接口窗口与页面展示层为空时，判据如实输出覆盖层级为数据字段层
        var eventUtc = new DateTime(2026, 3, 9, 21, 0, 0, DateTimeKind.Utc);
        var samples = new List<DayBoundarySample>
        {
            new()
            {
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-03-10",
                QueryWindowDate = null,
                PageDisplayDate = null
            }
        };

        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples);

        Assert.True(result.Pass);
        Assert.Equal("DataField", result.CoveredLayers);
        Assert.Contains("覆盖层级: 数据字段层 ✅; 接口窗口层、展示层: 本判据未覆盖 (需接口契约测试)", result.Detail);
    }

    [Fact]
    public void S8_OnlyDataFieldProvided_Deviating_Fails()
    {
        // 偏离业务日：UTC 21:00 (上海 05:00 属于 10 号)，字段却填了 09 号
        var eventUtc = new DateTime(2026, 3, 9, 21, 0, 0, DateTimeKind.Utc);
        var samples = new List<DayBoundarySample>
        {
            new()
            {
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-03-09", // 错误
                QueryWindowDate = null,
                PageDisplayDate = null
            }
        };

        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Equal("DataField", result.CoveredLayers);
    }

    #endregion

    #region S9: 有缺口必有信号 (INV-C20)

    [Fact]
    public void S9_Coverage60Percent_ReportedNormal_MustFail()
    {
        // 覆盖率 60% (< 95% 红线)，报告状态却为 Normal
        var report = new CoverageSignalReport
        {
            DeviceId = "DEV-GAP",
            OnlineDurationSeconds = 10000,
            ValidDataDurationSeconds = 6000, // 60%
            ReportedStatus = "Normal"
        };

        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.False(result.Pass);
        Assert.Contains("必须为红/错误", result.Detail);
    }

    [Fact]
    public void S9_Coverage60Percent_ReportedError_Passes()
    {
        // 覆盖率 60%，如实报告 Error / Critical
        var report = new CoverageSignalReport
        {
            DeviceId = "DEV-GAP",
            OnlineDurationSeconds = 10000,
            ValidDataDurationSeconds = 6000,
            ReportedStatus = "Error"
        };

        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.True(result.Pass);
    }

    [Fact]
    public void S9_Coverage97Percent_ReportedNormal_FailsYellowLine()
    {
        // 覆盖率 97% (< 99% 黄线)，报告状态为 Normal
        var report = new CoverageSignalReport
        {
            DeviceId = "DEV-MED",
            OnlineDurationSeconds = 10000,
            ValidDataDurationSeconds = 9700, // 97%
            ReportedStatus = "Normal"
        };

        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.False(result.Pass);
        Assert.Contains("必须报警告/黄线", result.Detail);
    }

    [Fact]
    public void S9_Coverage99Point5Percent_ReportedNormal_Passes()
    {
        // 覆盖率 99.5% (>= 99%)，报告 Normal 通过
        var report = new CoverageSignalReport
        {
            DeviceId = "DEV-GOOD",
            OnlineDurationSeconds = 10000,
            ValidDataDurationSeconds = 9950,
            ReportedStatus = "Normal"
        };

        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.True(result.Pass);
    }

    [Fact]
    public void S9_DataInsufficientForDenominator_ReturnsUnknownWithBreakdown()
    {
        var report = new CoverageSignalReport
        {
            DeviceId = "DESKTOP-ARJ75IN",
            OnlineDurationSeconds = 86400,
            ValidDataDurationSeconds = 41258.85,
            ReportedStatus = "Normal",
            IsDataInsufficientForDenominator = true,
            DenominatorBasisNote = "pc_tracker_health 仅存单条当前心跳 (uptime=2400s)，缺失历史心跳时序与离线声明日志",
            GapBreakdown = new List<string>
            {
                "[2026-09-13 12:56 ~ 2026-09-13 13:45 缺口 0.76h]",
                "[2026-09-13 14:42 ~ 2026-09-14 04:46 缺口 14.06h]"
            }
        };

        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.False(result.Pass);
        Assert.Equal(InvariantStatus.Unknown, result.Status);
        Assert.Contains("口径近似 / 数据源不足", result.Detail);
        Assert.Contains("47.8%", result.Detail);
        Assert.Contains("缺口 14.06h", result.Detail);
    }

    #endregion
}
