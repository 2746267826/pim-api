using Microsoft.EntityFrameworkCore;
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
