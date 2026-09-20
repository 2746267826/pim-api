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
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(30)),
                (_baseUtc.AddHours(2), _baseUtc.AddHours(2).AddMinutes(30))
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
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (gapStart, gapStart.AddMinutes(30)),
                (gapEnd, gapEnd.AddMinutes(30))
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
        var lagSamples = new List<UploadLagSample>();
        for (int i = 0; i < 100; i++)
        {
            var eventTime = _baseUtc.AddMinutes(i);
            // 99% 的样本滞后 45 分钟 (> 30m)
            lagSamples.Add(new UploadLagSample { EventTime = eventTime, CreatedAt = eventTime.AddMinutes(45) });
        }

        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-LAGGY",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10))
            },
            UploadLagSamples = lagSamples
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Contains("上传滞后 p99", result.Detail);
    }

    #endregion

    [Fact]
    public void S6_GapMeasuredFromEventEndNotNextStart()
    {
        // 事件本身很长：10:00 起持续 50 分钟，下一条 11:00 才开始。
        // 真实空档是 10:50 -> 11:00（10 分钟，未超阈值），而不是"起点差"50 分钟。
        // 旧实现取相邻起点之差，会把这个 50 分钟事件自身时长误报成无声明空档。
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-LONG",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(50)),
                (_baseUtc.AddHours(1), _baseUtc.AddHours(1).AddMinutes(30))
            },
            Declarations = new List<OfflineDeclaration>()
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S6_OverlappingIntervals_DoNotCreatePhantomGap()
    {
        // 区间互相重叠（第二段整体落在第一段内部）时不得产生空档
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-OVL",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddHours(2)),
                (_baseUtc.AddMinutes(30), _baseUtc.AddMinutes(40))
            },
            Declarations = new List<OfflineDeclaration>()
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S6_SyntheticGapEvents_AreExcludedFromUploadLagP99()
    {
        // 100 个真实样本滞后仅 1 分钟；另有 2 个系统合成 gap 事件"滞后" 700 分钟。
        // 合成 gap 的 timestamp 是断档起点、created_at 是重启后补传时刻，其差值恒等于断档时长，
        // 不代表链路延迟 —— 排除后 p99 应回到 1 分钟左右并通过。
        var samples = new List<UploadLagSample>();
        for (int i = 0; i < 100; i++)
        {
            var t = _baseUtc.AddMinutes(i);
            samples.Add(new UploadLagSample { EventTime = t, CreatedAt = t.AddMinutes(1) });
        }
        samples.Add(new UploadLagSample { EventTime = _baseUtc, CreatedAt = _baseUtc.AddMinutes(700), IsSyntheticGap = true });
        samples.Add(new UploadLagSample { EventTime = _baseUtc.AddMinutes(1), CreatedAt = _baseUtc.AddMinutes(701), IsSyntheticGap = true });

        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-GAPSYN",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10))
            },
            UploadLagSamples = samples
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S6_RealSamplesStillCountedWhenSyntheticGapsPresent()
    {
        // 排除合成 gap 之后，真实样本的滞后仍必须被计入：不能因为过滤而放过真实积压。
        var samples = new List<UploadLagSample>
        {
            new() { EventTime = _baseUtc, CreatedAt = _baseUtc.AddMinutes(45) },
            new() { EventTime = _baseUtc.AddMinutes(1), CreatedAt = _baseUtc.AddMinutes(46) },
            new() { EventTime = _baseUtc.AddMinutes(2), CreatedAt = _baseUtc.AddMinutes(700), IsSyntheticGap = true }
        };

        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-MIX",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10))
            },
            UploadLagSamples = samples
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Contains("上传滞后 p99", result.Detail);
    }

    [Fact]
    public void S6_EarlyDeclaration_DoesNotSuppressLaterHoles()
    {
        // 复审回归（Critical）：一次"我下线了"的声明只能解释它附近的空档，
        // 绝不能解释此后所有空档。实测有一次 exit 声明 7 秒后设备就恢复了：
        // 若把声明当成"此后永久离线"，该设备之后的真实断档会被永久掩盖（假绿灯）。
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-EXIT",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10)),
                // 声明时刻附近的短空档（由该声明解释）
                (_baseUtc.AddMinutes(12), _baseUtc.AddMinutes(22)),
                // 数小时之后的 2 小时空档：与那次声明无关，必须仍被判为无声明空档
                (_baseUtc.AddHours(5), _baseUtc.AddHours(5).AddMinutes(30))
            },
            Declarations = new List<OfflineDeclaration>
            {
                new() { DeviceId = "DEV-EXIT", StartTime = _baseUtc.AddMinutes(11), EndTime = _baseUtc.AddMinutes(11), Reason = "exit" }
            }
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("无声明空档", result.Detail);
    }

    [Fact]
    public void S6_DeclarationAtHoleStart_CoversThatHole()
    {
        // 声明时刻正好落在空档内 -> 该空档被解释，不判违规
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-OK",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10)),
                (_baseUtc.AddHours(2), _baseUtc.AddHours(2).AddMinutes(30))
            },
            Declarations = new List<OfflineDeclaration>
            {
                new() { DeviceId = "DEV-OK", StartTime = _baseUtc.AddMinutes(30), EndTime = _baseUtc.AddMinutes(30), Reason = "shutdown" }
            }
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S6_IntervalDeclaration_DoesNotCoverHoleOutsideItsRange()
    {
        // 区间声明必须完整覆盖空档才有解释力：声明只盖了 1 小时，
        // 而空档有 2 小时 —— 没被盖住的部分仍然是"无解释空白"。
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-PARTIAL",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(10)),
                (_baseUtc.AddHours(2), _baseUtc.AddHours(2).AddMinutes(10))
            },
            Declarations = new List<OfflineDeclaration>
            {
                new() { DeviceId = "DEV-PARTIAL", StartTime = _baseUtc.AddMinutes(10), EndTime = _baseUtc.AddHours(1), Reason = "planned_offline" }
            }
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S6_OnlyHistoricalViolations_DowngradesToWarning()
    {
        // 空档全部发生在 24h 窗口之外 -> 只计数、降级为黄线（T4 分档）
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-OLD",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(30)),
                (_baseUtc.AddHours(2), _baseUtc.AddHours(2).AddMinutes(30))
            },
            Declarations = new List<OfflineDeclaration>()
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, referenceTimeUtc: now);

        Assert.True(result.IsWarning);
        Assert.False(result.IsFail);
        Assert.Equal(0, result.NewViolations);
        Assert.True(result.HistoricalViolations > 0);
    }

    [Fact]
    public void S6_RecentViolation_StaysRed()
    {
        // 空档发生在 24h 窗口内 -> 保持红线
        var trace = new DeviceActivityTrace
        {
            DeviceId = "DEV-NEW",
            EventIntervals = new List<(DateTime, DateTime)>
            {
                (_baseUtc, _baseUtc.AddMinutes(30)),
                (_baseUtc.AddHours(2), _baseUtc.AddHours(2).AddMinutes(30))
            },
            Declarations = new List<OfflineDeclaration>()
        };

        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, referenceTimeUtc: _baseUtc.AddHours(3));

        Assert.True(result.IsFail);
        Assert.True(result.NewViolations > 0);
        Assert.Equal(0, result.HistoricalViolations);
    }

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

    [Fact]
    public void S7_HoleCoveredByMergedGapChunks_Passes()
    {
        // 60 分钟空洞由两个首尾相接的 30 分钟 gap 分片（客户端按 1800s 切块上报）完整覆盖。
        // 旧实现只看"相邻区间是否相接"，从不检查 IsGap 覆盖，会把这个已标记的断档判成违规。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(30), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(30), EndTime = _baseUtc.AddHours(1), IsGap = true, EventType = "gap" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddHours(1), EndTime = _baseUtc.AddHours(1).AddMinutes(30), IsGap = true, EventType = "gap" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddHours(1).AddMinutes(30), EndTime = _baseUtc.AddHours(2), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S7_HoleOnlyPartiallyCoveredByGap_Fails()
    {
        // gap 只盖住后半段，前半段仍是"无解释空白" —— 判据原文要求**完整覆盖**，留白即未标记。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(10), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(40), EndTime = _baseUtc.AddHours(1), IsGap = true, EventType = "gap" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddHours(1), EndTime = _baseUtc.AddHours(1).AddMinutes(10), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S7_GapOfAnotherDeviceDoesNotCoverHole()
    {
        // 另一台设备的 gap 不能替本设备标记断档
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(10), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddHours(1), EndTime = _baseUtc.AddHours(1).AddMinutes(10), EventType = "window" },
            new() { DeviceId = "DEV-2", StartTime = _baseUtc.AddMinutes(10), EndTime = _baseUtc.AddHours(1), IsGap = true, EventType = "gap" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S7_HoleLongerThanGapCoverage_BySubSecond_Fails()
    {
        // 复审回归：容差必须是毫秒级。gap 覆盖段比空洞短 500 毫秒时，
        // 仍然属于"没有完整覆盖" —— 秒级容差会把这种留白放行。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(10), EventType = "window" },
            new()
            {
                DeviceId = "DEV-1",
                StartTime = _baseUtc.AddMinutes(10),
                // 覆盖段比真实空洞短 0.5 秒
                EndTime = _baseUtc.AddMinutes(40).AddMilliseconds(-500),
                IsGap = true,
                EventType = "gap"
            },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(40), EndTime = _baseUtc.AddMinutes(50), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S7_HoleCoveredWithinMillisecondTolerance_Passes()
    {
        // 覆盖段与空洞边界只差 1 毫秒（入库精度）-> 视为完整覆盖
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(10), EventType = "window" },
            new()
            {
                DeviceId = "DEV-1",
                StartTime = _baseUtc.AddMinutes(10),
                EndTime = _baseUtc.AddMinutes(40).AddMilliseconds(-1),
                IsGap = true,
                EventType = "gap"
            },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(40), EndTime = _baseUtc.AddMinutes(50), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S7_ReversedInterval_DoesNotCreatePhantomHole()
    {
        // 反向区间（End < Start）本身无意义，但判据是公开 API，必须稳健：
        // 若直接使用其 End，会把后续区间误判成"不相接"从而凭空产生空洞。
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(50), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(50), EndTime = _baseUtc.AddMinutes(30), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(50), EndTime = _baseUtc.AddMinutes(60), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S7_OnlyHistoricalHoles_DowngradesToWarning()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var intervals = new List<TimelineInterval>
        {
            new() { DeviceId = "DEV-1", StartTime = _baseUtc, EndTime = _baseUtc.AddMinutes(30), EventType = "window" },
            new() { DeviceId = "DEV-1", StartTime = _baseUtc.AddMinutes(50), EndTime = _baseUtc.AddMinutes(80), EventType = "window" }
        };

        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals, referenceTimeUtc: now);

        Assert.True(result.IsWarning);
        Assert.Equal(0, result.NewViolations);
        Assert.True(result.HistoricalViolations > 0);
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

    /// <summary>
    /// 口径说明（有意为之，非缺陷）：S9 判的是"**缺口有没有产生信号**"，而不是"覆盖率本身好不好"。
    /// 覆盖率 60% 但设备如实上报 Error/Critical 时，系统并没有静默掩盖故障 → 通过。
    /// 只有"低覆盖率却被报成 Normal/Healthy/OK"才是这条尺子要抓的静默失败（见下一条用例）。
    ///
    /// 已知局限（登记为观察项）：`ReportedStatus` 长年固定为 Error 时，这条尺子无法区分
    /// "如实报告"与"坏了但状态字段不再更新"。要覆盖这一点需要引入状态上报的时效性判定，
    /// 属于口径变更，不在本工单范围内。
    /// </summary>
    [Fact]
    public void S9_Coverage60Percent_ReportedError_Passes()
    {
        // 覆盖率 60%，但设备如实报告 Error —— 没有静默掩盖，判通过
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
