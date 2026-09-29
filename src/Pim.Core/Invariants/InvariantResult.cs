using System;
using System.Collections.Generic;

namespace Pim.Core.Invariants;

/// <summary>
/// 不变量状态四态：绿（窗内无违规）、黄（阈值型警告）、红（窗内违规）、未知（数据源缺失/未接线）。
/// <para>
/// 口径（WO-RELIABILITY-WINDOW-20260928 REQ-2）：红 / 黄 / 绿**只由窗内违规决定**；
/// 历史欠账（考核线之前的违规）只计数、可下钻、可导出，**不参与任何颜色判定**——
/// "仅欠账"必须返回 <see cref="InvariantStatus.Pass"/>，不得折黄折红。
/// </para>
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

    /// <summary>
    /// 窗内违规数（业务时间 ≥ 考核线）：**唯一决定红 / 黄 / 绿的计数**。
    /// 考核线 = max(体检时刻 − 考核窗, 进程启动时刻)，见 <see cref="DataReliabilityAssessmentWindow"/>。
    /// </summary>
    public int WindowViolations { get; init; } = 0;

    /// <summary>历史欠账数（业务时间 &lt; 考核线）：只计数 / 展示 / 导出，不参与颜色判定。</summary>
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

    /// <summary>
    /// 绿（通过）。允许携带双计数与违规项，用于"窗内 0 违规、但窗外有历史欠账"的场景（REQ-2 / AC-2.1）：
    /// 状态必须是绿，欠账数字与下钻清单仍要看得见。
    /// </summary>
    public static InvariantResult Success(
        string detail,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        string? coveredLayers = null,
        int totalViolations = 0,
        int historicalViolations = 0,
        IReadOnlyList<string>? samples = null,
        IReadOnlyList<InvariantViolation>? violations = null,
        DateTime? earliestOccurrence = null,
        DateTime? latestOccurrence = null) => new()
    {
        Pass = true,
        Status = InvariantStatus.Pass,
        Detail = detail,
        TotalViolations = totalViolations,
        WindowViolations = 0,
        HistoricalViolations = historicalViolations,
        Samples = samples ?? Array.Empty<string>(),
        Violations = violations ?? Array.Empty<InvariantViolation>(),
        EarliestOccurrence = earliestOccurrence,
        LatestOccurrence = latestOccurrence,
        ThresholdNote = thresholdNote,
        ThresholdFallback = thresholdFallback,
        CoveredLayers = coveredLayers
    };

    /// <summary>
    /// 黄（阈值型警告，例如 S3 的 14.4h 清醒窗口警告线）。
    /// 注意：本工厂**不得**再用于"仅历史欠账"——那种情况按 REQ-2 必须是绿。
    /// </summary>
    public static InvariantResult Warning(
        string detail,
        IReadOnlyList<string>? samples = null,
        string? thresholdNote = null,
        bool thresholdFallback = false,
        string? coveredLayers = null,
        int? totalViolations = null,
        int historicalViolations = 0)
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
            TotalViolations = totalViolations ?? samples?.Count ?? 1,
            WindowViolations = totalViolations ?? samples?.Count ?? 1,
            HistoricalViolations = historicalViolations,
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
        int windowViolations = 0,
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
            TotalViolations = totalViolations > 0 ? totalViolations : (windowViolations + historicalViolations),
            WindowViolations = windowViolations,
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
