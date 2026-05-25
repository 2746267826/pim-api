using Pim.Core.Operations;

namespace Pim.Infrastructure.Operations;

public sealed class NoopBackgroundJobStatusService : IBackgroundJobStatusService
{
    public Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(new BackgroundJobSummaryDto(PimHealthStatus.Unknown, 0, 0, 0, 0, DateTimeOffset.UtcNow, "Background jobs are not configured yet."));
}
