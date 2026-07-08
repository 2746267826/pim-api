using Pim.Infrastructure.Endpoints;
using Xunit;

namespace Pim.UnitTests.Operations;

public class EndpointBoundaryTests
{
    [Theory]
    [InlineData("pc-activity")]
    [InlineData("android-location")]
    public void CollectionSignalsCanCacheOffline(string operationKind)
    {
        var service = new EndpointStatusService();

        Assert.True(service.CanCacheOffline(operationKind));
    }

    [Theory]
    [InlineData("task-fact-change")]
    [InlineData("confirmation-decision")]
    [InlineData("report-edit")]
    [InlineData("outlook-writeback")]
    public void FactChangesAndWritebackStayOnlineOnly(string operationKind)
    {
        var service = new EndpointStatusService();

        Assert.False(service.CanCacheOffline(operationKind));
    }
}
