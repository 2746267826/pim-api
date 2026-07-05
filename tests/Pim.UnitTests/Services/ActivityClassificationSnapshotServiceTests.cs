using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PimDbContext(options);
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
}
