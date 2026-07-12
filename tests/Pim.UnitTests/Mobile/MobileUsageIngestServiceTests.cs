using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileUsageIngestServiceTests
{
    [Fact]
    public async Task IngestAsync_ReturnsStableResultForEverySentItem()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = UploadRequest("batch-items", "Messages") with
        {
            Apps = [],
            Events =
            [
                Event("event-1", "2026-07-06T08:05:00Z"),
                Event("event-2", "2026-07-06T08:05:00Z")
            ],
            FallbackSummaries = []
        };

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(2, result.ItemResults.Count);
        Assert.Equal("accepted", result.ItemResults.Single(x => x.ClientItemKey == "event-1").Outcome);
        Assert.Equal("skipped", result.ItemResults.Single(x => x.ClientItemKey == "event-2").Outcome);
        Assert.Equal(result.ItemResults.Count(x => x.Outcome == "accepted"), result.AcceptedCount);
        Assert.Equal(result.ItemResults.Count(x => x.Outcome == "skipped"), result.SkippedCount);
    }

    [Fact]
    public async Task IngestAsync_ReturnsOneResultForEveryEntityType()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var source = UploadRequest("batch-all-types", "Messages");
        var request = source with
        {
            Apps = [source.Apps.Single() with { ClientItemKey = "app-1" }],
            Events = [Event("event-1", "2026-07-06T08:05:00Z")],
            FallbackSummaries =
            [
                source.FallbackSummaries.Single() with { ClientItemKey = "summary-1" }
            ]
        };

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(3, result.ItemResults.Count);
        Assert.Contains(result.ItemResults, x => x.ClientItemKey == "app-1" && x.EntityType == "app-metadata");
        Assert.Contains(result.ItemResults, x => x.ClientItemKey == "event-1" && x.EntityType == "usage-event");
        Assert.Contains(result.ItemResults, x => x.ClientItemKey == "summary-1" && x.EntityType == "usage-summary");
        Assert.All(result.ItemResults, x =>
        {
            Assert.Equal("accepted", x.Outcome);
            Assert.Equal("accepted", x.Code);
        });
        Assert.Equal(3, result.AcceptedCount);
    }

    [Fact]
    public async Task IngestAsync_RepeatedBatchReturnsPersistedItemResults()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = UploadRequest("batch-repeat", "Messages");

        var first = await service.IngestAsync(request, CancellationToken.None);
        var second = await service.IngestAsync(request, CancellationToken.None);
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        var envelope = JsonSerializer.Deserialize<MobileSyncBatchEnvelope>(batch.ErrorJson)!;

        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(first.ItemResults), JsonSerializer.Serialize(second.ItemResults));
        Assert.Equal(JsonSerializer.Serialize(first.ItemResults), JsonSerializer.Serialize(envelope.ItemResults));
        Assert.Empty(envelope.BatchErrors);
    }

    [Fact]
    public async Task IngestAsync_RecoversPersistedWinnerAfterConcurrentBatchInsert()
    {
        MobileTestHelpers.RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mobile-race-{Guid.NewGuid()}")
            .Options;
        var winnerResults = new[]
        {
            new MobileIngestItemResult(
                "winner-event",
                "usage-event",
                "accepted",
                "accepted",
                "Accepted.")
        };
        await using var db = new BatchInsertRacePimDbContext(options, winnerResults);
        var service = CreateService(db);

        var result = await service.IngestAsync(
            UploadRequest("batch-race", "Messages"),
            CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(winnerResults), JsonSerializer.Serialize(result.ItemResults));
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, await db.Set<MobileSyncBatchEntity>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_ExecutionStrategyRetryRechecksPersistedBatch()
    {
        MobileTestHelpers.RegisterMobileModule();
        var strategyState = new RetryExecutionStrategyState();
        RetryOnceExecutionStrategyFactory.CurrentState.Value = strategyState;
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mobile-strategy-{Guid.NewGuid()}")
            .ReplaceService<IExecutionStrategyFactory, RetryOnceExecutionStrategyFactory>()
            .Options;
        try
        {
            await using var db = new PersistBatchThenThrowPimDbContext(options);
            var service = CreateService(db);
            var request = UploadRequest("batch-strategy-retry", "Messages");

            var result = await service.IngestAsync(request, CancellationToken.None);

            Assert.Equal(1, strategyState.RetryableExceptionsObserved);
            Assert.Equal(1, db.TransientFailuresThrown);
            var persistedWinner = Assert.Single(result.ItemResults);
            Assert.Equal("persisted-strategy-winner", persistedWinner.ClientItemKey);
            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(1, await db.Set<MobileSyncBatchEntity>().CountAsync());
            Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileUsageSummaryEntity>().CountAsync());
            Assert.Equal(1, await db.Set<MobileAppCatalogEntity>().CountAsync());
        }
        finally
        {
            RetryOnceExecutionStrategyFactory.CurrentState.Value = null;
        }
    }

    [Fact]
    public async Task IngestAsync_LegacyItemsReceiveDeterministicNonEmptyKeys()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);

        var first = await service.IngestAsync(UploadRequest("batch-legacy-1", "Messages"), CancellationToken.None);
        var second = await service.IngestAsync(UploadRequest("batch-legacy-2", "Messages"), CancellationToken.None);

        Assert.Equal(4, first.ItemResults.Count);
        Assert.All(first.ItemResults, item => Assert.False(string.IsNullOrWhiteSpace(item.ClientItemKey)));
        Assert.Equal(
            first.ItemResults.Select(item => item.ClientItemKey).OrderBy(key => key),
            second.ItemResults.Select(item => item.ClientItemKey).OrderBy(key => key));
    }

    [Fact]
    public async Task IngestAsync_LegacyBatchDoesNotFabricateItemResults()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = UploadRequest("batch-legacy-envelope", "Messages");
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            AcceptedCount = 2,
            FailedCount = 1,
            ErrorJson = "{}"
        });
        await db.SaveChangesAsync();

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(result.ItemResults);
    }

    [Fact]
    public async Task IngestAsync_RejectsInvalidItemWithStableCode()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = UploadRequest("batch-invalid", "Messages") with
        {
            Apps = [],
            Events =
            [
                new MobileUsageEventDto(
                    "",
                    "USER_INTERACTION",
                    DateTimeOffset.Parse("2026-07-06T08:05:00Z"),
                    null,
                    DateTimeOffset.Parse("2026-07-06T08:05:00Z"),
                    "{}",
                    "invalid-event")
            ],
            FallbackSummaries = []
        };

        var result = await service.IngestAsync(request, CancellationToken.None);

        var item = Assert.Single(result.ItemResults);
        Assert.Equal("rejected", item.Outcome);
        Assert.Equal("invalid-package-name", item.Code);
        Assert.Equal(1, result.RejectedCount);
        Assert.Empty(await db.Set<MobileUsageEventEntity>().ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_RejectsEveryDatabaseConstrainedAppField()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var source = UploadRequest("batch-invalid-app-fields", "Messages");
        var app = source.Apps.Single();
        var request = source with
        {
            Apps =
            [
                app with
                {
                    PackageName = "com.example.version",
                    VersionName = new string('v', 129),
                    ClientItemKey = "invalid-version-name"
                },
                app with
                {
                    PackageName = "com.example.category",
                    CategoryName = new string('c', 129),
                    ClientItemKey = "invalid-category-name"
                },
                app with
                {
                    PackageName = "com.example.installer",
                    InstallerPackageName = new string('i', 257),
                    ClientItemKey = "invalid-installer-package"
                }
            ],
            Events = [],
            FallbackSummaries = []
        };

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(3, result.RejectedCount);
        Assert.Equal("invalid-version-name", result.ItemResults.Single(
            item => item.ClientItemKey == "invalid-version-name").Code);
        Assert.Equal("invalid-category-name", result.ItemResults.Single(
            item => item.ClientItemKey == "invalid-category-name").Code);
        Assert.Equal("invalid-installer-package", result.ItemResults.Single(
            item => item.ClientItemKey == "invalid-installer-package").Code);
        Assert.Empty(await db.Set<MobileAppCatalogEntity>().ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_IsIdempotentAndStoresFallbackSummariesSeparately()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var request = UploadRequest("batch-1", "Messages");

        var first = await service.IngestAsync(request, CancellationToken.None);
        var second = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
        Assert.Equal(1, await db.Set<MobileUsageSummaryEntity>().CountAsync());
        Assert.Equal(1, await db.Set<MobileAppCatalogEntity>().CountAsync());
        Assert.Equal(4, first.AcceptedCount);
        Assert.Equal(0, first.FailedCount);
        Assert.Equal(first.ItemResults, second.ItemResults);
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(2, batch.AcceptedCount);
    }

    [Fact]
    public async Task IngestAsync_DoesNotPersistAcknowledgementWhenDerivedWorkFails()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var staleService = new MobileAppCatalogOverrideService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")),
            staleService);
        var source = UploadRequest("batch-derived-failure", "Messages");
        var request = source with { SourceWindowEndUtc = source.SourceWindowStartUtc };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync(request, CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<MobileSyncBatchEntity>().ToListAsync());
    }

    [Fact]
    public async Task IngestAsync_UpsertsAppMetadataByPackageName()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        await service.IngestAsync(UploadRequest("batch-1", "Messages"), CancellationToken.None);
        await service.IngestAsync(UploadRequest("batch-2", "Messages Beta"), CancellationToken.None);

        var app = await db.Set<MobileAppCatalogEntity>().SingleAsync();
        Assert.Equal("com.example.messages", app.PackageName);
        Assert.Equal("Messages Beta", app.DisplayName);
    }

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsAcrossBatches()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var firstRequest = UploadRequest("batch-1", "Messages") with { Apps = [], FallbackSummaries = [] };
        var secondRequest = UploadRequest("batch-2", "Messages") with { Apps = [], FallbackSummaries = [] };
        var first = await service.IngestAsync(firstRequest, CancellationToken.None);
        var second = await service.IngestAsync(secondRequest, CancellationToken.None);

        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.Equal(2, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsWithNullClassName()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var request = UploadRequest(
            "batch-null-class",
            "Messages",
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    start.AddMinutes(5),
                    null,
                    start.AddMinutes(6),
                    "{\"event\":\"tap\"}"),
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    start.AddMinutes(5),
                    null,
                    start.AddMinutes(6),
                    "{\"event\":\"tap\"}")
            ]) with { Apps = [], FallbackSummaries = [] };

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        var usageEvent = Assert.Single(await db.Set<MobileUsageEventEntity>().ToListAsync());
        Assert.Equal(string.Empty, usageEvent.ClassName);
    }

    [Fact]
    public async Task IngestAsync_SkipsDuplicateEventsWhenExistingClassNameIsNull()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.messages",
            EventType = "USER_INTERACTION",
            EventTimestampUtc = start.AddMinutes(5),
            ClassName = null,
            SourceWindowStartUtc = start,
            SourceWindowEndUtc = start.AddHours(1),
            CollectedAtUtc = start.AddMinutes(6),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = start.AddMinutes(6)
        });
        await db.SaveChangesAsync();

        var result = await service.IngestAsync(
            UploadRequest(
                "batch-existing-null-class",
                "Messages",
                [
                    new MobileUsageEventDto(
                        "com.example.messages",
                        "USER_INTERACTION",
                        start.AddMinutes(5),
                        null,
                        start.AddMinutes(6),
                        "{\"event\":\"tap\"}")
                ]) with { Apps = [], FallbackSummaries = [] },
            CancellationToken.None);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().CountAsync());
    }

    [Fact]
    public async Task IngestAsync_ExistingBatchReturnsPersistedAckWithoutDerivedReprocessing()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        var staleService = new MobileAppCatalogOverrideService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(now));
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(now),
            staleService);

        var request = UploadRequest("batch-existing", "Messages");
        var persistedResults = new[]
        {
            new MobileIngestItemResult(
                "persisted-event",
                "usage-event",
                "accepted",
                "accepted",
                "Accepted.")
        };
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            AcceptedCount = 2,
            FailedCount = 0,
            Status = "completed",
            ErrorJson = JsonSerializer.Serialize(new MobileSyncBatchEnvelope(1, persistedResults, [])),
            CreatedAt = now,
            CompletedAtUtc = now
        });
        foreach (var usageEvent in request.Events)
        {
            db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = request.DeviceId,
                PackageName = usageEvent.PackageName,
                EventType = usageEvent.EventType,
                EventTimestampUtc = usageEvent.EventTimestampUtc,
                ClassName = usageEvent.ClassName,
                SourceWindowStartUtc = request.WindowStartUtc,
                SourceWindowEndUtc = request.WindowEndUtc,
                CollectedAtUtc = usageEvent.CollectedAtUtc,
                RawJson = usageEvent.RawJson,
                QualityFlagsJson = "[]",
                CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
            });
        }
        db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            Granularity = "hour",
            BucketStartUtc = request.WindowStartUtc,
            BucketEndUtc = request.WindowEndUtc,
            PackageName = "com.example.messages",
            DisplayName = "Messages",
            LifeCategory = MobileLifeCategories.Social,
            ForegroundSeconds = 60,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(persistedResults), JsonSerializer.Serialize(result.ItemResults));
        Assert.Empty(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.False((await db.Set<MobileUsageAggregateEntity>().SingleAsync()).IsStale);
    }

    [Fact]
    public async Task IngestAsync_MarksAffectedAnalyticsStaleWhenServiceIsAvailable()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var staleService = new MobileAppCatalogOverrideService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(now));
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(now),
            staleService);
        var request = UploadRequest("batch-stale", "Messages");
        db.Set<MobileUsageAggregateEntity>().Add(new MobileUsageAggregateEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            Granularity = "hour",
            BucketStartUtc = request.WindowStartUtc,
            BucketEndUtc = request.WindowEndUtc,
            PackageName = "com.example.messages",
            DisplayName = "Messages",
            LifeCategory = MobileLifeCategories.Social,
            ForegroundSeconds = 60,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Set<MobileTimelineBlockEntity>().Add(new MobileTimelineBlockEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = request.DeviceId,
            StartUtc = request.WindowStartUtc,
            EndUtc = request.WindowEndUtc,
            LocalDate = "2026-07-06",
            LifeCategory = MobileLifeCategories.Social,
            ForegroundSeconds = 60,
            SessionCount = 1,
            AppCount = 1,
            TopAppsJson = "[{\"packageName\":\"com.example.messages\",\"displayName\":\"Messages\",\"foregroundSeconds\":60}]",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        await service.IngestAsync(request, CancellationToken.None);

        Assert.True(await db.Set<MobileUsageAggregateEntity>().AnyAsync(row => row.IsStale));
        Assert.True(await db.Set<MobileTimelineBlockEntity>().AnyAsync(row => row.IsStale));
    }

    private static MobileUsageEventsUploadRequest UploadRequest(
        string batchId,
        string appName,
        IReadOnlyList<MobileUsageEventDto>? events = null)
    {
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");

        return new MobileUsageEventsUploadRequest(
            "android-main",
            batchId,
            start,
            end,
            [
                new MobileAppMetadataDto(
                    "com.example.messages",
                    appName,
                    "1.2.3",
                    123,
                    false,
                    "communication",
                    "com.android.vending",
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                    "{}")
            ],
            events ??
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "MOVE_TO_FOREGROUND",
                    start.AddMinutes(5),
                    "MainActivity",
                    start.AddMinutes(6),
                    "{\"event\":\"fg\"}"),
                new MobileUsageEventDto(
                    "com.example.messages",
                    "MOVE_TO_BACKGROUND",
                    start.AddMinutes(25),
                    "MainActivity",
                    start.AddMinutes(26),
                    "{\"event\":\"bg\"}")
            ],
            [
                new MobileUsageSummaryDto(
                    "com.example.messages",
                    start,
                    end,
                    1_200_000,
                    start.AddMinutes(25),
                    "usage-stats-fallback",
                    "{\"summary\":true}")
            ]);
    }

    private static MobileUsageIngestService CreateService(PimDbContext db) => new(
        db,
        MobileTestHelpers.CurrentUser(),
        new MobileSessionInterpreter(db),
        MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

    private static MobileUsageEventDto Event(string clientItemKey, string timestamp)
    {
        var occurredAt = DateTimeOffset.Parse(timestamp);
        return new MobileUsageEventDto(
            "com.example.messages",
            "USER_INTERACTION",
            occurredAt,
            null,
            occurredAt,
            "{}",
            clientItemKey);
    }

    private sealed class BatchInsertRacePimDbContext : PimDbContext
    {
        private readonly DbContextOptions<PimDbContext> _options;
        private readonly IReadOnlyList<MobileIngestItemResult> _winnerResults;
        private bool _hasThrown;

        public BatchInsertRacePimDbContext(
            DbContextOptions<PimDbContext> options,
            IReadOnlyList<MobileIngestItemResult> winnerResults)
            : base(options)
        {
            _options = options;
            _winnerResults = winnerResults;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pendingBatch = ChangeTracker.Entries<MobileSyncBatchEntity>()
                .SingleOrDefault(entry => entry.State == EntityState.Added);
            if (!_hasThrown && pendingBatch is not null)
            {
                _hasThrown = true;
                var attempted = pendingBatch.Entity;
                await using var competingDb = new PimDbContext(_options);
                competingDb.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
                {
                    UserId = attempted.UserId,
                    DeviceId = attempted.DeviceId,
                    BatchId = attempted.BatchId,
                    WindowStartUtc = attempted.WindowStartUtc,
                    WindowEndUtc = attempted.WindowEndUtc,
                    AcceptedCount = 1,
                    FailedCount = 0,
                    Status = "completed",
                    ErrorJson = JsonSerializer.Serialize(
                        new MobileSyncBatchEnvelope(1, _winnerResults, [])),
                    CreatedAt = attempted.CreatedAt,
                    CompletedAtUtc = attempted.CompletedAtUtc
                });
                await competingDb.SaveChangesAsync(cancellationToken);

                throw new DbUpdateException("Simulated concurrent mobile batch insert.");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class PersistBatchThenThrowPimDbContext : PimDbContext
    {
        private bool _hasThrown;

        public PersistBatchThenThrowPimDbContext(DbContextOptions<PimDbContext> options)
            : base(options)
        {
        }

        public int TransientFailuresThrown { get; private set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pendingBatch = !_hasThrown
                ? ChangeTracker.Entries<MobileSyncBatchEntity>()
                    .SingleOrDefault(entry => entry.State == EntityState.Added)
                    ?.Entity
                : null;
            var saved = await base.SaveChangesAsync(cancellationToken);
            if (pendingBatch is not null)
            {
                pendingBatch.AcceptedCount = 1;
                pendingBatch.ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                    [
                        new MobileIngestItemResult(
                            "persisted-strategy-winner",
                            "usage-event",
                            "accepted",
                            "accepted",
                            "Accepted.")
                    ],
                    []);
                await base.SaveChangesAsync(cancellationToken);
                _hasThrown = true;
                TransientFailuresThrown++;
                throw new RetryableIngestTestException();
            }

            return saved;
        }
    }

    private sealed class RetryExecutionStrategyState
    {
        public int RetryableExceptionsObserved { get; set; }
    }

    private sealed class RetryOnceExecutionStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies _dependencies;

        public static AsyncLocal<RetryExecutionStrategyState?> CurrentState { get; } = new();

        public RetryOnceExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public IExecutionStrategy Create()
            => new RetryOnceExecutionStrategy(
                _dependencies,
                CurrentState.Value ?? throw new InvalidOperationException("Retry strategy state is not configured."));
    }

    private sealed class RetryOnceExecutionStrategy : ExecutionStrategy
    {
        private readonly RetryExecutionStrategyState _state;

        public RetryOnceExecutionStrategy(
            ExecutionStrategyDependencies dependencies,
            RetryExecutionStrategyState state)
            : base(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
        {
            _state = state;
        }

        protected override bool ShouldRetryOn(Exception exception)
        {
            if (exception is not RetryableIngestTestException)
                return false;

            _state.RetryableExceptionsObserved++;
            return true;
        }
    }

    private sealed class RetryableIngestTestException : Exception
    {
    }
}
