using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationSnapshotServiceTests
{
    [Fact]
    public async Task EnsureClassificationsAsync_CreatesDeterministicSnapshotWithoutChangingRecord()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs");
        var rules = new[]
        {
            NewRule("Code is programming", "\u7f16\u7a0b")
        };

        var classified = await service.EnsureClassificationsAsync(
            [record],
            rules,
            null,
            CancellationToken.None);

        var item = Assert.Single(classified);
        Assert.NotSame(record, item);
        Assert.Equal("\u7f16\u7a0b", item.CategoryName);
        Assert.Equal("\u5176\u4ed6", record.CategoryName);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal(ActivityClassificationRecordKey.FromRecord(record), snapshot.RecordKey);
        Assert.Equal("\u7f16\u7a0b", snapshot.CategoryName);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_PersistsKeyVersionSourceBucketsAndStability()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "Program.cs") with
        {
            SourceBucketIds = ["aw-watcher-window_device-1"],
            SourceWindowEventIds = [123],
            InterpretationVersion = "interpreted-aw-v1"
        };

        await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "Programming")],
            null,
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("pc-aw-v1", snapshot.RecordKeyVersion);
        Assert.Equal("stable", snapshot.RecordKeyStability);
        Assert.Equal("aw", snapshot.SourceType);
        Assert.Equal("[\"aw-watcher-window_device-1\"]", snapshot.SourceBucketIdsJson);
        Assert.Equal("interpreted-aw-v1", snapshot.InterpretationVersion);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_PersistsAppIdentityForTimeline()
    {
        // #235：应用名 / 显示名 / 窗口标题必须随快照落库，
        // 否则时间线 v2 只能拿 record_key（pc-fallback-v1:<hash>）当应用名展示。
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "PcProductivityService.cs");

        await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "\u7f16\u7a0b")],
            null,
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("Code.exe", snapshot.AppName);
        Assert.Equal("Code.exe", snapshot.AppDisplayName);
        Assert.Equal("PcProductivityService.cs", snapshot.WindowTitle);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_PrefersBrowserWindowTitleWhenPresent()
    {
        // 浏览器页面记录：窗口标题取 BrowserWindowTitle，应用名回退到 BrowserAppName。
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("chrome.exe", "Tag") with
        {
            AppName = null,
            BrowserAppName = "chrome.exe",
            BrowserWindowTitle = "PIM 文档 - Google Chrome"
        };

        await service.EnsureClassificationsAsync(
            [record],
            [NewRule("chrome is browsing", "\u6d4f\u89c8")],
            null,
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("chrome.exe", snapshot.AppName);
        Assert.Equal("PIM 文档 - Google Chrome", snapshot.WindowTitle);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_UpdatesExistingSnapshotForSameRecordKey()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs");

        await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "\u7f16\u7a0b", priority: 100)],
            null,
            CancellationToken.None);

        var auditId = Guid.NewGuid();
        var classified = await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is office", "\u529e\u516c", priority: 1000)],
            auditId,
            CancellationToken.None);

        var item = Assert.Single(classified);
        Assert.Equal("\u529e\u516c", item.CategoryName);

        var snapshots = await db.Set<ActivityClassificationEntity>().ToListAsync();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("\u529e\u516c", snapshot.CategoryName);
        Assert.Equal(auditId, snapshot.AuditId);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_RetriesRepeatedUniqueRacesForMixedUpdatesAndInserts()
    {
        var databaseName = Guid.NewGuid().ToString();
        using (var seedDb = CreateDb(databaseName))
        {
            var existingRecord = NewRecord("Code.exe", "existing.cs");
            seedDb.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
            {
                Id = Guid.NewGuid(),
                RecordKey = ActivityClassificationRecordKey.FromRecord(existingRecord),
                RecordType = existingRecord.RecordType,
                DeviceId = existingRecord.DeviceId,
                SourceEventIdsJson = ActivityClassificationRecordKey.SourceEventIdsJson(existingRecord),
                StartedAt = DateTimeOffset.Parse(existingRecord.Start),
                EndedAt = DateTimeOffset.Parse(existingRecord.End!),
                CategoryName = "旧分类",
                CategoryColor = "#64748b",
                Source = "fallback",
                Explanation = "seed"
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateDbWithRepeatedClassificationInsertRace(databaseName);
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var existing = NewRecord("Code.exe", "existing.cs");
        var insertOne = NewRecord("Code.exe", "new-one.cs");
        var insertTwo = NewRecord("Code.exe", "new-two.cs");
        var auditId = Guid.NewGuid();

        var classified = await service.EnsureClassificationsAsync(
            [existing, insertOne, insertTwo],
            [NewRule("Code is programming", "编程")],
            auditId,
            CancellationToken.None);

        Assert.Equal(3, classified.Count);
        Assert.All(classified, record => Assert.Equal("编程", record.CategoryName));
        Assert.Equal(3, await db.Set<ActivityClassificationEntity>().CountAsync());
        Assert.Equal("编程", await db.Set<ActivityClassificationEntity>()
            .Where(snapshot => snapshot.RecordKey == ActivityClassificationRecordKey.FromRecord(existing))
            .Select(snapshot => snapshot.CategoryName)
            .SingleAsync());
        Assert.Equal(3, db.SaveAttemptCount);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_PropagatesUnrelatedDatabaseFailure()
    {
        using var db = CreateDbWithUnrelatedClassificationSaveFailure();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.EnsureClassificationsAsync(
            [NewRecord("Code.exe", "unrelated-failure.cs")],
            [NewRule("Code is programming", "编程")],
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Contains("unrelated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, db.SaveAttemptCount);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_ReturnsPerRecordClassificationsForDuplicateKeys()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var codeRecord = NewStableWebRecord("Code.exe");
        var excelRecord = NewStableWebRecord("Excel.exe");

        var classified = await service.EnsureClassificationsAsync(
            [codeRecord, excelRecord],
            [
                NewRule("Code is programming", "\u7f16\u7a0b"),
                NewRule(
                    "Excel is office",
                    "\u529e\u516c",
                    conditionsJson: """
                        {"all":[{"field":"appNameNormalized","op":"equals","value":"excel"}]}
                        """)
            ],
            null,
            CancellationToken.None);

        Assert.Equal("\u7f16\u7a0b", classified[0].CategoryName);
        Assert.Equal("\u529e\u516c", classified[1].CategoryName);
        Assert.Equal(ActivityClassificationRecordKey.FromRecord(codeRecord), ActivityClassificationRecordKey.FromRecord(excelRecord));
        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task EnsureClassificationsAsync_PreservesExistingManualSnapshot()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs");
        var manualAuditId = Guid.NewGuid();
        var snapshot = new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = ActivityClassificationRecordKey.FromRecord(record),
            RecordType = record.RecordType,
            DeviceId = record.DeviceId,
            SourceEventIdsJson = ActivityClassificationRecordKey.SourceEventIdsJson(record),
            StartedAt = DateTimeOffset.Parse(record.Start),
            EndedAt = DateTimeOffset.Parse(record.End!),
            CategoryName = "\u6df1\u5ea6\u5de5\u4f5c",
            CategoryColor = "#123456",
            Confidence = 1,
            Source = "manual",
            Explanation = "Manual correction.",
            ClassifierVersion = ActivityClassificationSnapshotService.ClassifierVersion,
            ClassifiedAt = DateTimeOffset.Parse("2026-05-25T09:00:00Z"),
            AuditId = manualAuditId
        };
        db.Set<ActivityClassificationEntity>().Add(snapshot);
        await db.SaveChangesAsync();

        var classified = await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "\u7f16\u7a0b")],
            null,
            CancellationToken.None);

        var item = Assert.Single(classified);
        Assert.Equal("\u6df1\u5ea6\u5de5\u4f5c", item.CategoryName);
        Assert.Equal("manual", item.ClassificationSource);

        var persisted = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("\u6df1\u5ea6\u5de5\u4f5c", persisted.CategoryName);
        Assert.Equal("manual", persisted.Source);
        Assert.Equal(manualAuditId, persisted.AuditId);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_UpdatesSourceMetadataForProtectedManualSnapshot()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "Program.cs") with
        {
            SourceBucketIds = ["aw-watcher-window_device-1"],
            SourceWindowEventIds = [123],
            InterpretationVersion = "interpreted-aw-v1"
        };
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(),
            RecordKey = ActivityClassificationRecordKey.FromRecord(record),
            RecordType = record.RecordType,
            DeviceId = record.DeviceId,
            SourceEventIdsJson = "[]",
            RecordKeyVersion = "pc-fallback-v1",
            RecordKeyStability = "low",
            SourceType = "fallback",
            SourceBucketIdsJson = "[]",
            InterpretationVersion = "unknown",
            StartedAt = DateTimeOffset.Parse(record.Start),
            EndedAt = DateTimeOffset.Parse(record.End!),
            CategoryName = "Deep Work",
            CategoryColor = "#123456",
            Confidence = 1,
            Source = "manual",
            Explanation = "Manual correction.",
            ClassifierVersion = ActivityClassificationSnapshotService.ClassifierVersion,
            ClassifiedAt = DateTimeOffset.Parse("2026-05-25T09:00:00Z")
        });
        await db.SaveChangesAsync();

        await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "Programming")],
            null,
            CancellationToken.None);

        var persisted = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("Deep Work", persisted.CategoryName);
        Assert.Equal("manual", persisted.Source);
        Assert.Equal("[123]", persisted.SourceEventIdsJson);
        Assert.Equal("pc-aw-v1", persisted.RecordKeyVersion);
        Assert.Equal("stable", persisted.RecordKeyStability);
        Assert.Equal("aw", persisted.SourceType);
        Assert.Equal("[\"aw-watcher-window_device-1\"]", persisted.SourceBucketIdsJson);
        Assert.Equal("interpreted-aw-v1", persisted.InterpretationVersion);
    }

    [Fact]
    public async Task EnsureClassificationsAsync_UsesBucketTypeInRuleContext()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewStableWebRecord("msedge.exe") with
        {
            BucketType = "web.tab.current"
        };

        var classified = await service.EnsureClassificationsAsync(
            [record],
            [
                NewRule(
                    "Web bucket is learning",
                    "\u5b66\u4e60",
                    conditionsJson: """
                        {"all":[{"field":"bucketType","op":"equals","value":"web.tab.current"}]}
                        """)
            ],
            null,
            CancellationToken.None);

        Assert.Equal("\u5b66\u4e60", Assert.Single(classified).CategoryName);
        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("\u5b66\u4e60", snapshot.CategoryName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(null)]
    public async Task EnsureClassificationsAsync_ReturnsInvalidDurationRecordsUnchangedWithoutPersistence(double? durationSeconds)
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs") with
        {
            DurationSeconds = durationSeconds
        };

        var classified = await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "\u7f16\u7a0b")],
            null,
            CancellationToken.None);

        Assert.Same(record, Assert.Single(classified));
        Assert.Equal(0, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task EnsureClassificationsAsync_ReturnsInvalidTimestampRecordsUnchangedWithoutPersistence()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs") with
        {
            Start = "not-a-date"
        };

        var classified = await service.EnsureClassificationsAsync(
            [record],
            [NewRule("Code is programming", "\u7f16\u7a0b")],
            null,
            CancellationToken.None);

        Assert.Same(record, Assert.Single(classified));
        Assert.Equal(0, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public void FromRecord_UsesStartAsEndFallbackForOpenEndedRecords()
    {
        var openEndedRecord = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs") with
        {
            End = null
        };
        var explicitEndRecord = openEndedRecord with
        {
            End = openEndedRecord.Start
        };

        Assert.Equal(
            ActivityClassificationRecordKey.FromRecord(explicitEndRecord),
            ActivityClassificationRecordKey.FromRecord(openEndedRecord));
    }

    [Fact]
    public void SourceEventIdsJson_OrdersIdsAndPrefersWebIds()
    {
        var record = NewRecord("Code.exe", "ActivityClassificationSnapshotService.cs") with
        {
            SourceWebEventIds = [5, 3, 4],
            SourceWindowEventIds = [2, 1]
        };

        Assert.Equal("[3,4,5]", ActivityClassificationRecordKey.SourceEventIdsJson(record));
    }

    private static PimDbContext CreateDb()
        => CreateDb(Guid.NewGuid().ToString());

    private static PimDbContext CreateDb(string databaseName)
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new PimDbContext(options);
    }

    private static RacePimDbContext CreateDbWithRepeatedClassificationInsertRace(string databaseName)
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new RacePimDbContext(options);
    }

    private static UnrelatedFailurePimDbContext CreateDbWithUnrelatedClassificationSaveFailure()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new UnrelatedFailurePimDbContext(options);
    }

    private static PcDetailRecord NewRecord(string appName, string title) =>
        new(
            "window",
            "2026-05-25T08:00:00Z",
            "2026-05-25T08:10:00Z",
            600,
            "device-1",
            appName,
            appName,
            "\u5176\u4ed6",
            title,
            null,
            null,
            null,
            null,
            null,
            null);

    private static PcDetailRecord NewStableWebRecord(string browserAppName) =>
        new(
            "web-page",
            "2026-05-25T08:00:00Z",
            "2026-05-25T08:10:00Z",
            600,
            "device-1",
            null,
            "example.com",
            "\u5176\u4ed6",
            "Same page",
            null,
            null,
            null,
            null,
            null,
            null,
            "https://example.com/docs",
            "example.com",
            "/docs",
            false,
            browserAppName);

    private static ActivityCategoryRuleEntity NewRule(
        string ruleName,
        string categoryName,
        int priority = 100,
        string conditionsJson = """
            {"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}
            """) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleName = ruleName,
            Scope = "activity",
            CategoryName = categoryName,
            Color = "#6B5EE4",
            Priority = priority,
            Source = "user",
            Status = "active",
            ConditionsJson = conditionsJson,
            Confidence = 0.95,
            Explanation = "Matched test rule."
        };

    private sealed class RacePimDbContext : PimDbContext
    {
        private readonly DbContextOptions<PimDbContext> _options;

        public RacePimDbContext(DbContextOptions<PimDbContext> options)
            : base(options)
        {
            _options = options;
        }

        public int SaveAttemptCount { get; private set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttemptCount++;
            var pending = ChangeTracker.Entries<ActivityClassificationEntity>()
                .Where(entry => entry.State == EntityState.Added)
                .OrderBy(entry => entry.Entity.RecordKey, StringComparer.Ordinal)
                .FirstOrDefault();

            if (SaveAttemptCount <= 2 && pending is not null)
            {
                await using var competingDb = new PimDbContext(_options);
                var now = DateTimeOffset.UtcNow;
                competingDb.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
                {
                    Id = Guid.NewGuid(),
                    RecordKey = pending.Entity.RecordKey,
                    RecordType = pending.Entity.RecordType,
                    DeviceId = pending.Entity.DeviceId,
                    SourceEventIdsJson = pending.Entity.SourceEventIdsJson,
                    StartedAt = pending.Entity.StartedAt,
                    EndedAt = pending.Entity.EndedAt,
                    CategoryName = "竞争写入",
                    CategoryColor = "#64748b",
                    Source = "fallback",
                    Explanation = "competing writer",
                    ClassifiedAt = now
                });
                await competingDb.SaveChangesAsync(cancellationToken);
                throw new DbUpdateException(
                    "Simulated activity classification unique race.",
                    new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class UnrelatedFailurePimDbContext : PimDbContext
    {
        public UnrelatedFailurePimDbContext(DbContextOptions<PimDbContext> options)
            : base(options)
        {
        }

        public int SaveAttemptCount { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttemptCount++;
            throw new DbUpdateException("Simulated unrelated database failure.",
                new PostgresException("deadlock", "ERROR", "ERROR", "40P01"));
        }
    }
}
