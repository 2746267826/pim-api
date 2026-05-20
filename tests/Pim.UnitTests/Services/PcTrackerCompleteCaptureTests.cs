using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerCompleteCaptureTests
{
    [Fact]
    public void Model_IncludesCompleteCaptureEntities()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);

        Assert.NotNull(db.Model.FindEntityType(typeof(AwBucketEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(KeystatsSampleEntity)));
    }

    [Fact]
    public void SchemaSql_CreatesAwEventsTableBeforeAlteringIt()
    {
        var createIndex = PcTrackerSchemaInitializer.SchemaSql.IndexOf(
            "CREATE TABLE IF NOT EXISTS pc_aw_events",
            StringComparison.Ordinal);
        var alterIndex = PcTrackerSchemaInitializer.SchemaSql.IndexOf(
            "ALTER TABLE pc_aw_events",
            StringComparison.Ordinal);

        Assert.True(createIndex >= 0, "Schema SQL must create pc_aw_events for partial existing databases.");
        Assert.True(alterIndex >= 0, "Schema SQL must keep ALTER statements for upgrade safety.");
        Assert.True(createIndex < alterIndex, "pc_aw_events must be created before it is altered.");
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_UpsertsByBucketAndSourceEventId()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        var service = new PcTrackerService(db);

        var bucket = new AwBucketDto(
            "aw-watcher-window_DESKTOP",
            null,
            "currentwindow",
            "aw-watcher-window",
            "DESKTOP",
            "2026-05-20T00:00:00+00:00",
            "2026-05-20T05:00:00+00:00",
            new Dictionary<string, object>());

        var first = new CompleteAwUploadRequest(
            "DESKTOP",
            new AwInfoDto("DESKTOP", "v0.13.2", false, "aw-device"),
            bucket,
            new List<CompleteAwEventEntry>
            {
                new(100, "2026-05-20T05:00:00+00:00", 1.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "First"
                })
            });

        var second = first with
        {
            Events = new List<CompleteAwEventEntry>
            {
                new(100, "2026-05-20T05:00:00+00:00", 42.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "First"
                })
            }
        };

        Assert.Equal(1, await service.UploadCompleteAwEventsAsync(first, CancellationToken.None));
        Assert.Equal(0, await service.UploadCompleteAwEventsAsync(second, CancellationToken.None));

        var saved = Assert.Single(db.Set<AwEventEntity>());
        Assert.Equal(42.0, saved.Duration);
        Assert.Equal("aw-watcher-window_DESKTOP", saved.BucketId);
        Assert.Equal(100, saved.SourceEventId);
        Assert.Equal("msedge", saved.AppNameNormalized);
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_DeduplicatesDuplicateSourceIdsWithinSameRequest()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        var service = new PcTrackerService(db);

        var request = new CompleteAwUploadRequest(
            "DESKTOP",
            new AwInfoDto("DESKTOP", "v0.13.2", false, "aw-device"),
            new AwBucketDto(
                "aw-watcher-window_DESKTOP",
                null,
                "currentwindow",
                "aw-watcher-window",
                "DESKTOP",
                "2026-05-20T00:00:00+00:00",
                "2026-05-20T05:00:00+00:00",
                new Dictionary<string, object>()),
            new List<CompleteAwEventEntry>
            {
                new(100, "2026-05-20T05:00:00+00:00", 1.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "First"
                }),
                new(100, "2026-05-20T05:00:00+00:00", 42.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "Updated"
                })
            });

        Assert.Equal(1, await service.UploadCompleteAwEventsAsync(request, CancellationToken.None));

        var saved = Assert.Single(db.Set<AwEventEntity>());
        Assert.Equal(42.0, saved.Duration);
        Assert.Equal(100, saved.SourceEventId);
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_ToleratesExistingDuplicateSourceRows()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        db.Set<AwEventEntity>().AddRange(
            new AwEventEntity
            {
                DeviceId = "DESKTOP",
                BucketId = "aw-watcher-window_DESKTOP",
                SourceEventId = 100,
                Timestamp = DateTimeOffset.Parse("2026-05-20T05:00:00+00:00"),
                Duration = 1.0,
                EventType = "window",
                WindowTitle = "Old One"
            },
            new AwEventEntity
            {
                DeviceId = "DESKTOP",
                BucketId = "aw-watcher-window_DESKTOP",
                SourceEventId = 100,
                Timestamp = DateTimeOffset.Parse("2026-05-20T05:00:00+00:00"),
                Duration = 2.0,
                EventType = "window",
                WindowTitle = "Old Two"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var request = new CompleteAwUploadRequest(
            "DESKTOP",
            new AwInfoDto("DESKTOP", "v0.13.2", false, "aw-device"),
            new AwBucketDto(
                "aw-watcher-window_DESKTOP",
                null,
                "currentwindow",
                "aw-watcher-window",
                "DESKTOP",
                "2026-05-20T00:00:00+00:00",
                "2026-05-20T05:00:00+00:00",
                new Dictionary<string, object>()),
            new List<CompleteAwEventEntry>
            {
                new(100, "2026-05-20T05:00:00+00:00", 42.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "Updated"
                })
            });

        Assert.Equal(0, await service.UploadCompleteAwEventsAsync(request, CancellationToken.None));

        var matching = db.Set<AwEventEntity>()
            .Where(e => e.DeviceId == "DESKTOP"
                && e.BucketId == "aw-watcher-window_DESKTOP"
                && e.SourceEventId == 100)
            .ToList();
        Assert.Equal(2, matching.Count);
        Assert.Contains(matching, e => e.Duration == 42.0 && e.WindowTitle == "Updated");
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_SkipsInvalidEventTimestamp()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        var service = new PcTrackerService(db);
        var request = new CompleteAwUploadRequest(
            "DESKTOP",
            new AwInfoDto("DESKTOP", "v0.13.2", false, "aw-device"),
            new AwBucketDto(
                "aw-watcher-window_DESKTOP",
                null,
                "currentwindow",
                "aw-watcher-window",
                "DESKTOP",
                "2026-05-20T00:00:00+00:00",
                "2026-05-20T05:00:00+00:00",
                new Dictionary<string, object>()),
            new List<CompleteAwEventEntry>
            {
                new(100, "not-a-timestamp", 1.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "Invalid"
                }),
                new(101, "2026-05-20T05:00:00+00:00", 42.0, new Dictionary<string, object>
                {
                    ["app"] = "msedge.exe",
                    ["title"] = "Valid"
                })
            });

        Assert.Equal(1, await service.UploadCompleteAwEventsAsync(request, CancellationToken.None));

        var saved = Assert.Single(db.Set<AwEventEntity>());
        Assert.Equal(101, saved.SourceEventId);
        Assert.Equal(42.0, saved.Duration);
        Assert.Equal("Valid", saved.WindowTitle);
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_RejectsOversizedBatch()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        var service = new PcTrackerService(db);
        var events = Enumerable.Range(0, 501)
            .Select(i => new CompleteAwEventEntry(
                i,
                "2026-05-20T05:00:00+00:00",
                1.0,
                new Dictionary<string, object>()))
            .ToList();
        var request = new CompleteAwUploadRequest(
            "DESKTOP",
            new AwInfoDto("DESKTOP", "v0.13.2", false, "aw-device"),
            new AwBucketDto(
                "aw-watcher-window_DESKTOP",
                null,
                "currentwindow",
                "aw-watcher-window",
                "DESKTOP",
                "2026-05-20T00:00:00+00:00",
                "2026-05-20T05:00:00+00:00",
                new Dictionary<string, object>()),
            events);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UploadCompleteAwEventsAsync(request, CancellationToken.None));
        Assert.Contains("Complete ActivityWatch uploads are limited to 500 events.", ex.Message);
    }
}
