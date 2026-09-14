using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 尺子组一 · 数据自洽（S1–S5）单元测试
/// 验收标准覆盖:
/// 1. 5 条判据均有独立单元测试，每条至少包含 1 pass + 1 fail
/// 2. S2 区分"打游戏 5 小时（有输入，通过）"和"下班锁屏挂着 5 小时（无输入无媒体，疑似未收尾）"
/// 3. S4 区分存量重复（黄）与新增重复（红）
/// 4. S5 容差可调，超前 10 分钟必红，超前 1 分钟通过
/// 5. S3 边界测试：单日 25h 活跃 → 失败；单日 15h → 通过
/// </summary>
public class DataReliabilityGroupOneTests
{
    private readonly DateTime _baseTime = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    #region S1: 同类型事件不重叠 (INV-P16)

    [Fact]
    public void S1_NonOverlappingEvents_Passes()
    {
        var events = new List<EventTimeSpan>
        {
            new() { DeviceId = "PC-1", EventType = "AppFocus", StartTime = _baseTime, EndTime = _baseTime.AddMinutes(30) },
            new() { DeviceId = "PC-1", EventType = "AppFocus", StartTime = _baseTime.AddMinutes(30), EndTime = _baseTime.AddMinutes(60) },
            // 不同类型可以重叠（如后台服务和前台应用）
            new() { DeviceId = "PC-1", EventType = "BackgroundSync", StartTime = _baseTime.AddMinutes(15), EndTime = _baseTime.AddMinutes(45) }
        };

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: _baseTime.AddHours(1));

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S1_OverlappingSameTypeEvents_Fails()
    {
        var events = new List<EventTimeSpan>
        {
            new() { DeviceId = "PC-1", EventType = "AppFocus", StartTime = _baseTime, EndTime = _baseTime.AddMinutes(30) },
            new() { DeviceId = "PC-1", EventType = "AppFocus", StartTime = _baseTime.AddMinutes(20), EndTime = _baseTime.AddMinutes(50) }
        };

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(events, referenceTimeUtc: _baseTime.AddHours(1));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Contains("overlaps with", result.Detail);
    }

    #endregion

    #region S2: 超长事件活动证据三态判定 (INV-P17)

    [Fact]
    public void S2_GamingFiveHoursWithInput_Passes()
    {
        // 打游戏 5 小时：时长 300 分钟，按键 3000 次，点击 1500 次，输入密度 = 15 次/分 (>= 1.0)
        var events = new List<LongEventCandidate>
        {
            new()
            {
                DeviceId = "PC-Gamer",
                EventType = "WindowActivity",
                AppName = "EldenRing.exe",
                StartTime = _baseTime,
                EndTime = _baseTime.AddHours(5),
                Keystrokes = 3000,
                MouseClicks = 1500,
                IsMediaActive = false,
                IsGapOrOffline = false
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: _baseTime.AddHours(6));

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S2_WatchingVideoThreeHoursWithMedia_Passes()
    {
        // 看视频 3 小时：输入为 0，但有媒体活跃
        var events = new List<LongEventCandidate>
        {
            new()
            {
                DeviceId = "PC-Media",
                EventType = "WindowActivity",
                AppName = "Bilibili",
                StartTime = _baseTime,
                EndTime = _baseTime.AddHours(3),
                Keystrokes = 0,
                MouseClicks = 2,
                IsMediaActive = true,
                IsAudible = true
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: _baseTime.AddHours(4));

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S2_SleepOrShutdownEightHours_Passes()
    {
        // 休眠 8 小时：明确的空档
        var events = new List<LongEventCandidate>
        {
            new()
            {
                DeviceId = "PC-Sleep",
                EventType = "gap_sleep",
                StartTime = _baseTime,
                EndTime = _baseTime.AddHours(8),
                IsGapOrOffline = true
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: _baseTime.AddHours(9));

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S2_LockScreenHangingFiveHours_FailsWithSuspectedUnclosed()
    {
        // 下班锁屏挂着 5 小时：无输入，无媒体活动，非空档声明
        var events = new List<LongEventCandidate>
        {
            new()
            {
                DeviceId = "PC-Office",
                EventType = "WindowActivity",
                AppName = "Code.exe",
                StartTime = _baseTime,
                EndTime = _baseTime.AddHours(5),
                Keystrokes = 0,
                MouseClicks = 0,
                IsMediaActive = false,
                IsAudible = false,
                IsGapOrOffline = false
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events, referenceTimeUtc: _baseTime.AddHours(6));

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("疑似未收尾", result.Detail);
    }

    [Fact]
    public void S2_ShortEventUnderThreshold_PassesEvenWithoutInput()
    {
        // 20 分钟事件 (<= 30m 阈值)，即使无输入也通过
        var events = new List<LongEventCandidate>
        {
            new()
            {
                DeviceId = "PC-1",
                EventType = "WindowActivity",
                StartTime = _baseTime,
                EndTime = _baseTime.AddMinutes(20),
                Keystrokes = 0,
                MouseClicks = 0
            }
        };

        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(events);

        Assert.True(result.Pass);
    }

    #endregion

    #region S3: 单日时长有界 (INV-P18)

    [Fact]
    public void S3_DailyDuration15Hours_PassesWithWarning()
    {
        // 15 小时 = 54,000 秒，在 24h 硬上限之内，但超过 14.4h 警告线
        var durations = new List<DailyActiveDuration>
        {
            new() { Date = "2026-03-10", DeviceId = "PC-1", ActiveDurationSeconds = 15 * 3600 }
        };

        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(durations);

        Assert.True(result.Pass);
        Assert.Contains("警告线", result.Detail);
    }

    [Fact]
    public void S3_DailyDuration25Hours_FailsHardCap()
    {
        // 25 小时活跃：物理不可能，硬上限失败
        var durations = new List<DailyActiveDuration>
        {
            new() { Date = "2026-03-10", DeviceId = "PC-1", ActiveDurationSeconds = 25 * 3600 }
        };

        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(durations);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("超过硬上限", result.Detail);
    }

    [Fact]
    public void S3_DailyDurationNormal8Hours_PassesWithoutWarning()
    {
        var durations = new List<DailyActiveDuration>
        {
            new() { Date = "2026-03-10", DeviceId = "PC-1", ActiveDurationSeconds = 8 * 3600 }
        };

        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(durations);

        Assert.True(result.Pass);
        Assert.False(result.IsWarning);
        Assert.DoesNotContain("超过清醒窗口警告线", result.Detail);
    }

    [Fact]
    public void S3_AggregateDailyActiveDurations_MergesOverlappingIntervalsAndRemovesOverlap()
    {
        // 两个重叠 1 小时的 2 小时活动事件：
        // 事件 1: 10:00 ~ 12:00 (7200s)
        // 事件 2: 11:00 ~ 13:00 (7200s)
        // 合并后区间应为 10:00 ~ 13:00 (3h = 10800s)，去重重叠时长 1h = 3600s
        var start1 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        var start2 = new DateTime(2026, 9, 9, 11, 0, 0, DateTimeKind.Utc);

        var events = new List<RawActivityEvent>
        {
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "window",
                Timestamp = start1,
                DurationSeconds = 7200,
                InputDensityPerMinute = 10
            },
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "web-page",
                Timestamp = start2,
                DurationSeconds = 7200,
                InputDensityPerMinute = 5
            }
        };

        var dailyDurations = DataReliabilityInvariants.AggregateDailyActiveDurations(events);

        Assert.Single(dailyDurations);
        var daily = dailyDurations[0];
        Assert.Equal("2026-09-09", daily.Date);
        Assert.Equal(10800, daily.ActiveDurationSeconds);
        Assert.Equal(10800, daily.MergedActiveSeconds);
        Assert.Equal(3600, daily.OverlapRemovedSeconds);
        Assert.Equal(0, daily.IdleSeconds);
        Assert.Equal(0, daily.GapSeconds);
        Assert.Equal(0, daily.SuspectedUnclosedSeconds);
    }

    [Fact]
    public void S3_AggregateDailyActiveDurations_FiltersThreeStates_ExcludesGapIdleAndUnclosed()
    {
        var baseTime = new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc);

        var events = new List<RawActivityEvent>
        {
            // 1. 活跃操作事件 (2h)
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "window",
                Timestamp = baseTime,
                DurationSeconds = 7200,
                InputDensityPerMinute = 20
            },
            // 2. 媒体/音频活跃事件 (1h, 虽然无操作输入但有媒体活动，算活跃)
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "window",
                Timestamp = baseTime.AddHours(2),
                DurationSeconds = 3600,
                InputDensityPerMinute = 0,
                IsMediaActive = true
            },
            // 3. Gap 空档事件 (9h，必须排除)
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "gap",
                Timestamp = baseTime.AddHours(3),
                DurationSeconds = 32400
            },
            // 4. Idle 闲置事件 (1h，必须排除)
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "window",
                Timestamp = baseTime.AddHours(12),
                DurationSeconds = 3600,
                IsIdle = true
            },
            // 5. 疑似未收尾事件 (45m > 30m，无操作且无媒体，非明确空档，必须排除)
            new()
            {
                DeviceId = "PC-1",
                BusinessDate = "2026-09-09",
                EventType = "window",
                Timestamp = baseTime.AddHours(13),
                DurationSeconds = 2700,
                InputDensityPerMinute = 0,
                IsMediaActive = false,
                Audible = false
            }
        };

        var dailyDurations = DataReliabilityInvariants.AggregateDailyActiveDurations(events);

        Assert.Single(dailyDurations);
        var daily = dailyDurations[0];
        // 活跃仅包含 1 (7200s) 和 2 (3600s) = 10800s (3h)
        Assert.Equal(10800, daily.ActiveDurationSeconds);
        Assert.Equal(10800, daily.MergedActiveSeconds);
        Assert.Equal(32400, daily.GapSeconds);
        Assert.Equal(3600, daily.IdleSeconds);
        Assert.Equal(2700, daily.SuspectedUnclosedSeconds);

        // 运行 S3 检查，应正常判绿并包含各态明细
        var check = DataReliabilityInvariants.CheckS3_DailyDurationBounded(dailyDurations);
        Assert.True(check.Pass);
        Assert.Contains("合并活跃=3.00h", check.Detail);
        Assert.Contains("Gap=9.00h", check.Detail);
        Assert.Contains("Idle=1.00h", check.Detail);
        Assert.Contains("剔除疑似未收尾=0.75h", check.Detail);
    }

    #endregion

    #region S4: 业务键唯一 (INV-C18)

    [Fact]
    public void S4_UniqueKeys_Passes()
    {
        var records = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForLocation("DEV-1", _baseTime, 31.2304, 121.4737),
            BusinessRecordKey.ForLocation("DEV-1", _baseTime.AddMinutes(5), 31.2305, 121.4738),
            BusinessRecordKey.ForMobile("DEV-1", "com.wechat", _baseTime, "foreground"),
            BusinessRecordKey.ForPc("DEV-1", _baseTime, 60, "active", "chrome.exe", "Chrome", "inst-1")
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S4_DistinguishesStockAndNewDuplicates()
    {
        var now = _baseTime;
        var oldTime = now.AddHours(-48); // 48 小时前 (存量)
        var newTime = now.AddMinutes(-30); // 30 分钟前 (新增)

        var records = new List<BusinessRecordKey>
        {
            // 存量重复 (2 条相同)
            BusinessRecordKey.ForPc("DEV-1", oldTime, 60, "active", "chrome.exe", null, null),
            BusinessRecordKey.ForPc("DEV-1", oldTime, 60, "active", "chrome.exe", null, null),

            // 新增重复 (2 条相同)
            BusinessRecordKey.ForLocation("DEV-1", newTime, 30.0, 120.0),
            BusinessRecordKey.ForLocation("DEV-1", newTime, 30.0, 120.0)
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(records, referenceTimeUtc: now);

        Assert.False(result.Pass);
        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.HistoricalViolations); // 存量重复
        Assert.Equal(1, result.NewViolations);        // 新增重复
    }

    #endregion

    #region S5: 时钟可信 (INV-P19)

    [Fact]
    public void S5_ClockSkewWithinTolerance_Passes()
    {
        // 客户端超前 1 分钟 (在 5 分钟容差内)
        var items = new List<ClockEventItem>
        {
            new()
            {
                DeviceId = "DEV-1",
                EventTime = _baseTime.AddMinutes(1),
                ServerReceivedTime = _baseTime
            }
        };

        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(items);

        Assert.True(result.Pass);
    }

    [Fact]
    public void S5_ClockSkewExceedingTolerance_Fails()
    {
        // 客户端超前 10 分钟 (> 5 分钟容差)
        var items = new List<ClockEventItem>
        {
            new()
            {
                DeviceId = "DEV-1",
                EventId = "EVT-FUTURE",
                EventTime = _baseTime.AddMinutes(10),
                ServerReceivedTime = _baseTime
            }
        };

        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(items);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("超前", result.Detail);
    }

    [Fact]
    public void S5_AdjustableTolerance_ChangesOutcome()
    {
        // 客户端超前 7 分钟：在默认 5 分钟下失败；在定制 10 分钟容差下通过
        var items = new List<ClockEventItem>
        {
            new()
            {
                DeviceId = "DEV-1",
                EventTime = _baseTime.AddMinutes(7),
                ServerReceivedTime = _baseTime
            }
        };

        var defaultResult = DataReliabilityInvariants.CheckS5_ClockTrustworthy(items);
        Assert.False(defaultResult.Pass);

        var customOptions = new InvariantOptions { ClockSkewToleranceMinutes = 10.0 };
        var customResult = DataReliabilityInvariants.CheckS5_ClockTrustworthy(items, customOptions);
        Assert.True(customResult.Pass);
    }

    #endregion
}
