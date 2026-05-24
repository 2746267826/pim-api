namespace Pim.Core.Operations;

public sealed record SystemStatusSummaryDto(
    PimHealthStatus Status,
    string Label,
    string Message,
    DateTimeOffset CheckedAt);

public sealed record StatusComponentDto(
    string Key,
    string Name,
    StatusComponentKind Kind,
    PimHealthStatus Status,
    string Message,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, string> Details);

public sealed record SystemStatusDetailDto(
    SystemStatusSummaryDto Summary,
    IReadOnlyList<StatusComponentDto> Components,
    IReadOnlyList<string> NextSteps);

public interface ISystemStatusService
{
    Task<SystemStatusSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<SystemStatusDetailDto> GetDetailAsync(CancellationToken ct = default);
}
