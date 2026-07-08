using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class ScheduleFactConfirmationGateTests
{
    [Fact]
    public async Task ConfirmationRequestCarriesBeforeAfterStrictAndAuditMetadata()
    {
        await using var db = TestDb.Create();
        var service = new OperationConfirmationService(db);
        var auditBatchId = Guid.NewGuid();

        var confirmation = await service.CreateAsync(new CreateOperationConfirmationRequest(
            RequestedByUserId: Guid.NewGuid(),
            OperationType: "calendar.event.batch-delete",
            Summary: "Delete selected schedule objects",
            RiskLevel: OperationRiskLevel.L4BatchOrDestructiveGovernance,
            Source: "pim",
            PayloadJson: "{}",
            PreviewJson: "{}",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId: "batch-1",
            ChangedFields: ["delete"],
            AllowedActions: ["confirm_strict", "reject"],
            ObjectType: "batch",
            ObjectId: auditBatchId,
            RequiresSecondLevelConfirmation: false,
            BeforeJson: "{\"count\":2}",
            AfterJson: "{\"count\":0}",
            RequiresStrictConfirmation: true,
            AuditBatchId: auditBatchId,
            AiRecommendation: "Review recoverability before deleting.",
            ExternalEffect: "No external writeback.",
            RecoveryPath: "Restore from audit version."));

        Assert.True(confirmation.RequiresStrictConfirmation);
        Assert.Equal(auditBatchId, confirmation.AuditBatchId);
        Assert.Equal("{\"count\":2}", confirmation.BeforeJson);
        Assert.Equal("{\"count\":0}", confirmation.AfterJson);
        Assert.Equal("Review recoverability before deleting.", confirmation.AiRecommendation);
        Assert.Equal("No external writeback.", confirmation.ExternalEffect);
        Assert.Equal("Restore from audit version.", confirmation.RecoveryPath);
    }

    [Fact]
    public async Task OutlookOriginCoreFactChangeCanRequireSecondLevelConfirmation()
    {
        await using var db = TestDb.Create();
        var service = new OperationConfirmationService(db);
        var eventId = Guid.NewGuid();

        var confirmation = await service.CreateAsync(new CreateOperationConfirmationRequest(
            RequestedByUserId: Guid.NewGuid(),
            OperationType: "outlook.event.location-change",
            Summary: "Outlook changed meeting location",
            RiskLevel: OperationRiskLevel.L3ExternalSourceOrWriteback,
            Source: "outlook",
            PayloadJson: "{}",
            PreviewJson: "{}",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId: "outlook-1",
            ChangedFields: ["location"],
            AllowedActions: ["keep_pim", "keep_outlook", "merge_by_field"],
            ObjectType: "event",
            ObjectId: eventId,
            RequiresSecondLevelConfirmation: true,
            BeforeJson: "{\"location\":\"Room A\"}",
            AfterJson: "{\"location\":\"Room B\"}",
            ExternalEffect: "May write back to Microsoft Graph."));

        Assert.Equal(OperationRiskLevel.L3ExternalSourceOrWriteback, confirmation.RiskLevel);
        Assert.True(confirmation.RequiresSecondLevelConfirmation);
        Assert.Contains("location", confirmation.ChangedFields ?? []);
        Assert.Equal("May write back to Microsoft Graph.", confirmation.ExternalEffect);
    }

    [Fact]
    public async Task BasicConfirmCannotBypassSecondLevelOrStrictConfirmation()
    {
        await using var db = TestDb.Create();
        var service = new OperationConfirmationService(db);
        var userId = Guid.NewGuid();

        var secondLevel = await service.CreateAsync(new CreateOperationConfirmationRequest(
            userId,
            "outlook.event.location-change",
            "Outlook changed meeting location",
            OperationRiskLevel.L3ExternalSourceOrWriteback,
            "outlook",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddHours(1),
            "outlook-2",
            RequiresSecondLevelConfirmation: true));

        var strict = await service.CreateAsync(new CreateOperationConfirmationRequest(
            userId,
            "calendar.batch-delete",
            "Delete selected schedule objects",
            OperationRiskLevel.L4BatchOrDestructiveGovernance,
            "data-center",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddHours(1),
            "batch-2",
            RequiresSecondLevelConfirmation: true,
            RequiresStrictConfirmation: true));

        await Assert.ThrowsAsync<DomainException>(() => service.ConfirmAsync(secondLevel.Id, userId));
        await Assert.ThrowsAsync<DomainException>(() => service.ConfirmAsync(strict.Id, userId));
        await Assert.ThrowsAsync<DomainException>(() => service.ConfirmSecondLevelAsync(strict.Id, userId));

        var confirmedSecondLevel = await service.ConfirmSecondLevelAsync(secondLevel.Id, userId);
        var confirmedStrict = await service.ConfirmStrictAsync(strict.Id, userId);

        Assert.Equal(OperationConfirmationStatus.Confirmed, confirmedSecondLevel.Status);
        Assert.Equal(OperationConfirmationStatus.Confirmed, confirmedStrict.Status);
    }

    private static class TestDb
    {
        public static PimDbContext Create()
        {
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;

            return new PimDbContext(options);
        }
    }
}
