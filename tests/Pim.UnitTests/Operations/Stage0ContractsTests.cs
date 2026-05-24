using Pim.Core.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class Stage0ContractsTests
{
    [Fact]
    public void HealthStatus_Order_AllowsWorstStatusAggregation()
    {
        Assert.True((int)PimHealthStatus.Unknown < (int)PimHealthStatus.Healthy);
        Assert.True((int)PimHealthStatus.Healthy < (int)PimHealthStatus.Warning);
        Assert.True((int)PimHealthStatus.Warning < (int)PimHealthStatus.Critical);
    }

    [Fact]
    public void ConfirmationStatus_IncludesRequiredLifecycle()
    {
        var names = Enum.GetNames<OperationConfirmationStatus>();

        Assert.Contains("Pending", names);
        Assert.Contains("Confirmed", names);
        Assert.Contains("Rejected", names);
        Assert.Contains("Expired", names);
        Assert.Contains("Executed", names);
    }

    [Fact]
    public void StatusSummary_CanRepresentSidebarIndicator()
    {
        var summary = new SystemStatusSummaryDto(
            PimHealthStatus.Warning,
            "有警告",
            "Windows daemon has not reported recently.",
            DateTimeOffset.Parse("2026-05-24T00:00:00Z"));

        Assert.Equal(PimHealthStatus.Warning, summary.Status);
        Assert.Equal("有警告", summary.Label);
        Assert.Contains("daemon", summary.Message, StringComparison.OrdinalIgnoreCase);
    }
}
