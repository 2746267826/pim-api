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
    public async Task IngestAsync_ExecutionStrategyRetryTakesOverOwnPendingBatch()
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
            // 重试接管"本次请求自己留下的 pending 批次"并幂等重跑，而不是把 pending 当成并发重投跳过（#243）
            Assert.Equal(4, result.AcceptedCount);
            Assert.Equal(4, result.ItemResults.Count);
            Assert.Equal(1, await db.Set<MobileSyncBatchEntity>().CountAsync());
            var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
            Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
            Assert.Equal(4, batch.AcceptedCount);
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
        // accepted_count 覆盖该批全部被接受的条目（元数据 + 事件 + 汇总），而不只是 usage-event（#243）
        Assert.Equal(4, batch.AcceptedCount);
        Assert.Equal(0, batch.RejectedCount);
        Assert.Equal(0, batch.SkippedCount);
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
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
        // 失败的批次不再"消失"：它以 pending 落库，让积压监控/质量面板能发现未完成的同步（#243）。
        // 客户端的下一次重投会接管它（租约过期后）并把状态推进到终态。
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Pending, batch.Status);
        Assert.Null(batch.CompletedAtUtc);
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
    public async Task IngestAsync_SkipsDuplicateEventsAcrossBatches_EvenWhenCollectedAtOrRawJsonDiffer()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        var eventTime = start.AddMinutes(5);

        var firstRequest = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-1",
            start,
            end,
            [],
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    eventTime,
                    "MainActivity",
                    start.AddMinutes(6),
                    "{\"event\":\"tap1\"}",
                    "item-1")
            ],
            []);

        var secondRequest = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-2",
            start,
            end,
            [],
            [
                new MobileUsageEventDto(
                    "com.example.messages",
                    "USER_INTERACTION",
                    eventTime,
                    "MainActivity",
                    start.AddMinutes(15),
                    "{\"event\":\"tap2\"}",
                    "item-2")
            ],
            []);

        var first = await service.IngestAsync(firstRequest, CancellationToken.None);
        var second = await service.IngestAsync(secondRequest, CancellationToken.None);

        Assert.Equal(1, first.AcceptedCount);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(1, second.SkippedCount);
        var skippedItem = Assert.Single(second.ItemResults);
        Assert.Equal("skipped", skippedItem.Outcome);
        Assert.Equal("duplicate", skippedItem.Code);
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>().CountAsync());
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
            RawJson = "{\"event\":\"tap\"}",
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
            LifeCategory = MobileLifeCategories.Chat,
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
            LifeCategory = MobileLifeCategories.Chat,
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
            LifeCategory = MobileLifeCategories.Chat,
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

    [Fact]
    public async Task IngestAsync_RebuildsSessionsFromTheBatchsOwnEvents()
    {
        // #248：重建会话读的是数据库快照。批次自己的事件必须先落库，
        // 否则本批的事件要等到"下一条批次"才会进入会话，形成永远落后一批的静默缺口。
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = UploadRequest("batch-own-events", "Messages") with { Apps = [], FallbackSummaries = [] };

        await service.IngestAsync(request, CancellationToken.None);

        var session = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal("com.example.messages", session.PackageName);
        Assert.Equal(DateTimeOffset.Parse("2026-07-06T08:05:00Z"), session.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-06T08:25:00Z"), session.EndUtc);
    }

    [Fact]
    public async Task IngestAsync_DoesNotRebuildSessionsWhenTheBatchAddsNoNewEvents()
    {
        // #248：补偿批重复上传同一窗口时，事件集没有变化 => 会话不变。
        // 重建会把窗口内的会话整批删除重建，因此必须跳过，否则同一份数据被反复重写上百次。
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var first = UploadRequest("batch-first", "Messages") with { Apps = [], FallbackSummaries = [] };
        await service.IngestAsync(first, CancellationToken.None);

        var session = await db.Set<MobileUsageSessionEntity>().SingleAsync();
        var sessionId = session.Id;
        var createdAt = session.CreatedAt;

        var compensation = first with { ClientBatchId = "batch-compensation" };
        var result = await service.IngestAsync(compensation, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(0, result.AcceptedCount);
        var after = await db.Set<MobileUsageSessionEntity>().SingleAsync();
        Assert.Equal(sessionId, after.Id);
        Assert.Equal(createdAt, after.CreatedAt);
    }

    [Fact]
    public async Task IngestAsync_KeepsEarlierSessionWhenALaterWindowOverlapsOnlyItsTail()
    {
        // #248：起点在上一批窗口内的会话，遇到下一批"只重叠尾部"的窗口时不能被删掉后丢失。
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var foreground = new MobileUsageEventDto(
            "com.example.messages",
            "MOVE_TO_FOREGROUND",
            start.AddMinutes(5),
            "MainActivity",
            start.AddMinutes(6),
            "{}",
            "item-fg");

        var firstBatch = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-a",
            start,
            start.AddMinutes(15),
            [],
            [foreground],
            []);
        await service.IngestAsync(firstBatch, CancellationToken.None);
        var firstSession = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal(start.AddMinutes(5), firstSession.StartUtc);
        Assert.Equal(start.AddMinutes(15), firstSession.EndUtc);

        var chatForeground = new MobileUsageEventDto(
            "com.example.chat",
            "MOVE_TO_FOREGROUND",
            start.AddMinutes(20),
            "ChatActivity",
            start.AddMinutes(21),
            "{}",
            "item-chat");
        var secondBatch = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-b",
            start.AddMinutes(10),
            start.AddMinutes(45),
            [],
            [chatForeground],
            []);
        await service.IngestAsync(secondBatch, CancellationToken.None);

        var sessions = await db.Set<MobileUsageSessionEntity>().OrderBy(s => s.StartUtc).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal("com.example.messages", sessions[0].PackageName);
        Assert.Equal(start.AddMinutes(5), sessions[0].StartUtc);
        Assert.Equal(start.AddMinutes(20), sessions[0].EndUtc);
        Assert.Contains("closed-by-app-switch", sessions[0].QualityFlagsJson);
        Assert.Equal("com.example.chat", sessions[1].PackageName);
        Assert.Equal(start.AddMinutes(20), sessions[1].StartUtc);
    }

    [Fact]
    public async Task IngestAsync_RebuildsWhenALaterBatchWidensTheWindowOfAnOpenSession()
    {
        // 评审 #1：事件集没变、但窗口更宽时，上一批按"窗口末端封口"的开放会话必须重新封口。
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var start = DateTimeOffset.Parse("2026-07-06T08:00:00Z");
        var foreground = new MobileUsageEventDto(
            "com.example.messages",
            "MOVE_TO_FOREGROUND",
            start.AddMinutes(5),
            "MainActivity",
            start.AddMinutes(6),
            "{}",
            "item-fg");

        var firstBatch = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-open-1",
            start,
            start.AddMinutes(15),
            [],
            [foreground],
            []);
        await service.IngestAsync(firstBatch, CancellationToken.None);
        var firstSession = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal(start.AddMinutes(15), firstSession.EndUtc);
        Assert.Contains("open-ended", firstSession.QualityFlagsJson);

        // 同一事件、窗口扩到 12:00：事件全部命中重复，但会话必须跟着变宽。
        var widenedBatch = firstBatch with { ClientBatchId = "batch-open-2", SourceWindowEndUtc = start.AddHours(4) };
        var second = await service.IngestAsync(widenedBatch, CancellationToken.None);

        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(1, second.SkippedCount);
        var session = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal(start.AddHours(4), session.EndUtc);
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
