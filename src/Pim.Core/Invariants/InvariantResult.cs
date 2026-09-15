using System;
using System.Collections.Generic;

namespace Pim.Core.Invariants;

/// <summary>
/// 不变量状态四态：绿（通过）、黄（警告/存量欠账）、红（违规/新增）、未知（数据源缺失/未接线）。
/// </summary>
public enum InvariantStatus
{
    Pass,
    Warning,
    Fail,
    Unknown
}

/// <summary>
/// 一条违规的结构化引用（#261 下钻「导出完整违规清单」用）：ID + 业务时间 + 设备 + 关键字段。
/// 与 <see cref="InvariantResult.Samples"/> 在同一处违规分支里生成，因此导出与判据永远不会漂移。
/// </summary>
public sealed record InvariantViolation(
    string Id,
    string DeviceId,
    DateTime OccurredAtUtc,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// 不变量判定统一返回结果结构。
/// 同时支持单条判定（(pass, detail) 解构与隐式转换）与设置页/CI体检场景（统计量、样例、时间范围、回退标注、四态区分、覆盖层级）。
/// </summary>
public sealed class InvariantResult
{
    public bool Pass { get; init; } = true;
    public InvariantStatus Status { get; init; } = InvariantStatus.Pass;
    public string Detail { get; init; } = string.Empty;
    public int TotalViolations { get; init; } = 0;
    public int NewViolations { get; init; } = 0;
    public int HistoricalViolations { get; init; } = 0;
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();

    /// <summary>结构化违规清单（数量受 <see cref="InvariantOptions.MaxSampleCount"/> 约束）。</summary>
    public IReadOnlyList<InvariantViolation> Violations { get; init; } = Array.Empty<InvariantViolation>();
    public DateTime? EarliestOccurrence { get; init; }
    public DateTime? LatestOccurrence { get; init; }
    public bool ThresholdFallback { get; init; } = false;
    public string? ThresholdNote { get; init; }
    public string? CoveredLayers { get; init; }

    public bool IsPass => Status == InvariantStatus.Pass;
    public bool IsWarning => Status == InvariantStatus.Warning;
    public bool IsFail => Status == InvariantStatus.Fail;
    public bool IsUnknown => Status == InvariantStatus.Unknown;

    public void Deconstruct(out bool pass, out string detail)
    {
        pass = Pass;
        detail = Detail;
    }

    public static implicit operator (bool pass, string detail)(InvariantResult r) => (r.Pass, r.Detail);

    public static implicit operator InvariantResult((bool pass, string detail) tuple) => new()
    {
        Pass = tuple.pass,
        Status = tuple.pass ? InvariantStatus.Pass : InvariantStatus.Fail,
        Detail = tuple.detail,
        TotalViolations = tuple.pass ? 0 : 1
    };

    public static InvariantResult Success(
        string detail,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        string? coveredLayers = null) => new()
    {
        Pass = true,
        Status = InvariantStatus.Pass,
        Detail = detail,
        ThresholdNote = thresholdNote,
        ThresholdFallback = thresholdFallback,
        CoveredLayers = coveredLayers
    };

    public static InvariantResult Warning(
        string detail,
        IReadOnlyList<string>? samples = null,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        string? coveredLayers = null)
    {
        string fullDetail = detail;
        if (samples != null && samples.Count > 0)
        {
            fullDetail = $"{detail}. 样本: [{string.Join("; ", samples)}]";
        }

        return new()
        {
            Pass = true,
            Status = InvariantStatus.Warning,
            Detail = fullDetail,
            TotalViolations = 0,
            NewViolations = 0,
            HistoricalViolations = samples?.Count ?? 1,
            Samples = samples ?? Array.Empty<string>(),
            ThresholdNote = thresholdNote,
            ThresholdFallback = thresholdFallback,
            CoveredLayers = coveredLayers
        };
    }

    public static InvariantResult Unknown(
        string detail,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        string? coveredLayers = null) => new()
    {
        Pass = false,
        Status = InvariantStatus.Unknown,
        Detail = detail,
        TotalViolations = 0,
        ThresholdNote = thresholdNote,
        ThresholdFallback = thresholdFallback,
        CoveredLayers = coveredLayers
    };

    public static InvariantResult Failure(
        string detail,
        int totalViolations = 1,
        int newViolations = 0,
        int historicalViolations = 0,
        IReadOnlyList<string>? samples = null,
        DateTime? earliestOccurrence = null,
        DateTime? latestOccurrence = null,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        bool isWarning = false,
        string? coveredLayers = null,
        IReadOnlyList<InvariantViolation>? violations = null)
    {
        string fullDetail = detail;
        if (samples != null && samples.Count > 0)
        {
            fullDetail = $"{detail}. 样本: [{string.Join("; ", samples)}]";
        }

        return new()
        {
            Pass = false,
            Status = isWarning ? InvariantStatus.Warning : InvariantStatus.Fail,
            Detail = fullDetail,
            TotalViolations = totalViolations > 0 ? totalViolations : (newViolations + historicalViolations),
            NewViolations = newViolations,
            HistoricalViolations = historicalViolations,
            Samples = samples ?? Array.Empty<string>(),
            Violations = violations ?? Array.Empty<InvariantViolation>(),
            EarliestOccurrence = earliestOccurrence,
            LatestOccurrence = latestOccurrence,
            ThresholdNote = thresholdNote,
            ThresholdFallback = thresholdFallback,
            CoveredLayers = coveredLayers
        };
    }
}
