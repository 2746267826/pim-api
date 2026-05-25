using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerQualityServiceTests
{
    [Fact]
    public async Task GetQualityAsync_ReturnsCritical_WhenWindowBucketIsMissing()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddKeyStatsSample(db);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Critical, result.OverallStatus);
        var issue = Assert.Single(result.Issues, i => i.Code == "missing-aw-window-bucket");
        Assert.Equal(PimHealthStatus.Critical, issue.Severity);
        var buckets = Assert.Single(result.Components, c => c.Key == "aw-buckets");
        Assert.Equal(PimHealthStatus.Critical, buckets.Status);
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsWarning_WhenOnlyWebBucketIsMissing()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddWindowEvent(db);
        AddKeyStatsSample(db);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Warning, result.OverallStatus);
        var issue = Assert.Single(result.Issues, i => i.Code == "missing-aw-web-bucket");
        Assert.Equal(PimHealthStatus.Warning, issue.Severity);
        Assert.DoesNotContain(result.Issues, i => i.Code == "missing-aw-window-bucket");
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsUnknownIssue_WhenDaemonHeartbeatIsMissing()
    {
        await using var db = CreateDbContext();
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddKeyStatsSample(db);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        var issue = Assert.Single(result.Issues, i => i.Code == "missing-windows-daemon-heartbeat");
        Assert.Equal(PimHealthStatus.Unknown, issue.Severity);
        Assert.Equal("daemon-upload", issue.ComponentKey);
        var daemon = Assert.Single(result.Components, c => c.Key == "daemon-upload");
        Assert.Equal(PimHealthStatus.Unknown, daemon.Status);
    }

    [Fact]
    public async Task GetQualityAsync_UsesCurrentBuckets_WhenQueryingPastRange()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        var currentSeenAt = DateTimeOffset.Parse("2026-05-25T00:00:00+00:00");
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow", currentSeenAt);
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus", currentSeenAt);
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current", currentSeenAt);
        AddWindowEvent(db);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:10:00+00:00"), keys: 10);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:11:00+00:00"), keys: 12);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.DoesNotContain(result.Issues, i => i.Code == "missing-aw-window-bucket");
        Assert.DoesNotContain(result.Issues, i => i.Code == "missing-aw-afk-bucket");
        Assert.DoesNotContain(result.Issues, i => i.Code == "missing-aw-web-bucket");
        var buckets = Assert.Single(result.Components, c => c.Key == "aw-buckets");
        Assert.NotEqual(PimHealthStatus.Critical, buckets.Status);
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsWarning_WhenOnlyOneKeyStatsSampleCannotBuildInputTimeline()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddKeyStatsSample(db);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Warning, result.OverallStatus);
        var issue = Assert.Single(result.Issues, i => i.Code == "keystats-insufficient-samples");
        Assert.Equal(PimHealthStatus.Warning, issue.Severity);
        Assert.Equal("interpreted-timeline", issue.ComponentKey);
        var timeline = Assert.Single(result.Components, c => c.Key == "interpreted-timeline");
        Assert.Equal(PimHealthStatus.Warning, timeline.Status);
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsWarning_ForKeyStatsGapAndReset()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T05:00:00+00:00"), keys: 20);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T05:04:00+00:00"), keys: 30);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T05:05:00+00:00"), keys: 10);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Warning, result.OverallStatus);
        Assert.Contains(result.Issues, i => i.Code == "keystats-sample-gap");
        Assert.Contains(result.Issues, i => i.Code == "keystats-counter-reset");
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsCompletenessIssue_ForLegacyAwRows()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db, sourceEventId: null, dataJson: "");
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:10:00+00:00"), keys: 10);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:11:00+00:00"), keys: 12);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Code == "aw-events-missing-source-id");
        Assert.Contains(result.Issues, i => i.Code == "aw-events-invalid-data-json");
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsCritical_WhenDaemonHeartbeatIsStale()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db, DateTimeOffset.UtcNow.AddHours(-2));
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:10:00+00:00"), keys: 10);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:11:00+00:00"), keys: 12);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Critical, result.OverallStatus);
        Assert.Contains(result.Issues, i => i.Code == "stale-windows-daemon-heartbeat");
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsHealthy_WhenFactsAreComplete()
    {
        await using var db = CreateDbContext();
        AddRecentWindowsDaemon(db);
        AddBucket(db, "aw-watcher-window_DESKTOP", "currentwindow");
        AddBucket(db, "aw-watcher-afk_DESKTOP", "afkstatus");
        AddBucket(db, "aw-watcher-web-chrome_DESKTOP", "web.tab.current");
        AddWindowEvent(db);
        AddAfkEvent(db);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:10:00+00:00"), keys: 10);
        AddKeyStatsSample(db, DateTimeOffset.Parse("2026-05-20T06:11:00+00:00"), keys: 12);
        await db.SaveChangesAsync();

        var service = new PcTrackerQualityService(db);
        var result = await service.GetQualityAsync(new DateTime(2026, 5, 20), null, null, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Healthy, result.OverallStatus);
        Assert.Empty(result.Issues);
        Assert.Empty(result.NextSteps);
        var timeline = Assert.Single(result.Components, c => c.Key == "interpreted-timeline");
        Assert.Equal(PimHealthStatus.Healthy, timeline.Status);
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PimDbContext(options);
    }

    private static void AddRecentWindowsDaemon(PimDbContext db, DateTimeOffset? receivedAt = null)
    {
        var heartbeatAt = receivedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1);

        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "DESKTOP",
            DaemonKind = "windows",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = heartbeatAt,
            LastAttemptedUploadAt = heartbeatAt,
            UploadQueueCount = 0,
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = heartbeatAt
        });
    }

    private static void AddBucket(PimDbContext db, string bucketId, string bucketType, DateTimeOffset? seenAt = null)
    {
        db.Set<AwBucketEntity>().Add(new AwBucketEntity
        {
            PimDeviceId = "DESKTOP",
            AwDeviceId = "DESKTOP",
            BucketId = bucketId,
            BucketType = bucketType,
            Client = "aw-client",
            Hostname = "DESKTOP",
            SeenAt = seenAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)
        });
    }

    private static void AddWindowEvent(PimDbContext db, int? sourceEventId = 100, string dataJson = "{\"app\":\"Code\"}")
    {
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = DateTimeOffset.Parse("2026-05-20T06:10:00+00:00"),
            Duration = 60,
            EventType = "window",
            AppName = "Code",
            WindowTitle = "Project",
            BucketType = "currentwindow",
            BucketId = "aw-watcher-window_DESKTOP",
            SourceEventId = sourceEventId,
            DataJson = dataJson
        });
    }

    private static void AddAfkEvent(PimDbContext db)
    {
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = DateTimeOffset.Parse("2026-05-20T06:11:00+00:00"),
            Duration = 60,
            EventType = "afk",
            AppName = null,
            WindowTitle = null,
            BucketType = "afkstatus",
            BucketId = "aw-watcher-afk_DESKTOP",
            SourceEventId = 101,
            DataJson = "{\"status\":\"not-afk\"}"
        });
    }

    private static void AddKeyStatsSample(PimDbContext db, DateTimeOffset? sampledAt = null, int keys = 10)
    {
        var sampleTime = sampledAt ?? DateTimeOffset.Parse("2026-05-20T06:10:00+00:00");
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = "DESKTOP",
            SampledAtUtc = sampleTime,
            StatsDate = new DateTime(2026, 5, 20),
            KeyPresses = keys,
            LeftClicks = 2,
            MouseDistance = 100,
            ScrollDistance = 20
        });
    }
}
