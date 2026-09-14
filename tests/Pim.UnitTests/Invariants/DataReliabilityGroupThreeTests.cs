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
                Status = "failed"
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches);

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
                Status = "completed"
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches);

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
                Status = "completed"
            }
        };

        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(batches);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
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
    public void S13_MultipleInstancesSameDeviceSameHour_Fails()
    {
        // 同一设备同小时出现两个不同的 instanceId
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc, InstanceId = "inst-A", SessionId = 1 },
            new() { DeviceId = "PC-MAIN", Timestamp = _baseUtc.AddMinutes(10), InstanceId = "inst-B", SessionId = 1 }
        };

        var result = DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats);

        Assert.False(result.Pass);
        Assert.Equal(1, result.TotalViolations);
        Assert.Contains("检测到 2 个不同实例ID", result.Detail);
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
