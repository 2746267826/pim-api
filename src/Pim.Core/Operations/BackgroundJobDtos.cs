namespace Pim.Core.Operations;

public sealed record BackgroundJobSummaryDto(
    PimHealthStatus Status,
    int Processing,
    int Enqueued,
    int Scheduled,
    int Failed,
    DateTimeOffset CheckedAt,
    string Message);

public interface IBackgroundJobStatusService
{
    Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default);
}
