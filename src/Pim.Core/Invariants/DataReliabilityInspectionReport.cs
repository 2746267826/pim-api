using System;
using System.Collections.Generic;
using Pim.Core.Liveness;

namespace Pim.Core.Invariants;

/// <summary>
/// S2 三态分布：超长事件在「操作活跃 / 观看活跃 / 疑似未收尾」三态上的时长与条数。
/// <see cref="TotalSeconds"/> 只累加三态，"明确空档"单独放在 <see cref="DeclaredGapSeconds"/>（它本身声明"这里没有人"，不属于活跃时长）。
/// </summary>
public sealed record S2ThreeStateDistribution(
    double InputActiveSeconds,
    double MediaActiveSeconds,
    double SuspectedUnclosedSeconds,
    double TotalSeconds,
    int InputActiveCount,
    int MediaActiveCount,
    int SuspectedUnclosedCount,
    double DeclaredGapSeconds);

/// <summary>
/// 单条尺子的体检结论（体检接口与设置页面板的数据契约）。
/// 判据细节来自 <see cref="InvariantResult"/>，阈值与判据原文来自 <see cref="DataReliabilityRuleCatalog"/>。
/// </summary>
/// <param name="TotalViolations">该尺子的总违规数 = <paramref name="WindowViolations"/> + <paramref name="HistoricalViolations"/>（AC-2.6）。</param>
/// <param name="WindowViolations">窗内违规数（业务时间 ≥ 考核线）：唯一决定红 / 黄 / 绿。</param>
/// <param name="HistoricalViolations">历史欠账数（考核线之前）：只计数 / 展示 / 导出。</param>
/// <param name="ScanTruncated">取数是否命中行数上限；命中时必须显式告知结果可能不完整。</param>
/// <param name="ScanCoveredDays">S3 专用：取数实际覆盖的业务日数（AC-6.4）；其余尺子为 null。</param>
public sealed record DataReliabilityRuleReport(
    string Code,
    string InvariantCode,
    string Key,
    int Order,
    string Name,
    string Group,
    string GroupLabel,
    string Status,
    string StatusLabel,
    string Detail,
    double? CurrentValue,
    string? CurrentValueUnit,
    string? CurrentValueLabel,
    string Threshold,
    string Criterion,
    string Rationale,
    IReadOnlyList<int> RelatedIssues,
    int TotalViolations,
    int WindowViolations,
    int HistoricalViolations,
    DateTimeOffset? EarliestOccurrenceUtc,
    DateTimeOffset? LatestOccurrenceUtc,
    IReadOnlyList<string> Samples,
    bool ThresholdFallback,
    string? ThresholdNote,
    string? CoveredLayers,
    string Trend,
    int? TrendDelta,
    DateTimeOffset? TrendBaselineUtc,
    S2ThreeStateDistribution? ThreeState,
    bool ScanTruncated,
    int? ScanCoveredDays = null);

/// <summary>
/// 一次完整体检的结果（#260）：13 条尺子结论 + 总览计数 + 本次体检时间与耗时。
/// <para>
/// <see cref="DeviceLiveness"/> 是阶段一新增的「设备存活」数据项（REQ-10）：**只展示数据，
/// 本版本不判红/黄/绿**（R4-P1 / AC-10.3），因此它是独立区块而不是第 14 条尺子，
/// 既不参与 <see cref="RedCount"/> / <see cref="YellowCount"/> / <see cref="GreenCount"/> 统计，
/// 也不改变 <see cref="Status"/>。
/// </para>
/// <para>
/// 计数口径（WO-RELIABILITY-WINDOW-20260928 REQ-2 / AC-2.4）：
/// <see cref="TotalViolations"/> **只统计窗内违规**（历史欠账不得计入总览违规数与总览状态），
/// <see cref="WindowViolations"/> 与它同值；<see cref="HistoricalViolations"/> 是 13 条尺子的历史欠账合计
/// （含绿灯尺子的欠账），只展示、不参与颜色。
/// </para>
/// </summary>
public sealed record DataReliabilityInspectionReport(
    DateTimeOffset InspectedAtUtc,
    long Version,
    long ElapsedMilliseconds,
    string Status,
    int RedCount,
    int YellowCount,
    int GreenCount,
    int UnknownCount,
    int TotalViolations,
    int WindowViolations,
    int HistoricalViolations,
    IReadOnlyDictionary<string, string> Notices,
    IReadOnlyList<DataReliabilityRuleReport> Rules,
    string Message,
    IReadOnlyList<DeviceLivenessInspectionItem>? DeviceLiveness = null,
    DateTimeOffset AssessmentStartUtc = default,
    double AssessmentWindowHours = 0);

/// <summary>
/// 违规清单中的一条（ID + 业务时间 + 设备 + 关键字段），用于下钻导出，避免把大列表塞进页面。
/// </summary>
public sealed record DataReliabilityViolationItem(
    string RuleCode,
    string Id,
    string DeviceId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string> Fields,
    bool IsNew = false);

/// <summary>
/// 某条尺子的完整违规清单导出结果。
/// </summary>
public sealed record DataReliabilityViolationExport(
    string RuleCode,
    DateTimeOffset GeneratedAtUtc,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<DataReliabilityViolationItem> Items);
