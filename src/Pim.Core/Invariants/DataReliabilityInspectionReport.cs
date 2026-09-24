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
    int NewViolations,
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
    bool ScanTruncated);

/// <summary>
/// 一次完整体检的结果（#260）：13 条尺子结论 + 总览计数 + 本次体检时间与耗时。
/// <para>
/// <see cref="DeviceLiveness"/> 是阶段一新增的「设备存活」数据项（REQ-10）：**只展示数据，
/// 本版本不判红/黄/绿**（R4-P1 / AC-10.3），因此它是独立区块而不是第 14 条尺子，
/// 既不参与 <see cref="RedCount"/> / <see cref="YellowCount"/> / <see cref="GreenCount"/> 统计，
/// 也不改变 <see cref="Status"/>。
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
    int NewViolations,
    int HistoricalViolations,
    IReadOnlyDictionary<string, string> Notices,
    IReadOnlyList<DataReliabilityRuleReport> Rules,
    string Message,
    IReadOnlyList<DeviceLivenessInspectionItem>? DeviceLiveness = null);

/// <summary>
/// 违规清单中的一条（ID + 业务时间 + 设备 + 关键字段），用于下钻导出，避免把大列表塞进页面。
/// </summary>
public sealed record DataReliabilityViolationItem(
    string RuleCode,
    string Id,
    string DeviceId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// 某条尺子的完整违规清单导出结果。
/// </summary>
public sealed record DataReliabilityViolationExport(
    string RuleCode,
    DateTimeOffset GeneratedAtUtc,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<DataReliabilityViolationItem> Items);
