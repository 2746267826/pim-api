using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using System.Globalization;
using System.Text.Json;
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
    public void SchemaSql_IsSafeForExecuteSqlRawFormatting()
    {
        var formattedSql = string.Format(
            CultureInfo.InvariantCulture,
            PcTrackerSchemaInitializer.SchemaSql,
            Array.Empty<object>());

        Assert.Contains("'{}'::jsonb", formattedSql);
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

    [Fact]
    public async Task UpsertKeystatsSampleAsync_StoresRawMinuteSnapshot()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        var service = new PcTrackerService(db);

        var first = new KeystatsSampleUploadRequest(
            "DESKTOP",
            "2026-05-20T13:05:42+08:00",
            "2026-05-20T00:00:00+08:00",
            10,
            new Dictionary<string, int> { ["Space"] = 3 },
            1,
            2,
            3,
            4,
            5,
            123.4,
            56.7,
            8,
            9,
            "123 m",
            "57 px",
            new Dictionary<string, AppStatEntry>
            {
                ["msedge.exe"] = new("msedge.exe", "Microsoft Edge", 10, 1, 2, 0, 0, 0, 56.7)
            });

        var second = first with
        {
            SampledAt = "2026-05-20T13:05:59+08:00",
            KeyPresses = 20
        };

        await service.UpsertKeystatsSampleAsync(first, CancellationToken.None);
        await service.UpsertKeystatsSampleAsync(second, CancellationToken.None);

        var saved = Assert.Single(db.Set<KeystatsSampleEntity>());
        Assert.Equal(DateTimeOffset.Parse("2026-05-20T05:05:00+00:00"), saved.SampledAtUtc);
        Assert.Equal(new DateTime(2026, 5, 20), saved.StatsDate);
        Assert.Equal(480, saved.StatsTimezoneOffsetMinutes);
        Assert.Equal(20, saved.KeyPresses);
        Assert.Contains("Space", saved.KeyCountsJson);
        Assert.Contains("msedge", saved.AppStatsJson);
        Assert.Contains("\"appName\"", saved.AppStatsJson);
        Assert.Contains("\"displayName\"", saved.AppStatsJson);
        Assert.Contains("\"pimDeviceId\"", saved.RawJson);
        Assert.Contains("\"sampledAt\"", saved.RawJson);
        Assert.Contains("\"keyPresses\"", saved.RawJson);
        Assert.Contains("\"peakKPS\"", saved.RawJson);
        Assert.Contains("\"formattedMouseDistance\"", saved.RawJson);
        Assert.Contains("\"appName\"", saved.RawJson);
        Assert.Contains("\"displayName\"", saved.RawJson);
    }

    [Fact]
    public async Task GetSummaryAsync_UsesLatestKeystatsSampleWhenDailySnapshotMissing()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        db.Set<KeystatsSampleEntity>().AddRange(
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:55:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 10,
                LeftClicks = 1,
                KeyCountsJson = "{\"A\":10}",
                AppStatsJson = "{\"old.exe\":{\"appName\":\"old.exe\",\"displayName\":\"Old\",\"keyPresses\":10,\"leftClicks\":1,\"rightClicks\":0,\"middleClicks\":0,\"sideBackClicks\":0,\"sideForwardClicks\":0,\"scrollDistance\":1}}",
                RawJson = "{}"
            },
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:56:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 99,
                LeftClicks = 2,
                RightClicks = 3,
                MiddleClicks = 1,
                SideBackClicks = 1,
                MouseDistance = 123.4,
                ScrollDistance = 56.7,
                PeakKps = 8,
                PeakCps = 9,
                KeyCountsJson = "{\"A\":33,\"B\":66}",
                AppStatsJson = "{\"msedge.exe\":{\"appName\":\"msedge.exe\",\"displayName\":\"Microsoft Edge\",\"keyPresses\":99,\"leftClicks\":2,\"rightClicks\":3,\"middleClicks\":1,\"sideBackClicks\":1,\"sideForwardClicks\":0,\"scrollDistance\":56.7}}",
                RawJson = "{}"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var summary = await service.GetSummaryAsync(new DateTime(2026, 5, 20), CancellationToken.None);

        Assert.NotNull(summary.Keystats);
        Assert.Equal("2026-05-20", summary.Keystats.Date);
        Assert.Equal(99, summary.Keystats.KeyPresses);
        Assert.Equal(7, summary.Keystats.TotalClicks);
        Assert.Equal(123.4, summary.Keystats.MouseDistance);
        Assert.Equal(56.7, summary.Keystats.ScrollDistance);
        Assert.Equal(8, summary.Keystats.PeakKps);
        Assert.Equal(9, summary.Keystats.PeakCps);
        Assert.Collection(
            summary.Keystats.TopKeys,
            first =>
            {
                Assert.Equal("B", first.KeyName);
                Assert.Equal(66, first.Count);
                Assert.Equal(66.0 / 99, first.Share);
            },
            second =>
            {
                Assert.Equal("A", second.KeyName);
                Assert.Equal(33, second.Count);
                Assert.Equal(33.0 / 99, second.Share);
            });

        var app = Assert.Single(summary.AppRanking);
        Assert.Equal("msedge.exe", app.AppName);
        Assert.Equal("Microsoft Edge", app.DisplayName);
        Assert.Equal(99, app.KeyPresses);
        Assert.Equal(7, app.TotalClicks);
        Assert.Equal(56.7, app.ScrollDistance);
        Assert.Equal(1, app.Share);
        Assert.Equal(99, summary.Metrics?.TotalKeyPresses);
        Assert.Equal(7, summary.Metrics?.TotalClicks);
    }

    [Fact]
    public async Task QueryCompleteDetailAsync_ReturnsWindowAndInputMinuteRecords()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = DateTimeOffset.Parse("2026-05-20T05:55:00+00:00"),
            Duration = 30,
            EventType = "window",
            AppName = "msedge.exe",
            AppNameNormalized = "msedge",
            WindowTitle = "Docs",
            DataJson = "{\"app\":\"msedge.exe\",\"title\":\"Docs\"}"
        });
        db.Set<KeystatsSampleEntity>().AddRange(
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:55:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 10,
                KeyCountsJson = "{\"A\":10}",
                RawJson = "{\"keyPresses\":10}"
            },
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:56:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 15,
                KeyCountsJson = "{\"A\":15}",
                RawJson = "{\"keyPresses\":15}"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var result = await service.QueryCompleteDetailAsync(
            new DetailQueryParams(
                "2026-05-20",
                "2026-05-20",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                1,
                20),
            CancellationToken.None);

        var window = Assert.Single(result.Items, x => x.RecordType == "window");
        Assert.Equal("DESKTOP", window.DeviceId);
        Assert.Equal("msedge.exe", window.AppName);
        Assert.Equal("msedge", window.DisplayName);
        Assert.Equal("Docs", window.Title);
        Assert.Equal("2026-05-20T05:55:00.0000000+00:00", window.Start);
        Assert.Equal("2026-05-20T05:55:30.0000000+00:00", window.End);
        Assert.Equal(30, window.DurationSeconds);
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(window.Raw).ValueKind);
        Assert.Contains("\"raw\":{\"app\":\"msedge.exe\",\"title\":\"Docs\"}", JsonSerializer.Serialize(window, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var inputMinute = Assert.Single(result.Items, x => x.RecordType == "input-minute");
        Assert.Equal("DESKTOP", inputMinute.DeviceId);
        Assert.Equal(5, inputMinute.KeyPresses);
        Assert.Equal("2026-05-20T05:55:00.0000000+00:00", inputMinute.Start);
        Assert.Equal("2026-05-20T05:56:00.0000000+00:00", inputMinute.End);
        Assert.Equal(60, inputMinute.DurationSeconds);
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(inputMinute.Raw).ValueKind);
        Assert.Contains("\"raw\":{\"keyPresses\":15}", JsonSerializer.Serialize(inputMinute, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void CompleteAwUploadRequest_BindsActivityWatchSnakeCaseFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = """
            {
              "pimDeviceId": "DESKTOP",
              "awInfo": {
                "hostname": "DESKTOP",
                "version": "v0.13.2",
                "testing": false,
                "device_id": "aw-device"
              },
              "bucket": {
                "id": "aw-watcher-window_DESKTOP",
                "name": null,
                "type": "currentwindow",
                "client": "aw-watcher-window",
                "hostname": "DESKTOP",
                "created": "2026-05-20T00:00:00+00:00",
                "last_updated": "2026-05-20T05:00:00+00:00",
                "data": {}
              },
              "events": []
            }
            """;

        var request = JsonSerializer.Deserialize<CompleteAwUploadRequest>(json, options);

        Assert.NotNull(request);
        Assert.Equal("aw-device", request.AwInfo?.DeviceId);
        Assert.Equal("2026-05-20T05:00:00+00:00", request.Bucket.LastUpdated);

        var serialized = JsonSerializer.Serialize(request, options);
        using var serializedDocument = JsonDocument.Parse(serialized);
        Assert.Equal("aw-device", serializedDocument.RootElement.GetProperty("awInfo").GetProperty("device_id").GetString());
        Assert.Equal("2026-05-20T05:00:00+00:00", serializedDocument.RootElement.GetProperty("bucket").GetProperty("last_updated").GetString());
    }

    [Fact]
    public async Task QueryCompleteDetailAsync_LeavesKeyCountsEmptyForResetDelta()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        db.Set<KeystatsSampleEntity>().AddRange(
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:55:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 100,
                KeyCountsJson = "{\"A\":1}",
                RawJson = "{}"
            },
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:56:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 10,
                KeyCountsJson = "{\"A\":2}",
                RawJson = "{}"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var result = await service.QueryCompleteDetailAsync(MakeDetailQuery(), CancellationToken.None);

        var inputMinute = Assert.Single(result.Items);
        Assert.Equal(0, inputMinute.KeyPresses);
        Assert.NotNull(inputMinute.KeyCounts);
        Assert.Empty(inputMinute.KeyCounts);
    }

    [Fact]
    public async Task QueryCompleteDetailAsync_UsesElapsedSecondsForGapDeltas()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);
        db.Set<KeystatsSampleEntity>().AddRange(
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T05:50:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 10,
                KeyCountsJson = "{}",
                RawJson = "{}"
            },
            new KeystatsSampleEntity
            {
                PimDeviceId = "DESKTOP",
                SampledAtUtc = DateTimeOffset.Parse("2026-05-20T06:00:00+00:00"),
                StatsDate = new DateTime(2026, 5, 20),
                KeyPresses = 15,
                KeyCountsJson = "{}",
                RawJson = "{}"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var result = await service.QueryCompleteDetailAsync(MakeDetailQuery(), CancellationToken.None);

        var inputMinute = Assert.Single(result.Items);
        Assert.Equal(5, inputMinute.KeyPresses);
        Assert.Equal(600, inputMinute.DurationSeconds);
        Assert.Equal("2026-05-20T05:50:00.0000000+00:00", inputMinute.Start);
        Assert.Equal("2026-05-20T06:00:00.0000000+00:00", inputMinute.End);
    }

    [Fact]
    public async Task QueryCompleteDetailAsync_NormalizesStartAndEndToUtcForSorting()
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
                Timestamp = DateTimeOffset.Parse("2026-05-20T13:00:00+08:00"),
                Duration = 60,
                EventType = "window",
                AppName = "early.exe",
                DataJson = "{}"
            },
            new AwEventEntity
            {
                DeviceId = "DESKTOP",
                Timestamp = DateTimeOffset.Parse("2026-05-20T05:30:00+00:00"),
                Duration = 60,
                EventType = "window",
                AppName = "late.exe",
                DataJson = "{}"
            });
        await db.SaveChangesAsync();

        var service = new PcTrackerService(db);
        var result = await service.QueryCompleteDetailAsync(MakeDetailQuery(), CancellationToken.None);

        Assert.Collection(
            result.Items,
            first =>
            {
                Assert.Equal("late.exe", first.AppName);
                Assert.Equal("2026-05-20T05:30:00.0000000+00:00", first.Start);
                Assert.Equal("2026-05-20T05:31:00.0000000+00:00", first.End);
            },
            second =>
            {
                Assert.Equal("early.exe", second.AppName);
                Assert.Equal("2026-05-20T05:00:00.0000000+00:00", second.Start);
                Assert.Equal("2026-05-20T05:01:00.0000000+00:00", second.End);
            });
    }

    [Fact]
    public async Task UploadCompleteAwEventsAsync_StoresWebTabCurrentAsWebEvent()
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
                "aw-watcher-web-edge_DESKTOP",
                null,
                "web.tab.current",
                "aw-client-web",
                "DESKTOP",
                "2026-05-20T00:00:00+00:00",
                "2026-05-20T05:00:00+00:00",
                new Dictionary<string, object>()),
            new List<CompleteAwEventEntry>
            {
                new(200, "2026-05-20T05:00:00+00:00", 8.0, new Dictionary<string, object>
                {
                    ["url"] = "https://docs.activitywatch.net/en/latest/api/rest.html",
                    ["title"] = "REST API",
                    ["audible"] = false,
                    ["incognito"] = false,
                    ["tabCount"] = 12
                })
            });

        Assert.Equal(1, await service.UploadCompleteAwEventsAsync(request, CancellationToken.None));

        var saved = Assert.Single(db.Set<AwEventEntity>());
        Assert.Equal("web", saved.EventType);
        Assert.Equal("web.tab.current", saved.BucketType);
        Assert.Null(saved.AppName);
        Assert.Equal("REST API", saved.WindowTitle);
        Assert.Contains("docs.activitywatch.net", saved.DataJson);
    }

    private static DetailQueryParams MakeDetailQuery()
    {
        return new DetailQueryParams(
            "2026-05-20",
            "2026-05-20",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            20);
    }
}
