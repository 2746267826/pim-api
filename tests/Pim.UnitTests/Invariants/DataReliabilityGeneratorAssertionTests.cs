using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Pim.UnitTests.Harness.Generators;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// Issue #259: 生成器补充 + CI 门禁（让尺子真的会红）
/// 验收标准验证：
/// 1. 13 类尺子全部配备生成器（S1–S13，涵盖 INV-P16 到 INV-P22）
/// 2. 每把尺子至少有 1 个注入违规转红测试和 1 个正常数据转绿测试
/// 3. 断言失败与结果报告中显式包含 INV 编号（如 [INV-P16], [INV-P17] 等）
/// 4. 零外部 DB 硬依赖：无外部数据库时可离线运行且全部绿色
/// 5. 生成器可复现性（基于确定的 seed）
/// </summary>
public class DataReliabilityGeneratorAssertionTests
{
    private readonly DateTime _referenceUtc = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    #region S1 (INV-P16): 同类型事件区间重叠

    [Fact]
    public void INV_P16_S1_InjectedOverlap_MustTurnRed()
    {
        var overlappingEvents = OverlappingSessionGenerator.GenerateS1OverlappingEvents(seed: 42);
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(overlappingEvents, referenceTimeUtc: _referenceUtc);

        Assert.False(result.Pass, "[INV-P16] Invariant S1 must fail when overlapping intervals of same type are injected");
        Assert.True(result.TotalViolations > 0, "[INV-P16] TotalViolations must be greater than 0");
        Assert.Contains("INV-P16", result.Detail);
    }

    [Fact]
    public void INV_P16_S1_InjectedNestedOverlap_MustTurnRed()
    {
        var nestedEvents = OverlappingSessionGenerator.GenerateS1NestedEvents(seed: 42);
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(nestedEvents, referenceTimeUtc: _referenceUtc);

        Assert.False(result.Pass, "[INV-P16] Invariant S1 must fail when nested parent-child intervals of same type are injected");
        Assert.True(result.TotalViolations > 0, "[INV-P16] TotalViolations must be greater than 0 for nested overlap");
        Assert.Contains("INV-P16", result.Detail);
    }

    [Fact]
    public void INV_P16_S1_NormalData_MustBeGreen()
    {
        var normalEvents = OverlappingSessionGenerator.GenerateS1NormalEvents(count: 6, seed: 42);
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(normalEvents, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-P16] Invariant S1 must pass for normal non-overlapping intervals: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P16", result.Detail);
    }

    [Fact]
    public void INV_P16_S1_DifferentTypesOverlap_MustBeGreen()
    {
        var diffTypeEvents = OverlappingSessionGenerator.GenerateS1DifferentTypeOverlapEvents(seed: 42);
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(diffTypeEvents, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-P16] Overlap between different event types is valid and must pass: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
    }

    #endregion

    #region S2 (INV-P17): 超长事件三态判定（有输入 / 有媒体 / 疑似未收尾）

    [Fact]
    public void INV_P17_S2_InjectedZombieUnclosed_MustTurnRed()
    {
        var triStateWithZombie = PcActivityStreamGenerator.GenerateS2TriStateEvents(seed: 42, injectSuspectedZombie: true);
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(triStateWithZombie, referenceTimeUtc: _referenceUtc);

        Assert.False(result.Pass, "[INV-P17] Invariant S2 must fail when overlong event has no input, no media, and no offline declaration");
        Assert.True(result.TotalViolations > 0, "[INV-P17] TotalViolations must be greater than 0");
        Assert.Contains("INV-P17", result.Detail);
    }

    [Fact]
    public void INV_P17_S2_NormalData_MustBeGreen()
    {
        // 仅包含有按键/鼠标输入与有媒体播放的合法事件
        var normalEvents = PcActivityStreamGenerator.GenerateS2NormalEvents(count: 6, seed: 42);
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(normalEvents, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-P17] Invariant S2 must pass when long events have input or media evidence: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P17", result.Detail);
    }

    [Fact]
    public void INV_P17_S2_TriStateWithoutZombie_MustBeGreen()
    {
        var triStateClean = PcActivityStreamGenerator.GenerateS2TriStateEvents(seed: 42, injectSuspectedZombie: false);
        var result = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(triStateClean, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-P17] Long events with keystrokes or media active must pass: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
    }

    #endregion

    #region S3 (INV-P18): 单日时长警戒线与硬上限

    [Fact]
    public void INV_P18_S3_InjectedHardCap_MustTurnRed()
    {
        var hardCapData = CrossDayBoundaryGenerator.GenerateS3HardCapViolation(seed: 42);
        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(hardCapData);

        Assert.False(result.Pass, "[INV-P18] Invariant S3 must fail when daily active duration exceeds 24.0h hard cap");
        Assert.True(result.TotalViolations > 0, "[INV-P18] TotalViolations must be greater than 0");
        Assert.Contains("INV-P18", result.Detail);
    }

    [Fact]
    public void INV_P18_S3_InjectedWarningLine_ReportsWarning()
    {
        var warningData = CrossDayBoundaryGenerator.GenerateS3WarningViolation(seed: 42);
        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(warningData);

        // 16.5h > 14.4h 警告线，但 <= 24h 硬上限 -> Pass 为 true，IsWarning 为 true
        Assert.True(result.Pass, "[INV-P18] Invariant S3 passes hard cap when within 24h");
        Assert.True(result.IsWarning, "[INV-P18] Invariant S3 must trigger IsWarning when daily active duration exceeds 14.4h awake window warning line");
        Assert.Contains("INV-P18 WARN", result.Detail);
    }

    [Fact]
    public void INV_P18_S3_NormalData_MustBeGreen()
    {
        var normalData = CrossDayBoundaryGenerator.GenerateS3NormalDurations(count: 7, seed: 42);
        var result = DataReliabilityInvariants.CheckS3_DailyDurationBounded(normalData);

        Assert.True(result.Pass, $"[INV-P18] Invariant S3 must pass for normal daily active durations (<= 14.4h): {result.Detail}");
        Assert.False(result.IsWarning, "[INV-P18] Normal durations must not trigger warning");
        Assert.Contains("INV-P18 PASS", result.Detail);
    }

    #endregion

    #region S4 (INV-C18): 业务去重键唯一性

    [Fact]
    public void INV_C18_S4_InjectedDuplicateKeys_MustTurnRed()
    {
        var duplicateData = CorruptedDataGenerator.GenerateS4DuplicateKeys(seed: 42);
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(duplicateData, referenceTimeUtc: _referenceUtc);

        Assert.False(result.Pass, "[INV-C18] Invariant S4 must fail when duplicate business keys are injected");
        Assert.True(result.TotalViolations > 0, "[INV-C18] TotalViolations must be greater than 0");
        Assert.Contains("INV-C18", result.Detail);
    }

    [Fact]
    public void INV_C18_S4_NormalData_MustBeGreen()
    {
        var uniqueData = CorruptedDataGenerator.GenerateS4UniqueKeys(count: 20, seed: 42);
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(uniqueData, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-C18] Invariant S4 must pass when all business keys are unique: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-C18", result.Detail);
    }

    #endregion

    #region S5 (INV-P19): 时钟可信（超前/落后）

    [Fact]
    public void INV_P19_S5_InjectedClockSkew_MustTurnRed()
    {
        var skewData = CorruptedDataGenerator.GenerateS5ClockSkewViolations(seed: 42);
        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(skewData, referenceTimeUtc: _referenceUtc);

        Assert.False(result.Pass, "[INV-P19] Invariant S5 must fail when event timestamp exceeds server received time by > 5.0 minutes");
        Assert.True(result.TotalViolations > 0, "[INV-P19] TotalViolations must be greater than 0");
        Assert.Contains("INV-P19", result.Detail);
    }

    [Fact]
    public void INV_P19_S5_NormalData_MustBeGreen()
    {
        var normalData = CorruptedDataGenerator.GenerateS5NormalClockEvents(count: 20, seed: 42);
        var result = DataReliabilityInvariants.CheckS5_ClockTrustworthy(normalData, referenceTimeUtc: _referenceUtc);

        Assert.True(result.Pass, $"[INV-P19] Invariant S5 must pass when clock skew is within 5-minute tolerance: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P19", result.Detail);
    }

    #endregion

    #region S6 (INV-P20): 无声明空档与上传滞后

    [Fact]
    public void INV_P20_S6_InjectedUndeclaredGap_MustTurnRed()
    {
        var undeclaredTrace = MultiDeviceGenerator.GenerateS6UndeclaredGapTrace(seed: 42, byUploadLag: false);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(undeclaredTrace);

        Assert.False(result.Pass, "[INV-P20] Invariant S6 must fail when device has gap > 30 minutes without offline/shutdown declaration");
        Assert.True(result.TotalViolations > 0, "[INV-P20] TotalViolations must be greater than 0");
        Assert.Contains("INV-P20", result.Detail);
    }

    [Fact]
    public void INV_P20_S6_InjectedUploadLagP99_MustTurnRed()
    {
        var lagTrace = MultiDeviceGenerator.GenerateS6UndeclaredGapTrace(seed: 42, byUploadLag: true);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(lagTrace);

        Assert.False(result.Pass, "[INV-P20] Invariant S6 must fail when upload lag p99 exceeds 30.0 minutes threshold");
        Assert.True(result.TotalViolations > 0, "[INV-P20] TotalViolations must be greater than 0");
        Assert.Contains("INV-P20", result.Detail);
    }

    [Fact]
    public void INV_P20_S6_NormalData_MustBeGreen()
    {
        var declaredTrace = MultiDeviceGenerator.GenerateS6DeclaredGapTrace(seed: 42);
        var result = DataReliabilityInvariants.CheckS6_OfflineDeclared(declaredTrace);

        Assert.True(result.Pass, $"[INV-P20] Invariant S6 must pass when gaps have valid shutdown declarations: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P20", result.Detail);
    }

    #endregion

    #region S7 (INV-P21): 时间线 >15 分钟空洞标记

    [Fact]
    public void INV_P21_S7_InjectedUnmarkedGap_MustTurnRed()
    {
        var unmarkedIntervals = MultiDeviceGenerator.GenerateS7UnmarkedGapIntervals(seed: 42);
        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(unmarkedIntervals);

        Assert.False(result.Pass, "[INV-P21] Invariant S7 must fail when timeline gap > 15 minutes is missing gap marker interval");
        Assert.True(result.TotalViolations > 0, "[INV-P21] TotalViolations must be greater than 0");
        Assert.Contains("INV-P21", result.Detail);
    }

    [Fact]
    public void INV_P21_S7_NormalData_MustBeGreen()
    {
        var markedIntervals = MultiDeviceGenerator.GenerateS7MarkedGapIntervals(seed: 42);
        var result = DataReliabilityInvariants.CheckS7_TimelineGapMarked(markedIntervals);

        Assert.True(result.Pass, $"[INV-P21] Invariant S7 must pass when gaps > 15 minutes are bridged with IsGap=true: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P21", result.Detail);
    }

    #endregion

    #region S8 (INV-C19): 本地日凌晨事件跨日界一致性

    [Fact]
    public void INV_C19_S8_InjectedEarlyMorningCrossDay_MustTurnRed()
    {
        var earlyMorningViolations = CrossDayBoundaryGenerator.GenerateS8EarlyMorningViolations(seed: 42);
        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(earlyMorningViolations);

        Assert.False(result.Pass, "[INV-C19] Invariant S8 must fail when early morning (00:00-03:59 CST) event dates conflict across layers");
        Assert.True(result.TotalViolations > 0, "[INV-C19] TotalViolations must be greater than 0");
        Assert.Contains("INV-C19", result.Detail);
    }

    [Fact]
    public void INV_C19_S8_NormalData_MustBeGreen()
    {
        var consistentSamples = CrossDayBoundaryGenerator.GenerateS8ConsistentSamples(count: 6, seed: 42);
        var result = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(consistentSamples);

        Assert.True(result.Pass, $"[INV-C19] Invariant S8 must pass when dates strictly adhere to 04:00 CST business day cutoff: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-C19", result.Detail);
    }

    #endregion

    #region S9 (INV-C20): 覆盖率不足 / 汇总停摆

    [Fact]
    public void INV_C20_S9_InjectedCoverageInsufficient_MustTurnRed()
    {
        // 85% 覆盖率低于 95% 红线，但报告状态却为 Normal
        var report = RealDataSampler.GenerateS9CoverageInsufficientReport(seed: 42, coverage: 0.85);
        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(report);

        Assert.False(result.Pass, "[INV-C20] Invariant S9 must fail when coverage < 95% is falsely reported as Normal");
        Assert.True(result.TotalViolations > 0, "[INV-C20] TotalViolations must be greater than 0");
        Assert.Contains("INV-C20", result.Detail);
    }

    [Fact]
    public void INV_C20_S9_NormalData_MustBeGreen()
    {
        var highCoverageReport = RealDataSampler.GenerateS9NormalReport(seed: 42, isLowCoverageHonest: false);
        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(highCoverageReport);

        Assert.True(result.Pass, $"[INV-C20] Invariant S9 must pass when coverage >= 99% and reported as Normal: {result.Detail}");
        Assert.Contains("INV-C20", result.Detail);
    }

    [Fact]
    public void INV_C20_S9_LowCoverageHonestReport_MustBeGreen()
    {
        // 覆盖率低但诚实上报 Error 状态，不属于假装正常的静默掩盖
        var honestReport = RealDataSampler.GenerateS9NormalReport(seed: 42, isLowCoverageHonest: true);
        var result = DataReliabilityInvariants.CheckS9_GapHasSignal(honestReport);

        Assert.True(result.Pass, $"[INV-C20] Invariant S9 must pass when low coverage is honestly reported as Error: {result.Detail}");
        Assert.Contains("INV-C20", result.Detail);
    }

    #endregion

    #region S10 (INV-C21): 后台任务产出为0且有可处理数据

    [Fact]
    public void INV_C21_S10_InjectedZeroOutputWithData_MustTurnRed()
    {
        var zeroOutputRuns = RealDataSampler.GenerateS10ZeroOutputWithDataRuns(seed: 42);
        var result = DataReliabilityInvariants.CheckS10_TaskHasOutput(zeroOutputRuns);

        Assert.False(result.Pass, "[INV-C21] Invariant S10 must fail when background task has available input data but outputs 0 rows");
        Assert.True(result.TotalViolations > 0, "[INV-C21] TotalViolations must be greater than 0");
        Assert.Contains("INV-C21", result.Detail);
    }

    [Fact]
    public void INV_C21_S10_NormalData_MustBeGreen()
    {
        var normalRuns = RealDataSampler.GenerateS10NormalRuns(count: 4, seed: 42);
        var result = DataReliabilityInvariants.CheckS10_TaskHasOutput(normalRuns);

        Assert.True(result.Pass, $"[INV-C21] Invariant S10 must pass when background tasks produce output matching input: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-C21", result.Detail);
    }

    #endregion

    #region S11 (INV-M21): 批次状态语义自洽

    [Fact]
    public void INV_M21_S11_InjectedStatusInconsistency_MustTurnRed()
    {
        var inconsistentBatches = RealDataSampler.GenerateS11StatusInconsistencyRecords(seed: 42);
        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(inconsistentBatches);

        Assert.False(result.Pass, "[INV-M21] Invariant S11 must fail when batch status contradicts item-level counts");
        Assert.True(result.TotalViolations > 0, "[INV-M21] TotalViolations must be greater than 0");
        Assert.Contains("INV-M21", result.Detail);
    }

    [Fact]
    public void INV_M21_S11_NormalData_MustBeGreen()
    {
        var consistentBatches = RealDataSampler.GenerateS11ConsistentRecords(count: 3, seed: 42);
        var result = DataReliabilityInvariants.CheckS11_StatusSemantics(consistentBatches);

        Assert.True(result.Pass, $"[INV-M21] Invariant S11 must pass when batch status is semantically consistent: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-M21", result.Detail);
    }

    #endregion

    #region S12 (INV-M22): 派生表在正常使用

    [Fact]
    public void INV_M22_S12_InjectedEmptyDerivedTable_MustTurnRed()
    {
        var emptyTables = RealDataSampler.GenerateS12EmptyDerivedTableViolations(seed: 42);
        var result = DataReliabilityInvariants.CheckS12_DerivedTableActive(emptyTables);

        Assert.False(result.Pass, "[INV-M22] Invariant S12 must fail when source table has recent data but derived table has 0 rows without online calculation declaration");
        Assert.True(result.TotalViolations > 0, "[INV-M22] TotalViolations must be greater than 0");
        Assert.Contains("INV-M22", result.Detail);
    }

    [Fact]
    public void INV_M22_S12_NormalData_MustBeGreen()
    {
        var normalTables = RealDataSampler.GenerateS12NormalDerivedTables(count: 2, seed: 42);
        var result = DataReliabilityInvariants.CheckS12_DerivedTableActive(normalTables);

        Assert.True(result.Pass, $"[INV-M22] Invariant S12 must pass when derived tables are populated or explicitly declared online: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-M22", result.Detail);
    }

    #endregion

    #region S13 (INV-P22): 实例唯一（单设备多采集流互斥）

    [Fact]
    public void INV_P22_S13_InjectedMultiStream_MustTurnRed()
    {
        var multiStreamHeartbeats = PcActivityStreamGenerator.GenerateS13MultiStreamViolations(seed: 42);
        var result = DataReliabilityInvariants.CheckS13_SingleInstance(multiStreamHeartbeats);

        Assert.False(result.Pass, "[INV-P22] Invariant S13 must fail when multiple collector instances report for the same device in the same hour");
        Assert.True(result.TotalViolations > 0, "[INV-P22] TotalViolations must be greater than 0");
        Assert.Contains("INV-P22", result.Detail);
    }

    [Fact]
    public void INV_P22_S13_NormalData_MustBeGreen()
    {
        var singleStreamHeartbeats = PcActivityStreamGenerator.GenerateS13SingleStreamNormal(count: 12, seed: 42);
        var result = DataReliabilityInvariants.CheckS13_SingleInstance(singleStreamHeartbeats);

        Assert.True(result.Pass, $"[INV-P22] Invariant S13 must pass when a single collector instance reports monotonically: {result.Detail}");
        Assert.Equal(0, result.TotalViolations);
        Assert.Contains("INV-P22", result.Detail);
    }

    #endregion

    #region Determinism & Seed Repeatability

    [Theory]
    [InlineData(42)]
    [InlineData(100)]
    [InlineData(999)]
    public void All13Generators_ReproducibleAcrossSeeds_DeterministicBehavior(int seed)
    {
        // 1. S1 OverlappingSessionGenerator
        var s1Run1 = OverlappingSessionGenerator.GenerateS1NormalEvents(5, seed);
        var s1Run2 = OverlappingSessionGenerator.GenerateS1NormalEvents(5, seed);
        Assert.Equal(s1Run1.Select(x => (x.StartTime, x.EndTime, x.EventType)), s1Run2.Select(x => (x.StartTime, x.EndTime, x.EventType)));

        // 2. S2 PcActivityStreamGenerator
        var s2Run1 = PcActivityStreamGenerator.GenerateS2NormalEvents(5, seed);
        var s2Run2 = PcActivityStreamGenerator.GenerateS2NormalEvents(5, seed);
        Assert.Equal(s2Run1.Select(x => (x.StartTime, x.EndTime, x.Keystrokes)), s2Run2.Select(x => (x.StartTime, x.EndTime, x.Keystrokes)));

        // 3. S3 CrossDayBoundaryGenerator
        var s3Run1 = CrossDayBoundaryGenerator.GenerateS3NormalDurations(5, seed);
        var s3Run2 = CrossDayBoundaryGenerator.GenerateS3NormalDurations(5, seed);
        Assert.Equal(s3Run1.Select(x => (x.Date, x.ActiveDurationSeconds)), s3Run2.Select(x => (x.Date, x.ActiveDurationSeconds)));

        // 4. S4 CorruptedDataGenerator
        var s4Run1 = CorruptedDataGenerator.GenerateS4UniqueKeys(10, seed);
        var s4Run2 = CorruptedDataGenerator.GenerateS4UniqueKeys(10, seed);
        Assert.Equal(s4Run1.Select(x => (x.Domain, x.UniqueKey)), s4Run2.Select(x => (x.Domain, x.UniqueKey)));

        // 5. S5 CorruptedDataGenerator
        var s5Run1 = CorruptedDataGenerator.GenerateS5NormalClockEvents(10, seed);
        var s5Run2 = CorruptedDataGenerator.GenerateS5NormalClockEvents(10, seed);
        Assert.Equal(s5Run1.Select(x => (x.EventTime, x.ServerReceivedTime)), s5Run2.Select(x => (x.EventTime, x.ServerReceivedTime)));

        // 6. S6 MultiDeviceGenerator
        var s6Run1 = MultiDeviceGenerator.GenerateS6DeclaredGapTrace(seed);
        var s6Run2 = MultiDeviceGenerator.GenerateS6DeclaredGapTrace(seed);
        Assert.Equal(s6Run1.EventTimes, s6Run2.EventTimes);

        // 7. S7 MultiDeviceGenerator
        var s7Run1 = MultiDeviceGenerator.GenerateS7MarkedGapIntervals(seed);
        var s7Run2 = MultiDeviceGenerator.GenerateS7MarkedGapIntervals(seed);
        Assert.Equal(s7Run1.Select(x => (x.StartTime, x.EndTime, x.IsGap)), s7Run2.Select(x => (x.StartTime, x.EndTime, x.IsGap)));

        // 8. S8 CrossDayBoundaryGenerator
        var s8Run1 = CrossDayBoundaryGenerator.GenerateS8ConsistentSamples(5, seed);
        var s8Run2 = CrossDayBoundaryGenerator.GenerateS8ConsistentSamples(5, seed);
        Assert.Equal(s8Run1.Select(x => (x.EventTimeUtc, x.DataFieldDateBucket)), s8Run2.Select(x => (x.EventTimeUtc, x.DataFieldDateBucket)));

        // 9. S9 RealDataSampler
        var s9Run1 = RealDataSampler.GenerateS9NormalReport(seed);
        var s9Run2 = RealDataSampler.GenerateS9NormalReport(seed);
        Assert.Equal(s9Run1.ValidDataDurationSeconds, s9Run2.ValidDataDurationSeconds);

        // 10. S10 RealDataSampler
        var s10Run1 = RealDataSampler.GenerateS10NormalRuns(4, seed);
        var s10Run2 = RealDataSampler.GenerateS10NormalRuns(4, seed);
        Assert.Equal(s10Run1.Select(x => (x.AvailableDataCount, x.OutputCount)), s10Run2.Select(x => (x.AvailableDataCount, x.OutputCount)));

        // 11. S11 RealDataSampler
        var s11Run1 = RealDataSampler.GenerateS11ConsistentRecords(3, seed);
        var s11Run2 = RealDataSampler.GenerateS11ConsistentRecords(3, seed);
        Assert.Equal(s11Run1.Select(x => (x.Status, x.AcceptedCount)), s11Run2.Select(x => (x.Status, x.AcceptedCount)));

        // 12. S12 RealDataSampler
        var s12Run1 = RealDataSampler.GenerateS12NormalDerivedTables(2, seed);
        var s12Run2 = RealDataSampler.GenerateS12NormalDerivedTables(2, seed);
        Assert.Equal(s12Run1.Select(x => (x.TableName, x.DerivedRowCount)), s12Run2.Select(x => (x.TableName, x.DerivedRowCount)));

        // 13. S13 PcActivityStreamGenerator
        var s13Run1 = PcActivityStreamGenerator.GenerateS13SingleStreamNormal(8, seed);
        var s13Run2 = PcActivityStreamGenerator.GenerateS13SingleStreamNormal(8, seed);
        Assert.Equal(s13Run1.Select(x => (x.Timestamp, x.InstanceId)), s13Run2.Select(x => (x.Timestamp, x.InstanceId)));
    }

    #endregion
}
