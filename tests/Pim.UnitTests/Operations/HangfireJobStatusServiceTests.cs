using Pim.Core.Operations;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class HangfireJobStatusServiceTests
{
    [Theory]
    [InlineData(0, PimHealthStatus.Healthy)]
    [InlineData(1, PimHealthStatus.Warning)]
    public void MapFailedCountToStatus_ReturnsExpectedStatus(int failed, PimHealthStatus expected)
    {
        Assert.Equal(expected, HangfireJobStatusService.MapFailedCountToStatus(failed));
    }

    [Fact]
    public async Task GetSummaryAsync_WhenStorageIsHealthy_ReturnsCounts()
    {
        var service = new HangfireJobStatusService(new FakeHangfireMonitoringClient(
            new HangfireMonitoringSnapshot(Processing: 2, Enqueued: 5, Scheduled: 3, Failed: 0)));

        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Healthy, summary.Status);
        Assert.Equal(2, summary.Processing);
        Assert.Equal(5, summary.Enqueued);
        Assert.Equal(3, summary.Scheduled);
        Assert.Equal(0, summary.Failed);
        Assert.Equal("Background jobs are healthy.", summary.Message);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenJobsHaveFailed_ReturnsWarning()
    {
        var service = new HangfireJobStatusService(new FakeHangfireMonitoringClient(
            new HangfireMonitoringSnapshot(Processing: 0, Enqueued: 1, Scheduled: 2, Failed: 4)));

        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal(1, summary.Enqueued);
        Assert.Equal(2, summary.Scheduled);
        Assert.Equal(4, summary.Failed);
        Assert.Equal("Some background jobs have failed.", summary.Message);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenStorageFails_ReturnsCriticalFallback()
    {
        var service = new HangfireJobStatusService(new ThrowingHangfireMonitoringClient());

        var summary = await service.GetSummaryAsync();

        Assert.Equal(PimHealthStatus.Critical, summary.Status);
        Assert.Equal(0, summary.Processing);
        Assert.Equal(0, summary.Enqueued);
        Assert.Equal(0, summary.Scheduled);
        Assert.Equal(0, summary.Failed);
        Assert.Equal("Background job status is unavailable.", summary.Message);
    }

    private sealed class FakeHangfireMonitoringClient : IHangfireMonitoringClient
    {
        private readonly HangfireMonitoringSnapshot _snapshot;

        public FakeHangfireMonitoringClient(HangfireMonitoringSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public HangfireMonitoringSnapshot GetSnapshot() => _snapshot;
    }

    private sealed class ThrowingHangfireMonitoringClient : IHangfireMonitoringClient
    {
        public HangfireMonitoringSnapshot GetSnapshot()
            => throw new InvalidOperationException("Storage connection failed with secret details.");
    }
}
