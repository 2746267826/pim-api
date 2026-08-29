using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

public sealed class PcTrackerCoverageTests
{
    private static readonly DateTime TestDate = new(2026, 7, 7);
    private static readonly DateTimeOffset DayStart = PcTrackerService.GetBusinessDayStartForQuery(TestDate);

    private static AwEventEntity Win(string app, DateTimeOffset ts, double dur, string? afk = null, string? btype = "currentwindow", string type = "window", string? dataJson = "{}")
        => new() { DeviceId = "pc-1", Timestamp = ts, Duration = dur, EventType = type, AppName = app, AppNameNormalized = app.ToLowerInvariant(), WindowTitle = app, AfkStatus = afk, DataJson = dataJson ?? "{}", BucketType = btype ?? "currentwindow", CreatedAt = ts, UpdatedAt = ts };

    private static AwBucketEntity Bucket(string type, DateTimeOffset seenAt) => new() { PimDeviceId = "pc-1", BucketId = "b-" + type, Name = type, BucketType = type, Client = "aw", Hostname = "h", SeenAt = seenAt };
    private static void SeedCategory(PimDbContext db, string name = "编程", string color = "#10b981")
    {
        if (!db.Set<PcCategoryEntity>().Any(c => c.Name == name))
            db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = name, Color = color, IsBuiltin = false });
    }

    // === PcActivityAggregation: validation branches ===
    [Fact] public async Task Agg_FocusBlocks_InvalidTimezone_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() => svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, "NoSuch/Zone"), CancellationToken.None));
    }
    [Fact] public async Task Agg_FocusBlocks_MissingDateAndRange_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetFocusBlocksAsync(new PcAggregationQuery(null, null, null, null), CancellationToken.None));
    }
    [Fact] public async Task Agg_FocusBlocks_StartAfterEnd_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetFocusBlocksAsync(new PcAggregationQuery(null, "2026-07-10", "2026-07-01", null), CancellationToken.None));
    }
    [Fact] public async Task Agg_FocusBlocks_DateTakesPrecedenceOverRange()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 600));
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6).AddMinutes(6), 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), "2026-01-01", "2026-01-02", null), CancellationToken.None);
        Assert.Single(res.Items);
    }
    [Fact] public async Task Agg_FocusBlocks_AfkFiltered()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 600, "afk"));
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6).AddMinutes(7), 600, "afk"));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Empty(res.Items);
    }
    [Fact] public async Task Agg_FocusBlocks_DurationCappedAt3600()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 7200));
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(7).AddMinutes(5), 600));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Single(res.Items);
        Assert.Equal(70, res.Items[0].DurationMinutes);
    }
    [Fact] public async Task Agg_FocusBlocks_ShortBlocksFiltered()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 120));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetFocusBlocksAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Empty(res.Items);
    }
    [Fact] public async Task Agg_AppUsage_LimitClamped()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 10; i++) db.Set<AwEventEntity>().Add(Win($"app{i}.exe", DayStart.AddHours(6).AddMinutes(i * 10), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var r1 = await svc.GetAppUsageAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), 100, CancellationToken.None);
        Assert.True(r1.Items.Count <= 50);
        var r2 = await svc.GetAppUsageAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), 0, CancellationToken.None);
        Assert.Single(r2.Items);
    }
    [Fact] public async Task Agg_AppUsage_ShortAppsExcluded_TotalIncludesAll()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("short.exe", DayStart.AddHours(6), 30));
        db.Set<AwEventEntity>().Add(Win("long.exe", DayStart.AddHours(7), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), null, CancellationToken.None);
        Assert.DoesNotContain(res.Items, x => x.AppName == "short");
        Assert.Contains(res.Items, x => x.AppName == "long");
        Assert.Equal(6, res.TotalMinutes);
    }
    [Fact] public async Task Agg_AppUsage_PercentageRoundedAndSum()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("a.exe", DayStart.AddHours(6), 600));
        db.Set<AwEventEntity>().Add(Win("b.exe", DayStart.AddHours(7), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetAppUsageAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), null, CancellationToken.None);
        var sum = res.Items.Sum(x => x.Percentage);
        Assert.InRange(sum, 99, 101);
    }
    [Fact] public async Task Agg_LateNight_MultiDayWindow()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetLateNightAsync(new PcAggregationQuery(null, TestDate.ToString("yyyy-MM-dd"), TestDate.AddDays(2).ToString("yyyy-MM-dd"), null), CancellationToken.None);
        Assert.Equal(3, res.Items.Count);
        Assert.All(res.Items, x => Assert.False(string.IsNullOrWhiteSpace(x.Date)));
    }
    [Fact] public async Task Agg_LateNight_HasActivityFlag()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 300));
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetLateNightAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.True(res.Items[0].HadActivity);
        Assert.Equal(0, res.Items[0].Minutes);
    }
    [Fact] public async Task Agg_CategoryDistribution_EmptySnapshots_Empty()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Empty(res.Items);
    }
    [Fact] public async Task Agg_CategoryDistribution_InvalidColor_Fallback()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "k1", RecordType = "window", DeviceId = "pc-1", StartedAt = DayStart.AddHours(6), EndedAt = DayStart.AddHours(6).AddMinutes(10), CategoryName = "自定义分类", CategoryColor = "bad", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Single(res.Items);
        Assert.Equal("#64748b", res.Items[0].Color);
    }
    [Fact] public async Task Agg_CategoryDistribution_ValidColor_Used()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "k2", RecordType = "window", DeviceId = "pc-1", StartedAt = DayStart.AddHours(6), EndedAt = DayStart.AddHours(6).AddMinutes(10), CategoryName = "工作", CategoryColor = "#ff0000", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Equal("#ff0000", res.Items[0].Color);
    }
    [Fact] public async Task Agg_CategoryDistribution_PercentageCorrection()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 3; i++)
            db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = $"kc{i}", RecordType = "window", DeviceId = "pc-1", StartedAt = DayStart.AddHours(6 + i), EndedAt = DayStart.AddHours(6 + i).AddSeconds(200), CategoryName = $"Cat{i}", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.InRange(res.Items.Sum(x => x.Percentage), 99, 101);
    }
    [Fact] public async Task Agg_CategoryDistribution_NegativeDurationClamped()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "kn", RecordType = "window", DeviceId = "pc-1", StartedAt = DayStart.AddHours(7), EndedAt = DayStart.AddHours(6), CategoryName = "工作", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(TestDate.ToString("yyyy-MM-dd"), null, null, null), CancellationToken.None);
        Assert.Single(res.Items);
        Assert.Equal(0, res.Items[0].Minutes);
    }

    // === PcTrackerQualityService branches ===
    [Fact] public async Task Quality_MissingWindowBucket_Critical()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("afkstatus", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        var comp = res.Components.First(c => c.Key == "aw-buckets");
        Assert.Equal(Pim.Core.Operations.PimHealthStatus.Critical, comp.Status);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-window-bucket");
    }
    [Fact] public async Task Quality_MissingAfkBucket_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("currentwindow", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("web.tab.current", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-afk-bucket");
    }
    [Fact] public async Task Quality_MissingWebBucket_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("currentwindow", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("afkstatus", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-web-bucket");
    }
    [Fact] public async Task Quality_StaleBucket_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("currentwindow", DateTimeOffset.UtcNow.AddDays(-2)));
        db.Set<AwBucketEntity>().Add(Bucket("afkstatus", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("web.tab.current", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "stale-aw-bucket");
        var comp = res.Components.First(c => c.Key == "aw-buckets");
        Assert.Equal("1", comp.Details["staleBucketCount"]);
    }
    [Fact] public async Task Quality_HealthyBuckets_NoWarnings()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("currentwindow", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("afkstatus", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("web.tab.current", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        var comp = res.Components.First(c => c.Key == "aw-buckets");
        Assert.Equal(Pim.Core.Operations.PimHealthStatus.Healthy, comp.Status);
    }
    [Fact] public async Task Quality_NoEvents_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwBucketEntity>().Add(Bucket("currentwindow", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("afkstatus", DateTimeOffset.UtcNow));
        db.Set<AwBucketEntity>().Add(Bucket("web.tab.current", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-events");
    }
    [Fact] public async Task Quality_MissingWindowEvents_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("app", DayStart.AddHours(6), 60, null, "afkstatus", "afk"));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-window-events");
    }
    [Fact] public async Task Quality_MissingAfkEvents_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 60, null, "currentwindow", "window"));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-aw-afk-events");
    }
    [Fact] public async Task Quality_MissingSourceId_MajorityCritical()
    {
        await using var db = ServiceTestBase.CreateDb();
        for (int i = 0; i < 3; i++)
        {
            db.Set<AwEventEntity>().Add(new AwEventEntity { DeviceId = "pc-1", Timestamp = DayStart.AddHours(6).AddMinutes(i), Duration = 60, EventType = "window", AppName = "code", AppNameNormalized = "code", WindowTitle = "t", DataJson = "{}", BucketType = "currentwindow", CreatedAt = DayStart, UpdatedAt = DayStart, SourceEventId = null });
            db.Set<AwEventEntity>().Add(new AwEventEntity { DeviceId = "pc-1", Timestamp = DayStart.AddHours(6).AddMinutes(i), Duration = 60, EventType = "afk", AppName = null, AppNameNormalized = null, WindowTitle = null, AfkStatus = "not-afk", DataJson = "{}", BucketType = "afkstatus", CreatedAt = DayStart, UpdatedAt = DayStart, SourceEventId = 999 });
        }
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        var issue = res.Issues.FirstOrDefault(x => x.Code == "aw-events-missing-source-id");
        Assert.True(issue != null);
    }
    [Fact] public async Task Quality_InvalidDataJson_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 60, null, "currentwindow", "window", "not-json"));
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6).AddMinutes(2), 60, null, "afkstatus", "afk", "{}"));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "aw-events-invalid-data-json");
    }
    [Fact] public async Task Quality_MissingKeystats_Critical()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-keystats-samples");
        var comp = res.Components.First(c => c.Key == "keystats-samples");
        Assert.Equal(Pim.Core.Operations.PimHealthStatus.Critical, comp.Status);
    }
    [Fact] public async Task Quality_KeystatsGap_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity { PimDeviceId = "pc-1", SampledAtUtc = DayStart.AddHours(6), StatsDate = TestDate.Date, StatsTimezoneOffsetMinutes = 480, KeyPresses = 100, CreatedAt = DateTimeOffset.UtcNow, KeyCountsJson = "{}", AppStatsJson = "{}", RawJson = "{}" });
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity { PimDeviceId = "pc-1", SampledAtUtc = DayStart.AddHours(8), StatsDate = TestDate.Date, StatsTimezoneOffsetMinutes = 480, KeyPresses = 200, CreatedAt = DateTimeOffset.UtcNow, KeyCountsJson = "{}", AppStatsJson = "{}", RawJson = "{}" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "keystats-sample-gap");
    }
    [Fact] public async Task Quality_KeystatsReset_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity { PimDeviceId = "pc-1", SampledAtUtc = DayStart.AddHours(6), StatsDate = TestDate.Date, StatsTimezoneOffsetMinutes = 480, KeyPresses = 500, CreatedAt = DateTimeOffset.UtcNow, KeyCountsJson = "{}", AppStatsJson = "{}", RawJson = "{}" });
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity { PimDeviceId = "pc-1", SampledAtUtc = DayStart.AddHours(6).AddMinutes(1), StatsDate = TestDate.Date, StatsTimezoneOffsetMinutes = 480, KeyPresses = 10, CreatedAt = DateTimeOffset.UtcNow, KeyCountsJson = "{}", AppStatsJson = "{}", RawJson = "{}" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "keystats-counter-reset");
    }
    [Fact] public async Task Quality_NoDaemonHeartbeat_Unknown()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "missing-windows-daemon-heartbeat");
        var comp = res.Components.First(c => c.Key == "daemon-upload");
        Assert.Equal(Pim.Core.Operations.PimHealthStatus.Unknown, comp.Status);
    }
    [Fact] public async Task Quality_StaleDaemon_Critical()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = DateTimeOffset.UtcNow.AddDays(-2), ActivityWatchState = "running", KeyStatsState = "running" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "stale-windows-daemon-heartbeat");
    }
    [Fact] public async Task Quality_OldDaemon_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10), ActivityWatchState = "running", KeyStatsState = "running" });
        await db.SaveChangesAsync();
        var fixedTime = new TestTimeProvider(DateTimeOffset.UtcNow);
        var svc = new PcTrackerQualityService(db, fixedTime);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "old-daemon-heartbeat");
    }
    [Fact] public async Task Quality_DaemonLastError_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = DateTimeOffset.UtcNow, ActivityWatchState = "running", KeyStatsState = "running", LastError = "boom" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "daemon-last-error");
    }
    [Fact] public async Task Quality_DaemonQueue_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = DateTimeOffset.UtcNow, ActivityWatchState = "running", KeyStatsState = "running", UploadQueueCount = 5 });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "daemon-upload-queue");
    }
    [Fact] public async Task Quality_DaemonSourceUnavailable_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = DateTimeOffset.UtcNow, ActivityWatchState = "Unavailable", KeyStatsState = "running" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "daemon-source-unavailable");
    }
    [Fact] public async Task Quality_PlannedOffline_Unknown()
    {
        await using var db = ServiceTestBase.CreateDb();
        var now = DateTimeOffset.UtcNow;
        db.Set<DaemonHeartbeatEntity>().Add(new DaemonHeartbeatEntity { Id = Guid.NewGuid(), DaemonKind = "windows", ReceivedAt = now, ActivityWatchState = "running", KeyStatsState = "running", PlannedOfflineAt = now.AddMinutes(1), OfflineReason = "shutdown" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "daemon-planned-offline");
    }
    [Fact] public async Task Quality_TimelineIncomplete_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 60));
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "timeline-inputs-incomplete");
    }
    [Fact] public async Task Quality_TimelineInsufficientSamples_Warning()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AwEventEntity>().Add(Win("code.exe", DayStart.AddHours(6), 60));
        db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity { PimDeviceId = "pc-1", SampledAtUtc = DayStart.AddHours(6), StatsDate = TestDate.Date, StatsTimezoneOffsetMinutes = 480, KeyPresses = 100, CreatedAt = DateTimeOffset.UtcNow, KeyCountsJson = "{}", AppStatsJson = "{}", RawJson = "{}" });
        await db.SaveChangesAsync();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Contains(res.Issues, x => x.Code == "keystats-insufficient-samples");
    }
    [Fact] public async Task Quality_DateRangeSwapped_Normalizes()
    {
        await using var db = ServiceTestBase.CreateDb();
        var later = TestDate.AddDays(2);
        var earlier = TestDate;
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(null, later, earlier, CancellationToken.None);
        Assert.True(res.CheckedAt > DateTimeOffset.MinValue);
    }
    [Fact] public async Task Quality_NextSteps_Deduplicated()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcTrackerQualityService(db, TimeProvider.System);
        var res = await svc.GetQualityAsync(TestDate, null, null, CancellationToken.None);
        Assert.Equal(res.NextSteps.Count, res.NextSteps.Distinct().Count());
        Assert.NotEmpty(res.NextSteps);
    }

    // === PcProductivityService ===
    [Fact] public async Task Productivity_Dashboard_NullDate_UsesToday()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(null, CancellationToken.None);
        Assert.Equal(7, res.WeeklyTrend.Count);
        Assert.Equal(0, res.TodayScore);
    }
    [Fact] public async Task Productivity_Dashboard_GoalMet_When5Hours()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "g1", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(day.AddHours(9), TimeSpan.Zero), EndedAt = new DateTimeOffset(day.AddHours(15), TimeSpan.Zero), CategoryName = "工作", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);
        Assert.True(res.GoalMet);
        Assert.Equal(5.0, res.TargetHours);
        Assert.Equal(6.0, res.ProductiveHours);
    }
    [Fact] public async Task Productivity_Dashboard_NeutralCategory()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "n1", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(day.AddHours(9), TimeSpan.Zero), EndedAt = new DateTimeOffset(day.AddHours(10), TimeSpan.Zero), CategoryName = "其他", CategoryColor = "#64748b", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetDashboardAsync(TestDate, CancellationToken.None);
        Assert.Equal(1.0, res.NeutralHours);
        Assert.Equal(0, res.TodayScore);
    }
    [Fact] public async Task Productivity_GetRange_GroupsByDay()
    {
        await using var db = ServiceTestBase.CreateDb();
        var d1 = new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "r1", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(d1, TimeSpan.Zero), EndedAt = new DateTimeOffset(d1.AddHours(1), TimeSpan.Zero), CategoryName = "工作", CategoryColor = "#10b981", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "r2", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(d2, TimeSpan.Zero), EndedAt = new DateTimeOffset(d2.AddHours(1), TimeSpan.Zero), CategoryName = "游戏", CategoryColor = "#ef4444", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetRangeAsync(new DateTime(2026, 7, 6), new DateTime(2026, 7, 7), CancellationToken.None);
        Assert.Equal(2, res.Count);
        Assert.Equal("2026-07-06", res[0].Date);
        Assert.Equal("productive", GetProd(res[0].ProductiveMinutes, res[0].DistractingMinutes));
    }
    [Fact] public async Task Productivity_TimelineV2_Distracting()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        // 使用业务日内时间（10:00 UTC 落在 04:00-次日04:00 Shanghai 窗口内）
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "d1", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(day.AddHours(10), TimeSpan.Zero), EndedAt = new DateTimeOffset(day.AddHours(11), TimeSpan.Zero), CategoryName = "游戏", CategoryColor = "#ef4444", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);
        Assert.Single(res);
        Assert.Equal("distracting", res[0].Productivity);
    }
    [Fact] public async Task Productivity_TimelineV2_NeutralFallback()
    {
        await using var db = ServiceTestBase.CreateDb();
        var day = TestDate.Date;
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity { Id = Guid.NewGuid(), RecordKey = "neu", RecordType = "window", DeviceId = "pc-1", StartedAt = new DateTimeOffset(day.AddHours(10), TimeSpan.Zero), EndedAt = new DateTimeOffset(day.AddHours(10).AddMinutes(30), TimeSpan.Zero), CategoryName = "其他", CategoryColor = "#64748b", Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var res = await svc.GetTimelineV2Async(TestDate, CancellationToken.None);
        Assert.Equal("neutral", res[0].Productivity);
        Assert.Equal("其他", res[0].CategoryName);
    }

    // === ActivityLabelingService ===
    [Fact] public async Task Labeling_AppAll_CreatesMapping()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        var res = await svc.LabelAsync(new ActivityLabelingRequest("app", "myapp", null, "编程", "all", null), CancellationToken.None);
        Assert.True(res.Ok);
        Assert.Equal("编程", res.CategoryName);
    }
    [Fact] public async Task Labeling_DomainAll_CreatesRule()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        var res = await svc.LabelAsync(new ActivityLabelingRequest("domain", "example.com", null, "编程", "all", null), CancellationToken.None);
        Assert.True(res.Ok);
    }
    [Fact] public async Task Labeling_DomainKeyword_WithKeyword()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        var res = await svc.LabelAsync(new ActivityLabelingRequest("domain", "example.com", null, "编程", "keyword", "tutorial"), CancellationToken.None);
        Assert.True(res.Ok);
    }
    [Fact] public async Task Labeling_AppKeyword_CreatesRule()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        var res = await svc.LabelAsync(new ActivityLabelingRequest("app", "chrome", null, "编程", "keyword", "github"), CancellationToken.None);
        Assert.True(res.Ok);
    }
    [Fact] public async Task Labeling_MobileApp_NotSupportedKeyword_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.LabelAsync(new ActivityLabelingRequest("mobile_app", "com.example", null, "编程", "keyword", "kw"), CancellationToken.None));
    }
    [Fact] public async Task Labeling_CustomCategory_CreatesNew()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        var res = await svc.LabelAsync(new ActivityLabelingRequest("app", "notion", null, "全新分类XYZ", "all", null), CancellationToken.None);
        Assert.True(res.Ok);
        Assert.True(db.Set<PcCategoryEntity>().Any(c => c.Name == "全新分类XYZ"));
    }
    [Fact] public async Task Labeling_WizardMode_ReturnsWithCurrentCategory()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        for (int i = 0; i < 4; i++) db.Set<AwEventEntity>().Add(Win("wizardapp.exe", DayStart.AddHours(8).AddMinutes(i * 5), 300, null, "currentwindow", "window", "{}"));
        // normalize already: wizardapp
        // Fix normalized
        foreach (var e in db.Set<AwEventEntity>().Local) e.AppNameNormalized = "wizardapp";
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        await svc.LabelAsync(new ActivityLabelingRequest("app", "wizardapp", null, "编程", "all", null), CancellationToken.None);
        var res = await svc.BuildQueueAsync(10, null, "wizard", CancellationToken.None);
        var item = res.Items.FirstOrDefault(x => x.Target == "wizardapp");
        Assert.True(item != null);
        Assert.Equal("编程", item!.CurrentCategory);
    }
    [Fact] public async Task Labeling_BuildQueue_LimitClamped()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        var r1 = await svc.BuildQueueAsync(0, CancellationToken.None);
        Assert.Empty(r1.Items);
        var r2 = await svc.BuildQueueAsync(200, CancellationToken.None);
        Assert.Empty(r2.Items);
    }
    [Fact] public async Task Labeling_DomainCandidate_FilteredIfCovered()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        db.Set<AwEventEntity>().Add(new AwEventEntity { DeviceId = "pc-1", Timestamp = DayStart.AddHours(6), Duration = 1200, EventType = "web", AppName = "chrome", AppNameNormalized = "chrome", WindowTitle = "Test", DataJson = "{\"url\":\"https://covered.com/page\",\"title\":\"t\"}", BucketType = "web.tab.current", CreatedAt = DayStart, UpdatedAt = DayStart });
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        await svc.LabelAsync(new ActivityLabelingRequest("domain", "covered.com", null, "编程", "all", null), CancellationToken.None);
        var res = await svc.BuildQueueAsync(10, CancellationToken.None);
        Assert.DoesNotContain(res.Items, x => x.Target == "covered.com");
    }
    [Fact] public async Task Labeling_EmptyTarget_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.LabelAsync(new ActivityLabelingRequest("app", " ", null, "编程", "all", null), CancellationToken.None));
    }
    [Fact] public async Task Labeling_MissingCategory_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.LabelAsync(new ActivityLabelingRequest("app", "myapp", null, null, "all", null), CancellationToken.None));
    }
    [Fact] public async Task Labeling_CategoryNameTooLong_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityLabelingService(db);
        var longName = new string('a', 65);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.LabelAsync(new ActivityLabelingRequest("app", "myapp", null, longName, "all", null), CancellationToken.None));
    }
    [Fact] public async Task Labeling_UnsupportedTargetType_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        SeedCategory(db);
        await db.SaveChangesAsync();
        var svc = new ActivityLabelingService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.LabelAsync(new ActivityLabelingRequest("unknown_type", "x", null, "编程", "all", null), CancellationToken.None));
    }

    // === PcTrackerService extra branches ===
    [Fact] public async Task Tracker_UpsertKeystats_ThrowsOnTransactionInMemory()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var req = new KeystatsUploadRequest("dev1", "2026-07-07", 100, new Dictionary<string, int> { ["a"] = 10 }, 10, 5, 1, 0, 0, 10, 5, 1, 1, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpsertKeystatsAsync(req, CancellationToken.None));
    }
    [Fact] public async Task Tracker_UploadAwEvents_Dedup()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var ts = DayStart.AddHours(6).ToString("O");
        var req = new AwEventsUploadRequest("pc-1", new List<AwEventEntry> { new(ts, 60, "window", "code.exe", "title", null) });
        var c1 = await svc.UploadAwEventsAsync(req, CancellationToken.None);
        Assert.Equal(1, c1);
        var c2 = await svc.UploadAwEventsAsync(req, CancellationToken.None);
        Assert.Equal(0, c2);
    }
    [Fact] public async Task Tracker_UploadCompleteAwEvents_LimitThrows()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var events = Enumerable.Range(0, 501).Select(i => new CompleteAwEventEntry(i, DayStart.AddMinutes(i).ToString("O"), 60, new Dictionary<string, object> { ["app"] = "code" })).ToList();
        var req = new CompleteAwUploadRequest("pc-1", null, new AwBucketDto("b1", "n", "currentwindow", "aw", "h", null, null, null), events);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadCompleteAwEventsAsync(req, CancellationToken.None));
    }
    [Fact] public async Task Tracker_QueryDetail_PaginationAndSort()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        for (int i = 0; i < 5; i++)
            db.Set<KeystatsDailyEntity>().Add(new KeystatsDailyEntity { DeviceId = $"d{i}", SnapshotDate = TestDate.AddDays(-i).Date, KeyPresses = 100 + i, LeftClicks = 10, RightClicks = 5, MiddleClicks = 1, MouseDistance = 10, ScrollDistance = 5, PeakKps = 1, PeakCps = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var res = await svc.QueryDetailAsync(new DetailQueryParams(null, null, null, null, null, null, null, null, "date", "asc", 1, 2), CancellationToken.None);
        Assert.Equal(5, res.TotalCount);
        Assert.Equal(3, res.TotalPages);
        Assert.Equal(2, res.Items.Count);
    }
    [Fact] public async Task Tracker_HeatmapGrid_DayDimension()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetHeatmapGridAsync(TestDate, TestDate, "day", CancellationToken.None);
        Assert.Single(res.Grid);
    }
    [Fact] public async Task Tracker_GetAllCategories_ReturnsOrdered()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<AppCategoryEntity>().Add(new AppCategoryEntity { AppPattern = "code", CategoryName = "编程", Color = "#10b981", Priority = 10, IsBuiltin = false });
        db.Set<AppCategoryEntity>().Add(new AppCategoryEntity { AppPattern = "game", CategoryName = "游戏", Color = "#ef4444", Priority = 1, IsBuiltin = false });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcTrackerService(db);
        var res = await svc.GetAllCategoriesAsync(CancellationToken.None);
        Assert.Equal(2, res.Count);
        Assert.Equal("code", res[0].AppPattern);
    }

    // === Additional coverage: uncovered Services branches ===
    [Fact] public void UrlSanitizer_NullAndNonHttpAndQueryStripped()
    {
        Assert.Null(ActivityUrlSanitizer.Sanitize(null));
        Assert.Null(ActivityUrlSanitizer.Sanitize(" "));
        Assert.Null(ActivityUrlSanitizer.Sanitize("not-a-url"));
        Assert.Null(ActivityUrlSanitizer.Sanitize("ftp://example.com/file"));
        Assert.Equal("https://example.com/a/b", ActivityUrlSanitizer.Sanitize("https://example.com/a/b?x=1#frag"));
    }
    [Fact] public void UrlSanitizer_RedactsSensitiveSegments()
    {
        var dotted = "https://example.com/abcdefgh.ijklmnop.qrstuvwxyz1234/page";
        Assert.Equal("https://example.com/[redacted]/page", ActivityUrlSanitizer.Sanitize(dotted));
        var nonDotted = "https://example.com/AbcDefGh12345678_XyZabcdEFGH/page";
        // Contains upper+lower+digit>=8 or underscore branch -> redacted
        Assert.Equal("https://example.com/[redacted]/page", ActivityUrlSanitizer.Sanitize(nonDotted));
        // short normal segment not redacted
        Assert.Equal("https://example.com/normal/page", ActivityUrlSanitizer.Sanitize("https://example.com/normal/page"));
    }
    [Fact] public void AppNameNormalizer_NormalizesAndTryNormalize()
    {
        Assert.Equal("unknown", AppNameNormalizer.Normalize(null));
        Assert.Equal("unknown", AppNameNormalizer.Normalize("   "));
        Assert.Equal("code", AppNameNormalizer.Normalize("CODE.EXE"));
        Assert.Equal("code", AppNameNormalizer.Normalize("  code.exe  "));
        var longName = new string('a', 300);
        Assert.Equal(256, AppNameNormalizer.Normalize(longName).Length);
        Assert.True(AppNameNormalizer.TryNormalize("chrome", out var n) && n == "chrome");
        Assert.False(AppNameNormalizer.TryNormalize("  ", out var u) && u == "unknown");
    }
    [Fact] public void CategoryLegacyMapper_MapsLegacyAndUnified()
    {
        Assert.Equal(CategoryLegacyMapper.ProgrammingTinkering, CategoryLegacyMapper.MapToUnified("编程"));
        Assert.Equal(CategoryLegacyMapper.Other, CategoryLegacyMapper.MapToUnified("未知分类"));
        Assert.Equal(CategoryLegacyMapper.Other, CategoryLegacyMapper.MapToUnified(null));
        Assert.Equal(CategoryLegacyMapper.Other, CategoryLegacyMapper.MapToUnified(" "));
        // unified name is idempotent
        Assert.Equal(CategoryLegacyMapper.Gaming, CategoryLegacyMapper.MapToUnified(CategoryLegacyMapper.Gaming));
        Assert.Equal(CategoryLegacyMapper.Learning, CategoryLegacyMapper.MapToUnified("技术学习"));
    }
    [Fact] public async Task PcCategoryService_SeedAndTreeAndDictionary()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new PcCategoryService(db);
        await svc.SeedDefaultsAsync(CancellationToken.None);
        var tree = await svc.GetTreeAsync(CancellationToken.None);
        Assert.Equal(7, tree.Count);
        var dict = await svc.GetDictionaryAsync(CancellationToken.None);
        Assert.Equal(7, dict.Count);
        // idempotent seed
        await svc.SeedDefaultsAsync(CancellationToken.None);
        Assert.Equal(7, (await svc.GetTreeAsync(CancellationToken.None)).Count);
    }
    [Fact] public async Task ClassificationSettings_SaveClampsToPreset()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = new ActivityClassificationSettingsService(db);
        var def = await svc.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(5, def.RecommendedMinimumClassificationDurationMinutes);
        var saved = await svc.SaveSettingsAsync(4, CancellationToken.None);
        Assert.Equal(3, saved.RecommendedMinimumClassificationDurationMinutes); // nearest to 4 is 3 (tie-break smaller)
        var saved2 = await svc.SaveSettingsAsync(100, CancellationToken.None);
        Assert.Equal(15, saved2.RecommendedMinimumClassificationDurationMinutes);
    }
    [Fact] public void TimelineSmoothing_MergesShortFallbackBetweenMatchingProject()
    {
        var svc = new ActivityTimelineSmoothingService();
        var baseDate = new DateTimeOffset(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        string F(DateTimeOffset d) => d.ToString("O");
        var items = new List<TimelineItem>
        {
            new(F(baseDate), F(baseDate.AddMinutes(10)), 10, "a.exe", "t", "编程/折腾", "#6B5EE4", "projA", 0.9, "rule", "exp"),
            new(F(baseDate.AddMinutes(10)), F(baseDate.AddMinutes(11)), 1, "b.exe", "t", "其他", "#64748b", null, 0.3, "fallback", "exp"),
            new(F(baseDate.AddMinutes(11)), F(baseDate.AddMinutes(21)), 10, "a.exe", "t", "编程/折腾", "#6B5EE4", "projA", 0.8, "rule", "exp"),
        };
        var smoothed = svc.Smooth(items, 5);
        // short fallback between same category/project should be merged -> 1 item spanning 10:00-10:21
        Assert.Single(smoothed);
        Assert.Equal(21, smoothed[0].DurationMinutes, 0.1);
    }

    private static string GetProd(double p, double d) => p > d ? "productive" : "other";

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
