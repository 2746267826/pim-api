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
    IReadOnlyDictionary<string, string> Details);

public record PcQualityIssueDto(
    string Code,
    PimHealthStatus Severity,
    string ComponentKey,
    string Message,
    string? NextStep);
