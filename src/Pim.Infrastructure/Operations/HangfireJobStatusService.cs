using Hangfire;
using Pim.Core.Operations;

namespace Pim.Infrastructure.Operations;

public sealed record HangfireMonitoringSnapshot(int Processing, int Enqueued, int Scheduled, int Failed);

public interface IHangfireMonitoringClient
{
    HangfireMonitoringSnapshot GetSnapshot();
}

public sealed class HangfireMonitoringClient : IHangfireMonitoringClient
{
    public HangfireMonitoringSnapshot GetSnapshot()
    {
        var monitoringApi = JobStorage.Current.GetMonitoringApi();
        var queues = monitoringApi.Queues();
        var processing = monitoringApi.ProcessingCount();
        var scheduled = monitoringApi.ScheduledCount();
        var failed = monitoringApi.FailedCount();
        var enqueued = queues.Sum(queue => queue.Length);

        return new HangfireMonitoringSnapshot(
            (int)processing,
            (int)enqueued,
            (int)scheduled,
            (int)failed);
    }
}

public sealed class HangfireJobStatusService : IBackgroundJobStatusService
{
    private readonly IHangfireMonitoringClient _monitoringClient;

    public HangfireJobStatusService(IHangfireMonitoringClient monitoringClient)
    {
        _monitoringClient = monitoringClient;
    }

    public Task<BackgroundJobSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            var snapshot = _monitoringClient.GetSnapshot();

            return Task.FromResult(new BackgroundJobSummaryDto(
                MapFailedCountToStatus(snapshot.Failed),
                snapshot.Processing,
                snapshot.Enqueued,
                snapshot.Scheduled,
                snapshot.Failed,
                DateTimeOffset.UtcNow,
                snapshot.Failed > 0 ? "Some background jobs have failed." : "Background jobs are healthy."));
        }
        catch
        {
            return Task.FromResult(new BackgroundJobSummaryDto(
                PimHealthStatus.Critical,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow,
                "Background job status is unavailable."));
        }
    }

    public static PimHealthStatus MapFailedCountToStatus(int failed)
        => failed > 0 ? PimHealthStatus.Warning : PimHealthStatus.Healthy;
}
