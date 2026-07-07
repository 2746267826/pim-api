using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class ScheduleWorkbenchConfirmationContractTests
{
    [Fact]
    public void RiskLevelsExposeWorkbenchScaleAndLegacyValues()
    {
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L0AutomaticArtifact"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L1LowRiskAction"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L2PimFactChange"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L3ExternalSourceOrWriteback"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L4BatchOrDestructiveGovernance"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "Low"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "Medium"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "High"));
    }

    [Fact]
    public void ConfirmationDtoCarriesDiffAndSecondLevelMetadata()
    {
        var dto = new OperationConfirmationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "calendar.event.update",
            "Change Outlook event location",
            OperationRiskLevel.L3ExternalSourceOrWriteback,
            "outlook",
            "{}",
            "{}",
            OperationConfirmationStatus.Pending,
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            "corr-1",
            ["location"],
            ["keep_pim", "keep_outlook"],
            "event",
            Guid.NewGuid(),
            true);

        Assert.Contains("location", dto.ChangedFields ?? []);
        Assert.Contains("keep_outlook", dto.AllowedActions ?? []);
        Assert.Equal("event", dto.ObjectType);
        Assert.True(dto.RequiresSecondLevelConfirmation);
    }

    [Fact]
    public async Task ConfirmationServiceFallsBackToMediumForUnknownRiskValues()
    {
        await using var db = CreateDb();
        var confirmation = new OperationConfirmationEntity
        {
            OperationType = "calendar.event.update",
            Summary = "Unknown risk from older data",
            RiskLevel = "FutureRiskValue",
            Source = "test",
            PayloadJson = "{}",
            PreviewJson = "{}",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.OperationConfirmations.Add(confirmation);
        await db.SaveChangesAsync();

        var service = new OperationConfirmationService(db);
        var dto = await service.GetAsync(confirmation.Id);

        Assert.NotNull(dto);
        Assert.Equal(OperationRiskLevel.Medium, dto.RiskLevel);
    }

    [Fact]
    public async Task ConfirmationServicePersistsDiffMetadataForPendingViews()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);
        var objectId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            userId,
            "calendar.outlook.writeback",
            "Write Outlook event",
            OperationRiskLevel.L3ExternalSourceOrWriteback,
            "outlook",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddHours(2),
            "corr",
            ["title", "dtStart"],
            ["keep_pim", "keep_outlook"],
            "event",
            objectId,
            true));

        var listed = Assert.Single(await service.ListPendingForUserAsync(userId));

        Assert.Equal(created.Id, listed.Id);
        Assert.Contains("title", listed.ChangedFields ?? []);
        Assert.Contains("keep_outlook", listed.AllowedActions ?? []);
        Assert.Equal("event", listed.ObjectType);
        Assert.Equal(objectId, listed.ObjectId);
        Assert.True(listed.RequiresSecondLevelConfirmation);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
