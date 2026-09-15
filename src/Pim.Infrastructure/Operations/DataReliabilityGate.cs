using System;
using System.Collections.Generic;
using System.Linq;
using Pim.Core.Invariants;
using Pim.Core.Operations;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 尺子结论对外的"门禁"结果：把数据可信度的红黄绿翻译成质量报告能用的健康状态。
/// </summary>
public sealed record DataReliabilityGateVerdict(
    PimHealthStatus Status,
    IReadOnlyList<string> RedRules,
    IReadOnlyList<string> YellowRules,
    IReadOnlyList<string> UnknownRules,
    DateTimeOffset? InspectedAtUtc,
    string Message);

/// <summary>
/// 质量报告与尺子结论之间的唯一接口（#260 第 4 点）：**尺子红，报告不得绿**。
/// </summary>
public interface IDataReliabilityGate
{
    /// <summary>
    /// 评估给定的一组尺子。默认只接受 26 小时内的体检结果；过期或从未体检时返回 <see cref="PimHealthStatus.Unknown"/>
    /// 并给出明确文案（禁止静默判健康）。
    /// </summary>
    DataReliabilityGateVerdict Evaluate(IReadOnlyList<string> ruleCodes, TimeSpan? maxAge = null);
}

/// <inheritdoc />
public sealed class DataReliabilityGate : IDataReliabilityGate
{
    /// <summary>默认新鲜度窗口：后台巡检每小时一次，26 小时足够覆盖一次漏跑。</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(26);

    private readonly IDataReliabilityInspectionStore _store;
    private readonly TimeProvider _timeProvider;

    public DataReliabilityGate(IDataReliabilityInspectionStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public DataReliabilityGateVerdict Evaluate(IReadOnlyList<string> ruleCodes, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(ruleCodes);

        var report = _store.Latest;
        if (report == null)
        {
            return new DataReliabilityGateVerdict(
                PimHealthStatus.Unknown,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                null,
                "数据可信度尺子尚未体检，本次质量结论无法与尺子对齐（可在「设置 → 数据可信度」手动体检）");
        }

        var effectiveMaxAge = maxAge ?? DefaultMaxAge;
        var age = _timeProvider.GetUtcNow() - report.InspectedAtUtc;
        if (age > effectiveMaxAge)
        {
            return new DataReliabilityGateVerdict(
                PimHealthStatus.Unknown,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                report.InspectedAtUtc,
                $"数据可信度尺子结果已过期（最近体检：{report.InspectedAtUtc:yyyy-MM-dd HH:mm:ss}，距今 {age.TotalHours:F1} 小时），本次质量结论无法与尺子对齐");
        }

        var selected = report.Rules
            .Where(rule => ruleCodes.Any(code => string.Equals(code, rule.Code, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var redRules = selected.Where(rule => rule.Status == "red").Select(rule => rule.Code).ToArray();
        var yellowRules = selected.Where(rule => rule.Status == "yellow").Select(rule => rule.Code).ToArray();

        // 未知（数据源缺失 / 未接线 / 取数超时）绝不等于通过：只要选中的尺子里有未知，
        // 就不能把整体判成 Healthy，否则质量报告会拿一条根本没跑出结论的尺子当绿色背书。
        var unknownRules = selected.Where(rule => rule.Status == "unknown").Select(rule => rule.Code).ToArray();

        var status = redRules.Length > 0
            ? PimHealthStatus.Critical
            : yellowRules.Length > 0
                ? PimHealthStatus.Warning
                : unknownRules.Length > 0
                    ? PimHealthStatus.Unknown
                    : PimHealthStatus.Healthy;

        var message = status switch
        {
            PimHealthStatus.Critical => $"数据可信度尺子报红：{string.Join("、", redRules)}（本次体检 {report.InspectedAtUtc:yyyy-MM-dd HH:mm:ss}）",
            PimHealthStatus.Warning => $"数据可信度尺子报黄：{string.Join("、", yellowRules)}（本次体检 {report.InspectedAtUtc:yyyy-MM-dd HH:mm:ss}）",
            PimHealthStatus.Unknown => $"数据可信度尺子未能判定：{string.Join("、", unknownRules)}（数据源缺失、未接线或取数超时；本次体检 {report.InspectedAtUtc:yyyy-MM-dd HH:mm:ss}）",
            _ => $"数据可信度尺子（{string.Join("、", selected.Select(rule => rule.Code))}）均通过（本次体检 {report.InspectedAtUtc:yyyy-MM-dd HH:mm:ss}）"
        };

        return new DataReliabilityGateVerdict(status, redRules, yellowRules, unknownRules, report.InspectedAtUtc, message);
    }
}
