using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class DataQualityInspectionJobTests
{
    private static PimDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public async Task HeartbeatFreshnessInspector_WhenNoDevices_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var inspector = new HeartbeatFreshnessInspector(db, NullLogger<HeartbeatFreshnessInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        Assert.True(result.IsHealthy);
        Assert.Equal(0, result.IssueCount);
        Assert.Equal("heartbeat", result.CheckName);
    }

    [Fact]
    public async Task HeartbeatFreshnessInspector_WhenDeviceFresh_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "win-dev-01",
            DaemonKind = "windows",
            ReceivedAt = now.AddMinutes(-3),
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var inspector = new HeartbeatFreshnessInspector(db, NullLogger<HeartbeatFreshnessInspector>.Instance);
        var result = await inspector.InspectAsync(now);

        Assert.True(result.IsHealthy);
        Assert.Equal(0, result.IssueCount);
    }

    [Fact]
    public async Task HeartbeatFreshnessInspector_WhenDeviceStale_ReturnsUnhealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "win-dev-stale",
            DaemonKind = "windows",
            ReceivedAt = now.AddMinutes(-15),
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var inspector = new HeartbeatFreshnessInspector(db, NullLogger<HeartbeatFreshnessInspector>.Instance);
        var result = await inspector.InspectAsync(now);

        Assert.False(result.IsHealthy);
        Assert.Equal(1, result.IssueCount);
        Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeartbeatFreshnessInspector_WhenDevicePlannedOffline_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "win-dev-offline",
            DaemonKind = "windows",
            ReceivedAt = now.AddHours(-1),
            PlannedOfflineAt = now.AddHours(-1),
            OfflineReason = "Maintenance",
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var inspector = new HeartbeatFreshnessInspector(db, NullLogger<HeartbeatFreshnessInspector>.Instance);
        var result = await inspector.InspectAsync(now);

        Assert.True(result.IsHealthy);
        Assert.Equal(0, result.IssueCount);
    }

    [Fact]
    public async Task AiGatewayQualityInspector_WhenNoRequests_ReturnsHealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var inspector = new AiGatewayQualityInspector(db, NullLogger<AiGatewayQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        Assert.True(result.IsHealthy);
        Assert.Equal(0, result.IssueCount);
    }

    [Fact]
    public async Task AiGatewayQualityInspector_WhenErrorRateElevated_ReturnsUnhealthy()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;

        // 6 total requests, 3 failed -> 50% error rate (>10% threshold)
        for (int i = 0; i < 3; i++)
        {
            db.AiRequestLogs.Add(new AiRequestLogEntity
            {
                Module = "chat",
                Purpose = "qa",
                StartedAt = now.AddMinutes(-10),
                Status = "Success"
            });
        }
        for (int i = 0; i < 3; i++)
        {
            db.AiRequestLogs.Add(new AiRequestLogEntity
            {
                Module = "chat",
                Purpose = "qa",
                StartedAt = now.AddMinutes(-10),
                Status = "Failed",
                ErrorCode = "ProviderTimeout"
            });
        }
        await db.SaveChangesAsync();

        var inspector = new AiGatewayQualityInspector(db, NullLogger<AiGatewayQualityInspector>.Instance);
        var result = await inspector.InspectAsync(now);

        Assert.False(result.IsHealthy);
        Assert.Equal(3, result.IssueCount);
        Assert.Contains("elevated", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeartbeatStaleInspectionJob_WhenDeviceStale_RecordsAuditLog()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "device-alert-01",
            DaemonKind = "windows",
            ReceivedAt = now.AddMinutes(-12),
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var fakeAudit = new FakeAuditLogService();
        var timeProvider = new FakeTimeProvider(now);

        var job = new HeartbeatStaleInspectionJob(
            db,
            fakeAudit,
            NullLogger<HeartbeatStaleInspectionJob>.Instance,
            timeProvider);

        var staleCount = await job.RunAsync();

        Assert.Equal(1, staleCount);
        Assert.Single(fakeAudit.RecordedRequests);
        var recorded = fakeAudit.RecordedRequests[0];
        Assert.Equal("HeartbeatStaleWatchdog", recorded.Action);
        Assert.Equal(AuditResult.Failure, recorded.Result);
        Assert.Equal("device-alert-01", recorded.ResourceId);
    }

    [Fact]
    public async Task Stage0DiagnosticJob_WhenAllInspectorsHealthy_RecordsSuccessAuditLog()
    {
        var fakeAudit = new FakeAuditLogService();
        var inspector = new StubInspector("stub_check", isHealthy: true, issueCount: 0, "Everything good");

        var job = new Stage0DiagnosticJob(
            new[] { inspector },
            fakeAudit,
            NullLogger<Stage0DiagnosticJob>.Instance);

        var results = await job.RunAsync();

        Assert.Single(results);
        Assert.True(results[0].IsHealthy);
        Assert.Single(fakeAudit.RecordedRequests);
        Assert.Equal(AuditResult.Success, fakeAudit.RecordedRequests[0].Result);
        Assert.Equal("DataQualityPatrol", fakeAudit.RecordedRequests[0].Action);
    }

    [Fact]
    public async Task Stage0DiagnosticJob_WhenInspectorFails_RecordsFailureAuditLog()
    {
        var fakeAudit = new FakeAuditLogService();
        var inspector1 = new StubInspector("healthy_check", isHealthy: true, issueCount: 0, "OK");
        var inspector2 = new StubInspector("failing_check", isHealthy: false, issueCount: 3, "3 items backlogged");

        var job = new Stage0DiagnosticJob(
            new[] { inspector1, inspector2 },
            fakeAudit,
            NullLogger<Stage0DiagnosticJob>.Instance);

        var results = await job.RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Single(fakeAudit.RecordedRequests);
        var audit = fakeAudit.RecordedRequests[0];
        Assert.Equal(AuditResult.Failure, audit.Result);
        Assert.Equal(500, audit.ErrorCode);
        Assert.Contains("3 data quality issue(s)", audit.ErrorMessage);
    }

    private sealed class StubInspector : IDataQualityInspector
    {
        private readonly bool _isHealthy;
        private readonly int _issueCount;
        private readonly string _message;

        public StubInspector(string name, bool isHealthy, int issueCount, string message)
        {
            CheckName = name;
            _isHealthy = isHealthy;
            _issueCount = issueCount;
            _message = message;
        }

        public string CheckName { get; }

        public Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(new DataQualityInspectionResult(CheckName, _isHealthy, _issueCount, _message));
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<CreateAuditLogRequest> RecordedRequests { get; } = new();

        public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
        {
            RecordedRequests.Add(request);
            return Task.FromResult(new AuditLogDto(
                Guid.NewGuid(),
                request.UserId,
                request.ActorType,
                request.Action,
                request.ResourceType,
                request.ResourceId,
                request.Source,
                request.Result,
                request.CorrelationId,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
