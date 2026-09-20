using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 尺子组三 · 链路健康（S10–S13）单元测试
/// 验收标准覆盖:
/// 1. 4 条判据均有独立单元测试（每条含至少 1 pass + 1 fail）
/// 2. S10 测试：有待补数据但补齐任务产出 0 行 → 红；产出 > 0 → 通过
/// 3. S11 测试：只有 rejected 无 failed 但标为 failed 的批次 → 失败；修复语义后 → 通过
/// 4. S12 测试：有源数据且派生表为空 → 失败；派生表非空或显式标记在线计算 → 通过
/// 5. S13 测试：同设备同小时出现两条相位互斥采集流 → 红；单流 → 通过
/// </summary>
public class DataReliabilityGroupThreeTests
{
    private readonly DateTime _baseUtc = new(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);

    #region S10: 后台任务必须有产出 (INV-C21)

    [Fact]
    public void S10_AvailableDataWithZeroOutput_Fails()
    {
        // 有待处理数据 150 条，但任务产出 0 行 (静默空转)
        var runs = new List<BackgroundTaskRun>
        {
            new()
            {
                TaskName = "ClassifySnapshotBackfill",
                ExecutedAt = _baseUtc,
                AvailableDataCount = 150,
                ProcessedCount = 150,
                OutputCount = 0
            }
        };

        var result = DataReliabilityInvariants.CheckS10_TaskHasOutput(runs);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("静默空转", result.Detail);
    }

    [Fact]
    public void S10_AvailableDataWithOutput_Passes()
    {
        // 有待处理数据 150 条，产出 150 行
        var runs = new List<BackgroundTaskRun>
        {
            new()
            {
                TaskName = "ClassifySnapshotBackfill",
                ExecutedAt = _baseUtc,
                AvailableDataCount = 150,
                ProcessedCount = 150,
                OutputCount = 150
            }
        };

        var result = DataReliabilityInvariants.CheckS10_TaskHasOutput(runs);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S10_NoAvailableDataWithZeroOutput_Passes()
    {
        // 队列无数据，产出 0 行正常
        var runs = new List<BackgroundTaskRun>
        {
            new()
            {
                TaskName = "HourlyAggregation",
                ExecutedAt = _baseUtc,
                AvailableDataCount = 0,
                ProcessedCount = 0,
                OutputCount = 0
            }
        };

        var result = DataReliabilityInvariants.CheckS10_TaskHasOutput(runs);

        Assert.True(result.Pass);
    }

    #endregion

    #region S11: 状态语义自洽 (INV-M21)

    [Fact]
    public void S11_OnlyRejectedMarkedAsFailed_Fails()
    {
        // 仅有业务去重拒绝 (rejected_count = 10, failed_count = 0)，状态却被标为 failed
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-001",
                FailedCount = 0,
                RejectedCount = 10,
                TotalCount = 100,
                Status = "failed",
                WindowStartUtc = _baseUtc.AddHours(-1)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("应为 completed 或 rejected 语义", result.Detail);
    }

    [Fact]
    public void S11_OnlyRejectedMarkedAsCompletedOrCompletedWithRejected_Passes()
    {
        // 修复语义：使用 completed 或 completed-with-rejected
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-001",
                FailedCount = 0,
                RejectedCount = 10,
                TotalCount = 100,
                Status = "completed",
                WindowStartUtc = _baseUtc.AddHours(-1)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S11_HasFailedMarkedAsCompleted_Fails()
    {
        // 有系统级失败 (failed_count = 5)，却谎报为 completed
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-002",
                FailedCount = 5,
                RejectedCount = 0,
                TotalCount = 100,
                Status = "completed",
                WindowStartUtc = _baseUtc.AddHours(-1)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S11_StockOnlyViolation_TurnsYellowWithHistoricalCount()
    {
        // T4: 窗口起点在 24h 之外 → 存量欠账，只计数不报警（黄线），新增必须为 0
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-OLD",
                FailedCount = 0,
                RejectedCount = 10,
                TotalCount = 100,
                Status = "failed",
                WindowStartUtc = _baseUtc.AddHours(-25)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.False(result.Pass);
        Assert.True(result.IsWarning);
        Assert.Equal(1, result.TotalViolations);
        Assert.Equal(0, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
        Assert.Contains("存量 1", result.Detail);
    }

    [Fact]
    public void S11_NewViolation_TurnsRedWithNewCount()
    {
        // T4: 窗口起点在 24h 之内 → 新增违规，保持红尺
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-NEW",
                FailedCount = 0,
                RejectedCount = 10,
                TotalCount = 100,
                Status = "failed",
                WindowStartUtc = _baseUtc.AddHours(-1)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.False(result.Pass);
        Assert.True(result.IsFail);
        Assert.Equal(1, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Equal(0, result.HistoricalViolations);
        Assert.Contains("新增 1", result.Detail);
    }

    [Fact]
    public void S11_MixedViolations_BucketedByWindowStart()
    {
        // 历史空转批次（存量）+ 今天的 failed 误标（新增）：按窗口起点分档、互不混淆
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-EMPTY-RUN",
                AcceptedCount = 0,
                FailedCount = 0,
                RejectedCount = 0,
                SkippedCount = 0,
                TotalCount = 0,
                Status = "completed",
                WindowStartUtc = _baseUtc.AddDays(-30)
            },
            new()
            {
                BatchId = "BATCH-NEW-FAILED",
                FailedCount = 0,
                RejectedCount = 10,
                TotalCount = 100,
                Status = "failed",
                WindowStartUtc = _baseUtc.AddHours(-2)
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        Assert.False(result.Pass);
        Assert.True(result.IsFail);
        Assert.Equal(2, result.TotalViolations);
        Assert.Equal(1, result.NewViolations);
        Assert.Equal(1, result.HistoricalViolations);
    }

    [Fact]
    public void S11_ViolationOccurredAt_UsesWindowStart()
    {
        // 下钻导出（#261）的违规业务时间必须取窗口起点，而不是 MinValue 占位
        var windowStart = _baseUtc.AddDays(-30);
        var batches = new List<BatchSyncStatusRecord>
        {
            new()
            {
                BatchId = "BATCH-EMPTY-RUN",
                TotalCount = 0,
                Status = "completed",
                WindowStartUtc = windowStart
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches, referenceTimeUtc: _baseUtc);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(windowStart, violation.OccurredAtUtc);
    }

    #endregion

    #region S12: 派生表在使用 (INV-M22)

    [Fact]
    public void S12_SourceDataPresentWithEmptyDerivedTable_Fails()
    {
        // 最近24h有 5000 条源数据，但派生表行数为 0 且未声明在线计算
        var tables = new List<DerivedTableStatus>
        {
            new()
            {
                TableName = "app_daily_summaries",
                SourceDataCountLast24H = 5000,
                DerivedRowCount = 0,
                IsExplicitOnlineCalculation = false
            }
        };

        var result = DataReliabilityInvariants.CheckS12_DerivedTableActive(tables);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("派生表行数却为 0", result.Detail);
    }

    [Fact]
    public void S12_DerivedTableHasRows_Passes()
    {
        // 派生表正常产出 120 行
        var tables = new List<DerivedTableStatus>
        {
            new()
            {
                TableName = "app_daily_summaries",
                SourceDataCountLast24H = 5000,
                DerivedRowCount = 120,
                IsExplicitOnlineCalculation = false
            }
        };

        var result = DataReliabilityInvariants.CheckS12_DerivedTableActive(tables);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S12_ExplicitOnlineCalculation_PassesEvenIfEmpty()
    {
        // 明确声明为在线计算架构（二选一中选择在线计算），通过
        var tables = new List<DerivedTableStatus>
        {
            new()
            {
                TableName = "timeline_blocks_view",
                SourceDataCountLast24H = 5000,
                DerivedRowCount = 0,
                IsExplicitOnlineCalculation = true,
                DocumentationNote = "采用轻量在线聚合，不使用落盘物化表"
            }
        };

        var result = DataReliabilityInvariants.CheckS12_DerivedTableActive(tables);

        Assert.True(result.Pass);
    }

    #endregion

    #region S13: 实例唯一 (INV-P22)

    [Fact]
    public void S13_MultipleInstancesSameDeviceSameHour_Overlapping_Fails()
    {
        // 同一设备同小时两个实例**同时**在采集：区间真实重叠（各采集 10 分钟，重叠 5 分钟）
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(5), DurationSeconds = 600, InstanceId = "inst-B", SessionId = 1 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("并发重叠", result.Detail);
    }

    [Fact]
    public void S13_MultipleInstancesSameDeviceSameHour_HandoverOnly_Passes()
    {
        // 同一小时内先后出现两个 instanceId，但**没有时间重叠** —— 旧实例退出、新实例接管，
        // 这是客户端升级/重启的正常交接（实测 09-18 19:17 交接误差 0.001s），不得判红。
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(10), DurationSeconds = 600, InstanceId = "inst-B", SessionId = 1 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S13_HandoverWithSubSecondBoundaryTouch_Passes()
    {
        // 交接边界亚秒相触（旧实例结束 19:17:22.038、新实例 19:17:22.039）：
        // 重叠为 0，属于正常交接；容差内的相触同样不得判红。
        var boundary = _baseUtc.AddMinutes(17).AddSeconds(22).AddMilliseconds(38);
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = boundary.AddSeconds(-10), DurationSeconds = 10, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = boundary, DurationSeconds = 20, InstanceId = "inst-B", SessionId = 2 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S13_OverlapWithinTolerance_Passes()
    {
        // 重叠 10 毫秒，小于默认容差 0.05s（毫秒级边界相触），不得判红
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600.010, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddSeconds(600), DurationSeconds = 60, InstanceId = "inst-B", SessionId = 2 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S13_OverlapExceedingTolerance_Fails()
    {
        // 重叠 200 毫秒 > 默认容差 0.05s：属于真实并发采集
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600.2, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddSeconds(600), DurationSeconds = 60, InstanceId = "inst-B", SessionId = 2 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S13_ZeroDurationHeartbeats_CannotEstablishConcurrency_Passes()
    {
        // 未提供时长（缺省 0）时退化为瞬时点：两个实例在不同时刻被观测到，
        // 无法据此断定它们**同时**在采集，因此不判红。
        // 需要注意：这也意味着纯瞬时输入不足以判定并发 —— 生产取数层必须提供
        // DurationSeconds（pc_tracker_events.duration），否则这条尺子会失去判定能力。
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(10), InstanceId = "inst-B", SessionId = 1 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S13_ToleranceIsConfigurable()
    {
        // 把容差抬到 1s：200ms 的重叠就落在容差内，不再判红 —— 证明容差确实生效
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600.2, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddSeconds(600), DurationSeconds = 60, InstanceId = "inst-B", SessionId = 2 }
        };

        var relaxed = new InvariantOptions { InstanceOverlapToleranceSeconds = 1.0 };
        Assert.True(DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats, relaxed).Pass);

        var strict = new InvariantOptions { InstanceOverlapToleranceSeconds = 0.0 };
        Assert.False(DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats, strict).Pass);
    }

    [Fact]
    public void S13_SameInstanceOverlappingSegments_Passes()
    {
        // 同一个 instanceId 的片段相互重叠属于同一条采集流内部的事，不是多实例并发
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(5), DurationSeconds = 600, InstanceId = "inst-A", SessionId = 2 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(10), DurationSeconds = 600, InstanceId = "inst-A", SessionId = 3 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void S13_ThreeInstances_ReportsSingleHourViolation()
    {
        // 三实例并发仍按"每设备每小时 1 处"计
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, DurationSeconds = 600, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(1), DurationSeconds = 600, InstanceId = "inst-B", SessionId = 2 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(2), DurationSeconds = 600, InstanceId = "inst-C", SessionId = 3 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
    }

    [Fact]
    public void S13_MutuallyExclusiveCollectionStreamsByPhase_Fails()
    {
        // 同一设备同小时出现两条相位交错的周期采集流 (0s 与 5s 相位)
        var heartbeats = new List<CollectionHeartbeat>();
        for (int i = 0; i < 10; i++)
        {
            heartbeats.Add(new CollectionHeartbeat
            {
                DeviceId = "PC-MAIN",
                Timestamp = _baseUtc.AddMinutes(i),
                PhaseOffsetSeconds = 0.0
            });
            heartbeats.Add(new CollectionHeartbeat
            {
                DeviceId = "PC-MAIN",
                Timestamp = _baseUtc.AddMinutes(i).AddSeconds(5),
                PhaseOffsetSeconds = 5.0
            });
        }

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("互斥轮询相位并发交错", result.Detail);
    }

    [Fact]
    public void S13_SingleInstanceStream_Passes()
    {
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(10), InstanceId = "inst-A", SessionId = 2 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(20), InstanceId = "inst-A", SessionId = 3 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.True(result.Pass);
        Assert.Equal(0, result.TotalViolations);
    }

    #endregion
}
