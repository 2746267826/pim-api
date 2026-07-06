using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileQualityServiceTests
{
    [Fact]
    public async Task GetQualityAsync_ReturnsStableComponentKeys()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileQualityService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var quality = await service.GetQualityAsync(null, null, CancellationToken.None);

        var keys = quality.Components.Select(component => component.Key).ToHashSet();
        Assert.Contains("android-heartbeat", keys);
        Assert.Contains("mobile-usage-coverage", keys);
        Assert.Contains("mobile-sync", keys);
        Assert.Contains("mobile-location", keys);
        Assert.Contains("mobile-app-metadata", keys);
    }

    [Fact]
    public async Task GetQualityAsync_ReportsRealSyncLocationAndFallbackIssues()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "android-main",
            DaemonKind = "android",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = now.AddMinutes(-5),
            UploadQueueCount = 0,
            StatusJson = "{}",
            ReceivedAt = now.AddMinutes(-5)
        });
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            DeviceHash = "hash-main",
            DisplayName = "Android Main",
            Manufacturer = "PIM",
            Brand = "PIM",
            Model = "Test",
            OsVersion = "14",
            ApiLevel = 35,
            AppVersion = "1.0.0",
            MetadataJson = "{}",
            RegisteredAtUtc = now.AddDays(-1),
            LastSeenAtUtc = now.AddMinutes(-5),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-5)
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.fallback",
            WindowStartUtc = now.AddHours(-2),
            WindowEndUtc = now.AddHours(-1),
            TotalTimeVisibleMs = 120_000,
            SourceKind = "usage-stats-fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-failed",
            WindowStartUtc = now.AddHours(-2),
            WindowEndUtc = now.AddHours(-1),
            AcceptedCount = 0,
            FailedCount = 2,
            Status = "failed",
            ErrorJson = "{\"message\":\"network\"}",
            CreatedAt = now.AddMinutes(-10),
            CompletedAtUtc = now.AddMinutes(-9)
        });
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            RecordedAtUtc = now.AddMinutes(-15),
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = 80m,
            Provider = "gps",
            Source = "manual",
            Quality = "rejected",
            RawJson = "{}",
            CreatedAt = now.AddMinutes(-14)
        });
        await db.SaveChangesAsync();

        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var quality = await service.GetQualityAsync(now.AddDays(-1), now, CancellationToken.None);

        Assert.Equal(PimHealthStatus.Warning, quality.OverallStatus);
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-sync-failed-batch");
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-location-rejected");
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-usage-fallback-only");
        var sync = Assert.Single(quality.Components, component => component.Key == "mobile-sync");
        Assert.Equal(PimHealthStatus.Warning, sync.Status);
        Assert.Equal("1", sync.Details["failedBatchCount"]);
        var location = Assert.Single(quality.Components, component => component.Key == "mobile-location");
        Assert.Equal("1", location.Details["rejectedLocationCount"]);
        var heartbeat = Assert.Single(quality.Components, component => component.Key == "android-heartbeat");
        Assert.Equal(PimHealthStatus.Healthy, heartbeat.Status);
    }

    [Fact]
    public async Task GetQualityAsync_IgnoresHeartbeatsFromOtherUsersDevices()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            DeviceHash = "hash-main",
            DisplayName = "Android Main",
            Manufacturer = "PIM",
            Brand = "PIM",
            Model = "Test",
            OsVersion = "14",
            ApiLevel = 35,
            AppVersion = "1.0.0",
            MetadataJson = "{}",
            RegisteredAtUtc = now.AddDays(-1),
            LastSeenAtUtc = now.AddMinutes(-5),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-5)
        });
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "android-other",
            DaemonKind = "android",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = now.AddMinutes(-5),
            UploadQueueCount = 0,
            StatusJson = "{}",
            ReceivedAt = now.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var quality = await service.GetQualityAsync(now.AddDays(-1), now, CancellationToken.None);

        var heartbeat = Assert.Single(quality.Components, component => component.Key == "android-heartbeat");
        Assert.Equal(PimHealthStatus.Unknown, heartbeat.Status);
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-heartbeat-missing");
    }

    [Fact]
    public async Task GetQualityAsync_WarnsWhenFallbackOrAppMetadataGapsRemainWithRealEvents()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            DeviceHash = "hash-main",
            DisplayName = "Android Main",
            Manufacturer = "PIM",
            Brand = "PIM",
            Model = "Test",
            OsVersion = "14",
            ApiLevel = 35,
            AppVersion = "1.0.0",
            MetadataJson = "{}",
            RegisteredAtUtc = now.AddDays(-1),
            LastSeenAtUtc = now.AddMinutes(-5),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-5)
        });
        db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "android-main",
            DaemonKind = "android",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = now.AddMinutes(-5),
            UploadQueueCount = 2,
            LastError = "network",
            StatusJson = "{}",
            ReceivedAt = now.AddMinutes(-5)
        });
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.real",
            EventType = "ACTIVITY_RESUMED",
            EventTimestampUtc = now.AddHours(-4),
            SourceWindowStartUtc = now.AddHours(-5),
            SourceWindowEndUtc = now.AddHours(-3),
            CollectedAtUtc = now.AddHours(-3),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = now.AddHours(-3)
        });
        db.Set<MobileUsageSummaryEntity>().Add(new MobileUsageSummaryEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.fallback",
            WindowStartUtc = now.AddHours(-2),
            WindowEndUtc = now.AddHours(-1),
            TotalTimeVisibleMs = 120_000,
            SourceKind = "usage-stats-fallback",
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.catalogued",
            DisplayName = "Catalogued",
            RawJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var quality = await service.GetQualityAsync(now.AddDays(-1), now, CancellationToken.None, "android-main");

        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-usage-fallback-only");
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-heartbeat-upload-queue");
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-app-metadata-missing");
        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Warning, usage.Status);
        var heartbeat = Assert.Single(quality.Components, component => component.Key == "android-heartbeat");
        Assert.Equal(PimHealthStatus.Warning, heartbeat.Status);
        var metadata = Assert.Single(quality.Components, component => component.Key == "mobile-app-metadata");
        Assert.Equal("2", metadata.Details["usedPackageCount"]);
        Assert.Equal("2", metadata.Details["missingAppMetadataCount"]);
    }
}
