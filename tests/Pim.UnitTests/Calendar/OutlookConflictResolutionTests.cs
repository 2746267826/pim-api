using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookConflictResolutionTests
{
    private static readonly Guid UserId = Guid.Parse("12121212-1212-1212-1212-121212121212");

    [Theory]
    [InlineData("keep_pim")]
    [InlineData("keep_outlook")]
    [InlineData("merge_by_field")]
    [InlineData("create_merge_copy")]
    [InlineData("skip_batch")]
    [InlineData("stop_sync")]
    public async Task ManualConflictActionsCreateExpectedConfirmationRisk(string action)
    {
        await using var db = CreateDb();
        var conflict = new SyncConflictEntity
        {
            UserId = UserId,
            Provider = "outlook",
            ObjectType = "event",
            ObjectId = Guid.NewGuid(),
            GraphEventId = "graph-1",
            ConflictKind = "both_sides_changed_location",
            PimSnapshotJson = """{"location":"Old room"}""",
            ExternalSnapshotJson = """{"location":"New room"}"""
        };
        db.Set<SyncConflictEntity>().Add(conflict);
        await db.SaveChangesAsync();
        var service = new OutlookConflictService(
            db,
            new FixedCurrentUserService(UserId),
            new OperationConfirmationService(db));

        var confirmation = await service.RequestActionAsync(
            conflict.Id,
            new ConflictResolutionRequest(action, """{"location":"New room"}""", "manual test"),
            CancellationToken.None);

        Assert.Contains(action, confirmation.AllowedActions ?? []);
        Assert.Equal(
            action == "stop_sync"
                ? OperationRiskLevel.L4BatchOrDestructiveGovernance
                : OperationRiskLevel.L3ExternalSourceOrWriteback,
            confirmation.RiskLevel);
        Assert.True(confirmation.RequiresSecondLevelConfirmation || action == "stop_sync");
        Assert.Equal(action == "stop_sync", confirmation.RequiresStrictConfirmation);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-conflict-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
