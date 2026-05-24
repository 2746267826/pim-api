using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Operations;

public class Stage0PersistenceTests
{
    [Fact]
    public async Task PimDbContext_SavesStage0Entities()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new PimDbContext(options);

        db.AuditLogs.Add(new AuditLogEntity
        {
            ActorType = AuditActorType.User.ToString(),
            Action = "calendar.event.create",
            ResourceType = "calendar_event",
            Source = "web",
            Result = AuditResult.Success.ToString(),
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.OperationConfirmations.Add(new OperationConfirmationEntity
        {
            OperationType = "outlook.write",
            Summary = "Write event to Outlook",
            RiskLevel = OperationRiskLevel.High.ToString(),
            Source = "web",
            PayloadJson = "{}",
            PreviewJson = "{}",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "pc-main",
            DaemonKind = "windows",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.AuditLogs.CountAsync());
        Assert.Equal(1, await db.OperationConfirmations.CountAsync());
        Assert.Equal(1, await db.DaemonHeartbeats.CountAsync());
    }
}
