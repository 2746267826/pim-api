using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · REQ-5（分类建议必须可正确归日且不混入空闲）。
/// </summary>
public sealed class PcSuggestionBusinessDateTests
{
    private const string IdleClusterKey = "app:__idle__";

    [Fact]
    public async Task BuildSuggestionsAsync_GivesEveryReturnedSuggestionABusinessDate()
    {
        await using var db = CreateDb();
        // 历史遗留 pending：没有任何扫描样本，但 sample_records_json 里保留了当时的样本时刻。
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.NewGuid(),
            ClusterKey = "web:legacy.example.com",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600,
            SampleRecordsJson = "[{\"start\":\"2026-07-03T01:00:00.0000000+00:00\",\"durationSeconds\":600}]",
            UpdatedAt = DateTimeOffset.Parse("2026-07-04T02:00:00+00:00"),
            CreatedAt = DateTimeOffset.Parse("2026-07-04T02:00:00+00:00")
        });
        await db.SaveChangesAsync();

        var suggestions = await Service(db).BuildSuggestionsAsync(
            new[] { FallbackRecord(At("2026-07-05T01:00:00Z"), 600, "Mystery.exe", "window") },
            recommendedMinimumMinutes: 1,
            CancellationToken.None);

        // AC-5.1：每条建议都能归属到一个业务日（逐条检查，不是只看第一条）。
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s.GeneratedForDate)));
        Assert.Equal("2026-07-05", suggestions.Single(s => s.ClusterKey == "app:mystery").GeneratedForDate);
        Assert.Equal("2026-07-03", suggestions.Single(s => s.ClusterKey == "web:legacy.example.com").GeneratedForDate);
    }

    [Fact]
    public async Task GetSuggestionsV2Async_AlsoCarriesGeneratedForDate()
    {
        await using var db = CreateDb();
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.NewGuid(),
            ClusterKey = "app:mystery",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600,
            SampleRecordsJson = "[{\"start\":\"2026-07-05T01:00:00.0000000+00:00\",\"durationSeconds\":600}]",
            UpdatedAt = DateTimeOffset.Parse("2026-07-05T02:00:00+00:00"),
            CreatedAt = DateTimeOffset.Parse("2026-07-05T02:00:00+00:00")
        });
        await db.SaveChangesAsync();

        var v2 = await Service(db).GetSuggestionsV2Async(CancellationToken.None);

        // v2 列表与 v1 同口径，同样每条都能归日。
        var suggestion = Assert.Single(v2);
        Assert.Equal("2026-07-05", suggestion.GeneratedForDate);
    }

    [Fact]
    public async Task BuildSuggestionsAsync_NeverSuggestsIdleSentinelOrInactiveRecords()
    {
        await using var db = CreateDb();
        var records = new[]
        {
            // 采集端哨兵：AppName = __IDLE__（客户端在空闲时段写死）。
            FallbackRecord(At("2026-07-05T01:00:00Z"), 900, "__IDLE__", "window"),
            // 未活动类型：gap / idle / afk 一律不参与建议。
            FallbackRecord(At("2026-07-05T02:00:00Z"), 900, "idle-proc.exe", "idle"),
            FallbackRecord(At("2026-07-05T03:00:00Z"), 900, "gap-proc.exe", "gap"),
            FallbackRecord(At("2026-07-05T04:00:00Z"), 900, "afk-proc.exe", "afk"),
            // 正常待分类记录仍然要出建议。
            FallbackRecord(At("2026-07-05T05:00:00Z"), 900, "Mystery.exe", "window")
        };

        var suggestions = await Service(db).BuildSuggestionsAsync(records, recommendedMinimumMinutes: 1, CancellationToken.None);

        // AC-5.2：含空闲的记录流不再生成 app:__idle__，且未活动记录不入簇。
        var suggestion = Assert.Single(suggestions);
        Assert.Equal("app:mystery", suggestion.ClusterKey);
        Assert.DoesNotContain(suggestions, s => s.ClusterKey.Contains("idle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.ClusterKey.Contains("gap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.ClusterKey.Contains("afk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildSuggestionsAsync_RetiresLegacyIdleSentinelPendingSuggestion()
    {
        await using var db = CreateDb();
        var legacyId = Guid.NewGuid();
        var legacyUpdatedAt = DateTimeOffset.Parse("2026-09-19T10:26:00+00:00");
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = legacyId,
            ClusterKey = IdleClusterKey,
            Status = "pending",
            SampleCount = 2,
            TotalDurationSeconds = 120,
            UpdatedAt = legacyUpdatedAt,
            CreatedAt = DateTimeOffset.Parse("2026-09-01T14:00:00+00:00")
        });
        await db.SaveChangesAsync();

        var service = Service(db);
        var suggestions = await service.BuildSuggestionsAsync(
            new[] { FallbackRecord(At("2026-07-05T01:00:00Z"), 600, "Mystery.exe", "window") },
            recommendedMinimumMinutes: 1,
            CancellationToken.None);

        // P-5 方案 a：既有 app:__idle__ pending 置失效、保留可追溯（行还在、原创建时间不动）。
        var retired = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync(s => s.Id == legacyId);
        Assert.Equal("invalidated", retired.Status);
        Assert.True(retired.UpdatedAt > legacyUpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T14:00:00+00:00"), retired.CreatedAt);
        Assert.DoesNotContain(suggestions, s => s.ClusterKey == IdleClusterKey);
        Assert.DoesNotContain(await service.GetSuggestionsAsync(CancellationToken.None), s => s.ClusterKey == IdleClusterKey);
    }

    [Fact]
    public async Task BuildSuggestionsAsync_ScansRecordsAcrossTheWholeBusinessDay()
    {
        await using var db = CreateDb();
        // 前 200 条（升序第一页）都是不缺分类的记录；真正需要建议的记录排在最后。
        var records = Enumerable.Range(0, 210)
            .Select(i => ConfidentRecord(At("2026-07-05T01:00:00Z").AddSeconds(i * 10), 10, $"Known{i % 5}.exe"))
            .Append(FallbackRecord(At("2026-07-05T09:30:00Z"), 600, "Mystery.exe", "window"))
            .ToList();

        var suggestions = await Service(db).BuildSuggestionsAsync(records, recommendedMinimumMinutes: 1, CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("app:mystery", suggestion.ClusterKey);
    }

    private static ActivitySuggestionService Service(PimDbContext db) => new(db, new AppSignatureService(db));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationSuggestionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static DateTimeOffset At(string timestamp) => DateTimeOffset.Parse(timestamp);

    private static PcDetailRecord FallbackRecord(DateTimeOffset start, double duration, string appName, string recordType)
        => Record(start, duration, appName, recordType, "fallback", 0.2);

    private static PcDetailRecord ConfidentRecord(DateTimeOffset start, double duration, string appName)
        => Record(start, duration, appName, "window", "rule", 0.95);

    private static PcDetailRecord Record(
        DateTimeOffset start,
        double duration,
        string appName,
        string recordType,
        string classificationSource,
        double confidence)
        => new(
            RecordType: recordType,
            Start: start.ToUniversalTime().ToString("O"),
            End: start.AddSeconds(duration).ToUniversalTime().ToString("O"),
            DurationSeconds: duration,
            DeviceId: "device-1",
            AppName: appName,
            DisplayName: appName,
            CategoryName: "其他",
            Title: null,
            KeyPresses: null,
            TotalClicks: null,
            MouseDistance: null,
            ScrollDistance: null,
            KeyCounts: null,
            Raw: null,
            ClassificationConfidence: confidence,
            ClassificationSource: classificationSource,
            ClassificationExplanation: classificationSource);
}
