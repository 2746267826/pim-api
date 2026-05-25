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
}
