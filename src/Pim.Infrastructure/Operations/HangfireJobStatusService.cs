using Hangfire;
using Pim.Core.Operations;

namespace Pim.Infrastructure.Operations;

public sealed class HangfireJobStatusService : IBackgroundJobStatusService
{
    public Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var monitoringApi = JobStorage.Current.GetMonitoringApi();
        var queues = monitoringApi.Queues();
        var processing = monitoringApi.ProcessingCount();
        var scheduled = monitoringApi.ScheduledCount();
        var failed = monitoringApi.FailedCount();
        var enqueued = queues.Sum(queue => queue.Length);

        return Task.FromResult(new BackgroundJobSummaryDto(
            MapFailedCountToStatus((int)failed),
            (int)processing,
            (int)enqueued,
            (int)scheduled,
            (int)failed,
            DateTimeOffset.UtcNow,
            failed > 0 ? "Some background jobs have failed." : "Background jobs are healthy."));
    }

    public static PimHealthStatus MapFailedCountToStatus(int failed)
        => failed > 0 ? PimHealthStatus.Warning : PimHealthStatus.Healthy;
}
