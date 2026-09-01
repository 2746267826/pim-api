using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerEventDedupTests
{
    private static readonly DateTimeOffset FixedTs = new(2026, 8, 20, 6, 30, 0, TimeSpan.Zero);

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static PcTrackerService CreateService(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static TrackerEventsUploadRequest Request(string deviceId, params TrackerEventDto[] events)
        => new(deviceId, events.ToList());

    private static TrackerEventDto Event(string? browser = null, string? instanceId = null)
        => new(
            Timestamp: FixedTs.ToString("O"),
            Duration: 10,
            EventType: "window",
            ExePath: @"C:\Program Files\app\app.exe",
            AppName: "App",
            DisplayName: "App",
            WindowTitle: "Title",
            CommandLine: null,
            IsIdle: false,
            IsMediaActive: false,
            Url: "https://example.com",
            Domain: "example.com",
            PagePath: null,
            Audible: null,
            Incognito: null,
            TabCount: null,
            PageVisitCount: 0,
            PageVisitDuration: 0,
            RawJson: null,
            Date: "2026-08-20",
            Browser: browser,
            InstanceId: instanceId);

    [Fact]
    public async Task Upload_OldFormatNullBrowserInstance_DeduplicatesAcrossRequests()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event()), CancellationToken.None));
        Assert.Equal(0, await service.UploadTrackerEventsAsync(Request("dev-a", Event()), CancellationToken.None));

        Assert.Single(db.Set<TrackerEventEntity>());
    }

    [Fact]
    public async Task Upload_SameInstanceId_DeduplicatesAcrossRequests()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", "ext_123")), CancellationToken.None));
        Assert.Equal(0, await service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", "ext_123")), CancellationToken.None));

        Assert.Single(db.Set<TrackerEventEntity>());
    }

    [Fact]
    public async Task Upload_DifferentInstanceId_StoresBoth()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", "ext_123")), CancellationToken.None));
        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", "ext_456")), CancellationToken.None));

        Assert.Equal(2, db.Set<TrackerEventEntity>().Count());
    }

    [Fact]
    public async Task Upload_OldFormatAndSameInstanceIdAreSeparate()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event()), CancellationToken.None));
        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", "ext_123")), CancellationToken.None));

        Assert.Equal(2, db.Set<TrackerEventEntity>().Count());
    }

    [Fact]
    public async Task Upload_NormalizesBrowserLowercaseAndWhitespace()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("  CHROME  ", "ext_1")), CancellationToken.None));
        var saved = Assert.Single(db.Set<TrackerEventEntity>());
        Assert.Equal("chrome", saved.Browser);
        Assert.Equal("ext_1", saved.InstanceId);
    }

    [Fact]
    public async Task Upload_UnknownBrowserMapsToOther()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("vivaldi", "ext_1")), CancellationToken.None));
        var saved = Assert.Single(db.Set<TrackerEventEntity>());
        Assert.Equal("other", saved.Browser);
    }

    [Fact]
    public async Task Upload_BlankBrowserAndInstanceIdBecomeNull()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("   ", "   ")), CancellationToken.None));
        var saved = Assert.Single(db.Set<TrackerEventEntity>());
        Assert.Null(saved.Browser);
        Assert.Null(saved.InstanceId);
    }

    [Fact]
    public async Task Upload_InstanceIdIsTrimmed()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(1, await service.UploadTrackerEventsAsync(Request("dev-a", Event("edge", "  abc_123  ")), CancellationToken.None));
        var saved = Assert.Single(db.Set<TrackerEventEntity>());
        Assert.Equal("abc_123", saved.InstanceId);
    }

    [Fact]
    public async Task Upload_BrowserTooLong_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadTrackerEventsAsync(Request("dev-a", Event(new string('x', 17), "ext_1")), CancellationToken.None));
        Assert.Contains("Browser too long", ex.Message);
        Assert.Empty(db.Set<TrackerEventEntity>());
    }

    [Fact]
    public async Task Upload_InstanceIdTooLong_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadTrackerEventsAsync(Request("dev-a", Event("chrome", new string('i', 129))), CancellationToken.None));
        Assert.Contains("InstanceId too long", ex.Message);
        Assert.Empty(db.Set<TrackerEventEntity>());
    }

    [Fact]
    public void SchemaSql_DedupIndexUsesCoalesceForNullableColumns()
    {
        var sql = PcTrackerSchemaInitializer.SchemaSql;

        Assert.Contains(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_tracker_events_dedup ON pc_tracker_events(device_id, timestamp, duration, event_type, app_name, COALESCE(browser,''), COALESCE(instance_id,''))",
            sql);

        Assert.Contains("ALTER TABLE pc_tracker_events ADD COLUMN IF NOT EXISTS browser VARCHAR(16);", sql);
        Assert.Contains("ALTER TABLE pc_tracker_events ADD COLUMN IF NOT EXISTS instance_id VARCHAR(128);", sql);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_tracker_events_browser ON pc_tracker_events(browser);", sql);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_tracker_events_instance ON pc_tracker_events(instance_id);", sql);
    }

    [Fact]
    public void SchemaSql_IsSafeForExecuteSqlRawFormatting()
    {
        var formatted = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            PcTrackerSchemaInitializer.SchemaSql,
            Array.Empty<object>());

        Assert.Contains("COALESCE(browser,'')", formatted);
    }

    [Fact]
    public void Model_DoesNotDeclarePlainUniqueDedupIndex()
    {
        // 去重唯一索引由 SchemaInitializer 以 COALESCE 表达式索引维护。若在 EF 模型中
        // 声明普通唯一索引，PostgreSQL 会因 NULL 互不相等导致老数据去重失效，此处断言
        // 防止该缺陷被重新引入。
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new PimDbContext(options);

        var entity = db.Model.FindEntityType(typeof(TrackerEventEntity));
        Assert.NotNull(entity);
        var dedup = entity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "ux_tracker_events_dedup");
        Assert.Null(dedup);
    }
}
