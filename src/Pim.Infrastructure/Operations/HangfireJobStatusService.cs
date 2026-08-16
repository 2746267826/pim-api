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
    private readonly TimeProvider _timeProvider;

    public HangfireJobStatusService(IHangfireMonitoringClient monitoringClient, TimeProvider? timeProvider = null)
    {
        _monitoringClient = monitoringClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                _timeProvider.GetUtcNow(),
                snapshot.Failed > 0 ? "部分后台任务执行失败。" : "后台任务正常。"));
        }
        catch
        {
            return Task.FromResult(new BackgroundJobSummaryDto(
                PimHealthStatus.Critical,
                0,
                0,
                0,
                0,
                _timeProvider.GetUtcNow(),
                "后台任务状态不可用。"));
        }
    }

    public static PimHealthStatus MapFailedCountToStatus(int failed)
        => failed > 0 ? PimHealthStatus.Warning : PimHealthStatus.Healthy;
}
