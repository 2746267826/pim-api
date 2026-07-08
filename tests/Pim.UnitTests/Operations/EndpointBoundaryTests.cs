using Microsoft.EntityFrameworkCore;
using Pim.Core.Endpoints;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Endpoints;
using Xunit;

namespace Pim.UnitTests.Operations;

public class EndpointBoundaryTests
{
    private static readonly Guid UserId = Guid.Parse("56565656-5656-5656-5656-565656565656");
    private static readonly Guid OtherUserId = Guid.Parse("57575757-5757-5757-5757-575757575757");

    [Theory]
    [InlineData("pc-activity")]
    [InlineData("android-location")]
    public void CollectionSignalsCanCacheOffline(string operationKind)
    {
        using var db = CreateDb();
        var service = CreateService(db, UserId);

        Assert.True(service.CanCacheOffline(operationKind));
    }

    [Theory]
    [InlineData("task-fact-change")]
    [InlineData("confirmation-decision")]
    [InlineData("report-edit")]
    [InlineData("outlook-writeback")]
    public void FactChangesAndWritebackStayOnlineOnly(string operationKind)
    {
        using var db = CreateDb();
        var service = CreateService(db, UserId);

        Assert.False(service.CanCacheOffline(operationKind));
    }

    [Fact]
    public async Task EndpointHeartbeatPersistsAndStaysUserScoped()
    {
        await using var db = CreateDb();
        await CreateService(db, UserId).UpsertHeartbeatAsync(
            "win-1",
            new EndpointHeartbeatRequest("windows", "1.0.0", "Healthy", 2));

        var userReload = await CreateService(db, UserId).ListAsync();
        var otherUserReload = await CreateService(db, OtherUserId).ListAsync();

        var status = Assert.Single(userReload);
        Assert.Equal("win-1", status.DeviceId);
        Assert.Equal("windows", status.Platform);
        Assert.Equal("Healthy", status.UploadStatus);
        Assert.Equal(2, status.CollectionCacheCount);
        Assert.Empty(otherUserReload);
    }

    [Fact]
    public async Task NotificationActionsPersistHistoryAndBlockedCount()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);
        await service.UpsertHeartbeatAsync(
            "android-1",
            new EndpointHeartbeatRequest("android", "2.0.0", "Warning", 0));

        var lowRisk = await service.HandleNotificationActionAsync(
            "android-1",
            new EndpointNotificationActionRequest("dismiss", "L1LowRiskAction"));
        var highRisk = await service.HandleNotificationActionAsync(
            "android-1",
            new EndpointNotificationActionRequest("confirm", "L3ExternalSourceOrWriteback", "confirmation-1"));

        var actions = await db.Set<EndpointNotificationActionEntity>()
            .Where(a => a.UserId == UserId && a.DeviceId == "android-1")
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        var status = Assert.Single(await service.ListAsync());
        Assert.Equal("Executed", lowRisk.Result);
        Assert.Equal("OpenDetailRequired", highRisk.Result);
        Assert.Equal(2, actions.Count);
        Assert.Equal("dismiss", actions[0].Action);
        Assert.Equal("Executed", actions[0].Result);
        Assert.Equal("OpenDetailRequired", actions[1].Result);
        Assert.Equal(1, status.OnlineOnlyBlockedCount);
    }

    [Fact]
    public async Task RejectedNotificationActionsStillPersistHistory()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);

        var rejected = await service.HandleNotificationActionAsync(
            "android-1",
            new EndpointNotificationActionRequest(null!, null!));

        var action = Assert.Single(await db.Set<EndpointNotificationActionEntity>().ToListAsync());
        Assert.Equal("Rejected", rejected.Result);
        Assert.Equal("Rejected", action.Result);
        Assert.Equal(string.Empty, action.Action);
        Assert.Equal(string.Empty, action.RiskLevel);
    }

    private static EndpointStatusService CreateService(PimDbContext db, Guid userId)
        => new(db, new FixedCurrentUserService(userId));

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"endpoint-boundary-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
