using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class DataCenterGovernanceTests
{
    private static readonly Guid UserId = Guid.Parse("93939393-9393-9393-9393-939393939393");

    [Fact]
    public async Task BatchPreviewAndAuditExportExposeStrictGovernanceMetadata()
    {
        await using var db = CreateDb();
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "governance-task@pim",
            Title = "Governance task"
        };
        db.Set<TaskEntity>().Add(task);
        db.AuditVersions.Add(new AuditVersionEntity
        {
            ObjectType = "task",
            ObjectId = task.Id,
            Source = "data-center",
            Actor = "system",
            BeforeJson = "{}",
            AfterJson = """{"title":"Governance task"}""",
            ChangedFieldsJson = """["title"]""",
            CreatedAt = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewBatchOperationAsync(
            new DataCenterBatchOperationRequest(
                "archive",
                [new DataCenterObjectRef("task", task.Id)],
                "governance cleanup"),
            CancellationToken.None);

        Assert.Equal("L4BatchOrDestructiveGovernance", preview.RiskLevel);
        Assert.True(preview.RequiresStrictConfirmation);
        Assert.Contains("Recoverability", preview.Summary);
        Assert.NotEmpty(preview.AffectedObjectTypes);

        var export = await service.ExportAuditAsync(
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            CancellationToken.None);

        Assert.Contains("audit-export.json", export.FileName);
    }

    [Fact]
    public async Task ExecuteConfirmedArchiveBatchSoftDeletesTaskAndRecordsAuditVersion()
    {
        await using var db = CreateDb();
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "archive-task@pim",
            Title = "Archive from data center"
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var confirmation = await service.RequestBatchConfirmationAsync(
            new DataCenterBatchOperationRequest(
                "archive",
                [new DataCenterObjectRef("task", task.Id)],
                "cleanup"),
            CancellationToken.None);
        await new OperationConfirmationService(db).ConfirmStrictAsync(confirmation.Id, UserId);

        var result = await service.ExecuteConfirmedBatchAsync(confirmation.Id, CancellationToken.None);

        var archivedTask = await db.Set<TaskEntity>()
            .IgnoreQueryFilters()
            .SingleAsync(t => t.Id == task.Id);
        var audit = await db.AuditVersions.SingleAsync(v => v.ObjectType == "task" && v.ObjectId == task.Id);
        Assert.Equal(1, result.AffectedCount);
        Assert.NotNull(archivedTask.DeletedAt);
        Assert.Equal(confirmation.Id, archivedTask.DeletedByOperationId);
        Assert.Equal("data-center.batch.archive", archivedTask.DeletedByOperationKind);
        Assert.Equal(confirmation.Id, audit.ConfirmationId);
        Assert.Contains("deletedAt", audit.ChangedFieldsJson);
    }

    [Fact]
    public async Task ExecuteBatchRejectsNonStrictConfirmation()
    {
        await using var db = CreateDb();
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "non-strict-archive@pim",
            Title = "Do not archive without strict metadata"
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();
        var confirmations = new OperationConfirmationService(db);
        var service = CreateService(db);
        var confirmation = await confirmations.CreateAsync(new CreateOperationConfirmationRequest(
            UserId,
            "data-center.batch.archive",
            "Archive selected objects",
            OperationRiskLevel.L4BatchOrDestructiveGovernance,
            "data-center",
            $$"""{"action":"archive","objects":[{"objectType":"task","objectId":"{{task.Id}}"}],"reason":"cleanup"}""",
            "{}",
            DateTimeOffset.UtcNow.AddHours(1),
            "data-center-batch-non-strict"));
        await confirmations.ConfirmAsync(confirmation.Id, UserId);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteConfirmedBatchAsync(confirmation.Id, CancellationToken.None));
    }

    private static DataCenterGovernanceService CreateService(PimDbContext db)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new OperationConfirmationService(db),
            new AuditVersionService(db));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"data-center-governance-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
