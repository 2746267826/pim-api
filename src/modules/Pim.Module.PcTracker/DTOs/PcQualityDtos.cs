using Pim.Core.Operations;

namespace Pim.Module.PcTracker.DTOs;

public record PcQualityResponse(
    PimHealthStatus OverallStatus,
    string Label,
    string Message,
    DateTimeOffset CheckedAt,
    IReadOnlyList<PcQualityComponentDto> Components,
    IReadOnlyList<PcQualityIssueDto> Issues,
    IReadOnlyList<string> NextSteps);

public record PcQualityComponentDto(
    string Key,
    string Name,
    PimHealthStatus Status,
    string Message,
    /// <summary>
    /// 组件的机器可读细节。值类型为 <see cref="object"/> 以便同时承载标量与数组
    /// （WO-PC-BACKEND-20261001 REQ-5：<c>tracker-events</c> 的 <c>missingSegments</c> 是数组）。
    /// 既有键的取值仍是字符串，JSON 形状不变。
    /// </summary>
    IReadOnlyDictionary<string, object?> Details);

/// <summary>
/// WO-PC-BACKEND-20261001 REQ-5（#377）：缺数时段的结构化表示，供前端 / MCP 直接消费，
/// 不再靠正则解析本地化文案。时刻为 ISO 8601（UTC）。
/// </summary>
public record PcQualityMissingSegmentDto(string StartUtc, string EndUtc);

public record PcQualityIssueDto(
    string Code,
    PimHealthStatus Severity,
    string ComponentKey,
    string Message,
    string? NextStep);
