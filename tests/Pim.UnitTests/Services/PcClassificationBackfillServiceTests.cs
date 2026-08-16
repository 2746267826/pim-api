using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Caching;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcClassificationBackfillServiceTests
{
    // 固定 now = 2026-08-15T12:00:00Z（Asia/Shanghai 本地 2026-08-15 20:00），
    // 使「今天」业务日 = 2026-08-15（窗口 [2026-08-14T20:00Z, 2026-08-15T20:00Z)）。
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-08-15T12:00:00Z");

    [Fact]
    public async Task BackfillAsync_ProcessesPastDayWithEventsButNoSnapshots()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().Add(WindowEvent("2026-08-10T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var stats = await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);

        Assert.Equal(1, stats.ProcessedDays);
        Assert.Equal(1, stats.WrittenSnapshots);
        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task BackfillAsync_SkipsPastDayAlreadyClassified()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().Add(WindowEvent("2026-08-10T08:00:00Z", 600, "Code.exe", "Program.cs"));
        db.Set<ActivityClassificationEntity>().Add(Snapshot(
            "pc-fallback-v1:pre-existing",
            DateTimeOffset.Parse("2026-08-10T08:00:00Z")));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var stats = await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);

        Assert.Equal(0, stats.ProcessedDays);
        Assert.Equal(0, stats.WrittenSnapshots);
        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task EnsureClassificationsAsync_SecondRunDoesNotDuplicateSnapshots()
    {
        // 并发防护的幂等面：同一批 records 连续 ensure 两次，第二次全命中已有快照，不新增不抛。
        await using var db = CreateDb();
        var snapshotService = new ActivityClassificationSnapshotService(
            db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var rules = new List<ActivityCategoryRuleEntity>();
        var records = new List<PcDetailRecord>
        {
            new(
                RecordType: "window",
                Start: "2026-08-10T08:00:00Z",
                End: "2026-08-10T08:10:00Z",
                DurationSeconds: 600,
                DeviceId: "device-1",
                AppName: "Code.exe",
                DisplayName: null,
                CategoryName: null,
                Title: "Program.cs",
                KeyPresses: null,
                TotalClicks: null,
                MouseDistance: null,
                ScrollDistance: null,
                KeyCounts: null,
                Raw: null,
                SourceWebEventIds: null,
                SourceWindowEventIds: null)
        };

        await snapshotService.EnsureClassificationsAsync(records, rules, null, CancellationToken.None);
        await snapshotService.EnsureClassificationsAsync(records, rules, null, CancellationToken.None);

        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task BackfillAsync_SkipsDayWithoutEvents()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var stats = await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);

        Assert.Equal(0, stats.ProcessedDays);
        Assert.Equal(0, stats.WrittenSnapshots);
        Assert.Equal(0, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task BackfillAsync_AlwaysProcessesCurrentDay()
    {
        await using var db = CreateDb();
        // 今天业务日窗口 [2026-08-14T20:00Z, 2026-08-15T20:00Z)：01:00Z / 02:00Z 均为本地 09:00 / 10:00。
        db.Set<AwEventEntity>().AddRange(
            WindowEvent("2026-08-15T01:00:00Z", 600, "Code.exe", "Program.cs"),
            WindowEvent("2026-08-15T02:00:00Z", 600, "Edge.exe", "Web.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var first = await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);
        Assert.Equal(1, first.ProcessedDays);
        Assert.Equal(2, await db.Set<ActivityClassificationEntity>().CountAsync());

        // 今天部分快照已存在：新增第三条事件，再次 backfill 应增量补上其 record_key，不产生重复。
        db.Set<AwEventEntity>().Add(WindowEvent("2026-08-15T03:00:00Z", 600, "Notepad.exe", "Notes.txt"));
        await db.SaveChangesAsync();

        var second = await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);

        Assert.Equal(1, second.ProcessedDays);
        Assert.Equal(1, second.WrittenSnapshots);
        Assert.Equal(3, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task BackfillAsync_EvictsPcCachePrefix()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().Add(WindowEvent("2026-08-10T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var cache = new FakeAggregateResultCache();
        var service = CreateService(db, cache);

        await service.BackfillAsync(lookbackDays: 14, CancellationToken.None);

        Assert.Contains("/api/v1/pc/", cache.EvictedPrefixes);
    }

    [Fact]
    public async Task RecomputeAsync_StillWritesAudit()
    {
        await using var db = CreateDb();
        db.Set<ActivityCategoryRuleEntity>().Add(CodeRule("\u7f16\u7a0b", 1000));
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var recompute = CreateRecomputeService(db);

        var result = await recompute.RecomputeAsync(
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, result.RecomputedRecordCount);
        Assert.NotEqual(Guid.Empty, result.AuditId);
        var audit = await db.Set<ActivityClassificationAuditEntity>().SingleAsync();
        Assert.Equal("range.recompute", audit.Operation);
        Assert.Equal(result.AuditId, audit.Id);
        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal(result.AuditId, snapshot.AuditId);
    }

    // === helpers ===

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new PimDbContext(options);
        db.Set<PcCategoryEntity>().AddRange(
            new PcCategoryEntity { Id = Guid.NewGuid(), Name = "\u7f16\u7a0b", Color = "#6B5EE4" },
            new PcCategoryEntity { Id = Guid.NewGuid(), Name = "\u529e\u516c", Color = "#F59E0B" },
            new PcCategoryEntity { Id = Guid.NewGuid(), Name = "\u6df1\u5ea6\u5de5\u4f5c", Color = "#123456" });
        db.SaveChanges();
        return db;
    }

    private static PcClassificationBackfillService CreateService(
        PimDbContext db,
        FakeAggregateResultCache? cache = null) =>
        new(
            db,
            CreateRecomputeService(db),
            new FixedTimeProvider(FixedNow),
            cache ?? new FakeAggregateResultCache(),
            NullLogger<PcClassificationBackfillService>.Instance);

    private static ActivityClassificationRecomputeService CreateRecomputeService(PimDbContext db) =>
        new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationRuleService(db),
            new FixedCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            NullLogger<ActivityClassificationRecomputeService>.Instance);

    private static AwEventEntity WindowEvent(string timestamp, double duration, string appName, string title) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            SourceEventId = null,
            DeviceId = "device-1",
            Timestamp = DateTimeOffset.Parse(timestamp),
            Duration = duration,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = AppNameNormalizer.Normalize(appName),
            WindowTitle = title,
            DataJson = "{}"
        };

    private static ActivityClassificationEntity Snapshot(string recordKey, DateTimeOffset startedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            RecordKey = recordKey,
            RecordType = "window",
            DeviceId = "device-1",
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(600),
            CategoryName = "\u5176\u4ed6",
            CategoryColor = "#64748b",
            Confidence = 0.2,
            Source = "fallback"
        };

    private static ActivityCategoryRuleEntity CodeRule(string categoryName, int priority) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleName = "Code windows",
            Scope = "activity",
            CategoryName = categoryName,
            Color = "#F59E0B",
            Priority = priority,
            Source = "user",
            Status = "active",
            ConditionsJson = """
            {"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}
            """,
            Confidence = 0.9,
            Explanation = "Matched Code windows.",
            CreatedAt = DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-24T00:00:00Z")
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeAggregateResultCache : IAggregateResultCache
    {
        public List<string> EvictedPrefixes { get; } = [];

        public Task<T> GetOrCreateAsync<T>(string key, bool force, Func<Task<T>> factory, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void EvictByPrefix(string keyPrefix) => EvictedPrefixes.Add(keyPrefix);

        public TimeSpan ResolveTtl(DateTimeOffset utcNow) => TimeSpan.FromMinutes(5);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "User";
    }
}
