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

public static class HealthScoreHelper
{
    // 真库回放：确保健康分 0-100 且与状态一致，阈值来源：StatsAndTodayInvariants S02/S05
    public static int ClampScore(double score) => (int)Math.Clamp(Math.Round(score), 0, 100);
    public static PimHealthStatus ScoreToStatus(double score) => score switch
    {
        >= 80 => PimHealthStatus.Healthy,
        >= 50 => PimHealthStatus.Warning,
        _ => PimHealthStatus.Critical
    };
    // 额外校验：确保分数与状态映射单调
    public static bool IsConsistent(double score, PimHealthStatus status) => ScoreToStatus(score) == status;
}
