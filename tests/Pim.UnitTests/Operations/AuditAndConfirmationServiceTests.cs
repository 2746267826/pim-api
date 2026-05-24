using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class AuditAndConfirmationServiceTests
{
    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public async Task AuditLogService_RecordsAudit()
    {
        await using var db = CreateDb();
        var service = new AuditLogService(db);

        var audit = await service.RecordAsync(new CreateAuditLogRequest(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AuditActorType.User,
            "calendar.event.delete",
            "calendar_event",
            "event-1",
            "web",
            AuditResult.Success,
            "127.0.0.1",
            "UnitTest",
            "corr-1",
            new Dictionary<string, string> { ["reason"] = "test" },
            null,
            null));

        Assert.NotEqual(Guid.Empty, audit.Id);
        Assert.Equal("calendar.event.delete", audit.Action);
        Assert.Equal(1, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task OperationConfirmationService_HandlesLifecycle()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{\"count\":1}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-2"));

        var confirmed = await service.ConfirmAsync(created.Id, created.RequestedByUserId);
        var executed = await service.MarkExecutedAsync(created.Id, "{\"ok\":true}");

        Assert.Equal(OperationConfirmationStatus.Pending, created.Status);
        Assert.Equal(OperationConfirmationStatus.Confirmed, confirmed.Status);
        Assert.Equal(OperationConfirmationStatus.Executed, executed.Status);
        Assert.NotNull(executed.ExecutedAt);
    }

    [Fact]
    public async Task OperationConfirmationService_ExpiresOldPendingRecords()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "corr-3"));

        var expired = await service.ExpireOldAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, expired);
        Assert.Equal(OperationConfirmationStatus.Expired.ToString(), (await db.OperationConfirmations.SingleAsync()).Status);
    }
}
