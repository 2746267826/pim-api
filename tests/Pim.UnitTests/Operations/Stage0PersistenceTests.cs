using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Operations;

public class Stage0PersistenceTests
{
    [Fact]
    public void PimDbContext_ConfiguresStage0PersistenceMetadata()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);

        var auditLog = db.Model.FindEntityType(typeof(AuditLogEntity))!;
        Assert.Equal("{}", auditLog.FindProperty(nameof(AuditLogEntity.MetadataJson))!.GetDefaultValue());
        Assert.Equal("now()", auditLog.FindProperty(nameof(AuditLogEntity.CreatedAt))!.GetDefaultValueSql());

        var operationConfirmation = db.Model.FindEntityType(typeof(OperationConfirmationEntity))!;
        Assert.Equal("{}", operationConfirmation.FindProperty(nameof(OperationConfirmationEntity.PayloadJson))!.GetDefaultValue());
        Assert.Equal("{}", operationConfirmation.FindProperty(nameof(OperationConfirmationEntity.PreviewJson))!.GetDefaultValue());
        Assert.Equal(OperationConfirmationStatus.Pending.ToString(), operationConfirmation.FindProperty(nameof(OperationConfirmationEntity.Status))!.GetDefaultValue());
        Assert.Null(operationConfirmation.FindProperty(nameof(OperationConfirmationEntity.Summary))!.GetMaxLength());
        Assert.Equal("now()", operationConfirmation.FindProperty(nameof(OperationConfirmationEntity.CreatedAt))!.GetDefaultValueSql());

        var daemonHeartbeat = db.Model.FindEntityType(typeof(DaemonHeartbeatEntity))!;
        Assert.Equal(32, daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.DaemonKind))!.GetMaxLength());
        Assert.True(daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.UploadQueueCount))!.IsNullable);
        Assert.Equal("windows", daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.DaemonKind))!.GetDefaultValue());
        Assert.Equal(DaemonSourceState.Unknown.ToString(), daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.ActivityWatchState))!.GetDefaultValue());
        Assert.Equal(DaemonSourceState.Unknown.ToString(), daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.KeyStatsState))!.GetDefaultValue());
        Assert.Equal("{}", daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.StatusJson))!.GetDefaultValue());
        Assert.Equal("now()", daemonHeartbeat.FindProperty(nameof(DaemonHeartbeatEntity.ReceivedAt))!.GetDefaultValueSql());
    }

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

    [Fact]
    public void ModelSnapshot_ContainsDaemonPlannedOfflineColumns()
    {
        var snapshot = File.ReadAllText(RepoPath(
            "src", "Pim.Infrastructure", "Data", "Migrations", "PimDbContextModelSnapshot.cs"));

        Assert.Contains("\"planned_offline_at\"", snapshot);
        Assert.Contains("\"offline_reason\"", snapshot);
        Assert.Contains("PlannedOfflineAt", snapshot);
        Assert.Contains("OfflineReason", snapshot);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
