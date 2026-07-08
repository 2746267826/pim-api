using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsNotificationActionRouterTests
{
    [Fact]
    public void HighRiskActionsOpenWebAuditDetail()
    {
        var router = new NotificationActionRouter();

        var result = router.Route("confirm", "L3ExternalSourceOrWriteback", "confirmation-1");

        Assert.Equal("OpenDetailRequired", result.Kind);
        Assert.Equal("/confirmations/confirmation-1", result.DetailUrl);
    }

    [Fact]
    public void LowRiskActionsExecuteDirectly()
    {
        var router = new NotificationActionRouter();

        var result = router.Route("dismiss", "L1LowRiskAction");

        Assert.Equal("Executed", result.Kind);
        Assert.Null(result.DetailUrl);
    }

    [Fact]
    public void OfflineBoundaryOnlyAllowsCollectionUploads()
    {
        var boundary = new EndpointCollectionBoundaryService();

        Assert.True(boundary.CanQueueOffline("collection-upload"));
        Assert.True(boundary.CanQueueOffline("pc-activity"));
        Assert.True(boundary.CanQueueOffline("window-context"));
        Assert.True(boundary.CanQueueOffline("upload-retry"));
        Assert.False(boundary.CanQueueOffline("task-fact-change"));
        Assert.False(boundary.CanQueueOffline("event-fact-change"));
        Assert.False(boundary.CanQueueOffline("habit-rule-change"));
        Assert.False(boundary.CanQueueOffline("confirmation-decision"));
        Assert.False(boundary.CanQueueOffline("report-edit"));
        Assert.False(boundary.CanQueueOffline("outlook-writeback"));
        Assert.False(boundary.CanQueueOffline("restore-delete-operation"));
    }
}
