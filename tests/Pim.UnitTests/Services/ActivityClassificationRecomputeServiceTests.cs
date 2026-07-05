using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationRecomputeServiceTests
{
    [Fact]
    public async Task PreviewRuleAsync_ReturnsAffectedCountsWithoutSavingRule()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        Assert.Equal(600, preview.AffectedDurationSeconds);
        Assert.Equal(0, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
    }

    [Fact]
    public async Task ApplyRuleAsync_SavesRuleRecomputesRangeAndWritesAudit()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"),
            WindowEvent("2026-05-26T08:00:00Z", 600, "Code.exe", "OtherDay.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.ApplyRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        Assert.Equal(1, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("pc.classification.rule.apply", audit.Action);
        Assert.Equal("User", audit.ActorType);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), audit.UserId);
    }

    [Fact]
    public async Task PreviewAndApply_LowerPriorityRuleDoesNotAffectHigherPriorityExistingRule()
    {
        await using var db = CreateDb();
        db.Set<ActivityCategoryRuleEntity>().Add(CodeRule("\u529e\u516c", 2000));
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(priority: 1000),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(0, preview.AffectedRecordCount);

        await service.ApplyRuleAsync(
            CodeRuleRequest(priority: 1000),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("\u529e\u516c", snapshot.CategoryName);
    }

    [Fact]
    public async Task PreviewRuleAsync_CurrentCategoryCountsUseCurrentClassification()
    {
        await using var db = CreateDb();
        db.Set<ActivityCategoryRuleEntity>().Add(CodeRule("\u529e\u516c", 100));
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(priority: 1000),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        Assert.Equal(1, preview.CurrentCategoryCounts["\u529e\u516c"]);
        Assert.False(preview.CurrentCategoryCounts.ContainsKey("\u5176\u4ed6"));
        Assert.Equal(1, preview.NewCategoryCounts["\u7f16\u7a0b"]);
    }

    [Fact]
    public async Task PreviewAndApply_PreserveProtectedManualSnapshot()
    {
        await using var db = CreateDb();
        var awEvent = WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs");
        db.Set<AwEventEntity>().Add(awEvent);
        var record = WindowRecord(awEvent);
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
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
            ClassifiedAt = DateTimeOffset.Parse("2026-05-25T09:00:00Z")
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(0, preview.AffectedRecordCount);

        await service.ApplyRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("\u6df1\u5ea6\u5de5\u4f5c", snapshot.CategoryName);
        Assert.Equal("manual", snapshot.Source);
    }

    [Fact]
    public async Task PreviewAndApply_CandidateWinsEqualPriorityByCreatedAt()
    {
        await using var db = CreateDb();
        db.Set<ActivityCategoryRuleEntity>().Add(CodeRule(
            "\u529e\u516c",
            1000,
            withCreatedAt: DateTimeOffset.Parse("2026-05-24T00:00:00Z")));
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(priority: 1000),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        Assert.Equal("\u7f16\u7a0b", Assert.Single(preview.Samples).CategoryName);

        await service.ApplyRuleAsync(
            CodeRuleRequest(priority: 1000),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("\u7f16\u7a0b", snapshot.CategoryName);
    }

    [Fact]
    public async Task PreviewAndApply_DomainRuleAffectsInterpretedWebPageRecord()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            WindowEvent("2026-05-25T08:00:00Z", 600, "msedge.exe", "Docs Browser"),
            WebEvent(100, "2026-05-25T08:01:00Z", 300, "https://docs.example.com/guide", "Docs Guide"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var request = CodeRuleRequest() with
        {
            RuleName = "Docs domain",
            ConditionsJson = """
            {"all":[{"field":"domain","op":"equals","value":"docs.example.com"}]}
            """
        };

        var preview = await service.PreviewRuleAsync(
            request,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        var sample = Assert.Single(preview.Samples);
        Assert.Equal("web-page", sample.RecordType);
        Assert.Equal("docs.example.com", sample.Domain);
        Assert.Equal("/guide", sample.Path);

        await service.ApplyRuleAsync(
            request,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("web-page", snapshot.RecordType);
        Assert.Equal("\u7f16\u7a0b", snapshot.CategoryName);
    }

    [Fact]
    public async Task PreviewAndApply_BucketTypeRuleAffectsInterpretedWebPageRecord()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            WindowEvent("2026-05-25T08:00:00Z", 600, "msedge.exe", "Docs Browser"),
            WebEvent(101, "2026-05-25T08:01:00Z", 300, "https://docs.example.com/guide", "Docs Guide"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var request = CodeRuleRequest() with
        {
            RuleName = "Web bucket",
            ConditionsJson = """
            {"all":[{"field":"bucketType","op":"equals","value":"web.tab.current"}]}
            """
        };

        var preview = await service.PreviewRuleAsync(
            request,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(1, preview.AffectedRecordCount);
        var sample = Assert.Single(preview.Samples);
        Assert.Equal("web-page", sample.RecordType);
        Assert.Equal("web.tab.current", sample.BucketType);

        await service.ApplyRuleAsync(
            request,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        var snapshot = await db.Set<ActivityClassificationEntity>().SingleAsync();
        Assert.Equal("web-page", snapshot.RecordType);
        Assert.Equal("\u7f16\u7a0b", snapshot.CategoryName);
    }

    [Fact]
    public async Task PreviewRuleAsync_UsesPcBusinessDayRange()
    {
        await using var db = CreateDb();
        var businessStart = PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 5, 25));
        var businessEnd = businessStart.AddDays(1);
        db.Set<AwEventEntity>().AddRange(
            WindowEvent(businessStart.AddMinutes(-1), 60, "Code.exe", "Previous day"),
            WindowEvent(businessStart, 60, "Code.exe", "Business start"),
            WindowEvent(businessEnd.AddMinutes(-1), 60, "Code.exe", "Business end"),
            WindowEvent(businessEnd, 60, "Code.exe", "Next day"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var preview = await service.PreviewRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None);

        Assert.Equal(2, preview.AffectedRecordCount);
        Assert.Collection(
            preview.Samples,
            sample => Assert.Equal("Business start", sample.Title),
            sample => Assert.Equal("Business end", sample.Title));
    }

    [Theory]
    [InlineData("today", null, null)]
    [InlineData("today", "2026-05-25", null)]
    [InlineData("all", null, null)]
    [InlineData("all", "2026-05-25", "2026-05-25")]
    [InlineData("weekly", "2026-05-25", "2026-05-25")]
    [InlineData("range", "not-a-date", "2026-05-25")]
    [InlineData("range", "2026-05-26", "2026-05-25")]
    public async Task PreviewRuleAsync_RejectsInvalidRange(string mode, string? dateFrom, string? dateTo)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest(mode, dateFrom, dateTo),
            CancellationToken.None));
    }

    [Fact]
    public async Task ApplyRuleAsync_RejectsDuplicateRuleName()
    {
        await using var db = CreateDb();
        db.Set<ActivityCategoryRuleEntity>().Add(CodeRule("\u529e\u516c", 1000, ruleName: "Code windows"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRuleAsync(
            CodeRuleRequest(),
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None));
    }

    [Fact]
    public async Task PreviewSuggestionAsync_ReturnsPreviewWithoutSavingRuleOrChangingSuggestion()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = "Programming", Color = "#2563eb" });
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            ClusterKey = "app:code",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600
        });
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var drafts = new ClassificationRuleDraftService(db);

        var result = await service.PreviewSuggestionAsync(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            new SuggestionClassificationPreviewRequest(
                "Programming",
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25")),
            drafts,
            CancellationToken.None);

        Assert.Equal("Programming", result.Rule.CategoryName);
        Assert.Equal(1, result.Preview.AffectedRecordCount);
        Assert.Equal(0, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
        Assert.Equal("pending", await db.Set<ActivityClassificationSuggestionEntity>()
            .Where(item => item.Id == Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"))
            .Select(item => item.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task ApplySuggestionAsync_SavesRuleRecomputesAndMarksSuggestionAccepted()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = "Programming", Color = "#2563eb" });
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ClusterKey = "app:code",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600
        });
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var drafts = new ClassificationRuleDraftService(db);

        var result = await service.ApplySuggestionAsync(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            new SuggestionClassificationApplyRequest(
                "Programming",
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25")),
            drafts,
            CancellationToken.None);

        Assert.Equal("accepted", result.SuggestionStatus);
        Assert.Equal("Programming", result.Rule.CategoryName);
        Assert.Equal(1, await db.Set<ActivityClassificationEntity>().CountAsync());
    }

    [Fact]
    public async Task ApplySuggestionAsync_RejectsSecondApplyWithoutCreatingDuplicateRule()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = "Programming", Color = "#2563eb" });
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            ClusterKey = "app:code",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600
        });
        db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var drafts = new ClassificationRuleDraftService(db);
        var request = new SuggestionClassificationApplyRequest(
            "Programming",
            null,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"));

        await service.ApplySuggestionAsync(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            request,
            drafts,
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySuggestionAsync(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            request,
            drafts,
            CancellationToken.None));

        Assert.Contains("pending", ex.Message);
        Assert.Equal(1, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{invalid")]
    [InlineData("{}")]
    [InlineData("""{"all":[]}""")]
    [InlineData("""{"all":[{"field":"appNameNormalized","op":"unknown","value":"code"}]}""")]
    [InlineData("""{"all":[{"field":"unknown","op":"equals","value":"code"}]}""")]
    [InlineData("""{"all":[{"field":"appNameNormalized","op":"containsAny","value":"code"}]}""")]
    [InlineData("""{"all":[{"field":"appNameNormalized","op":"containsAny","value":[]}]}""")]
    [InlineData("""{"all":[{"field":"appNameNormalized","op":"containsAny","value":[""]}]}""")]
    [InlineData("""{"all":[{"field":"appNameNormalized","op":"regex","value":"["}]}""")]
    public async Task PreviewRuleAsync_RejectsInvalidConditionsJson(string conditionsJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var request = CodeRuleRequest() with { ConditionsJson = conditionsJson };

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewRuleAsync(
            request,
            new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"),
            CancellationToken.None));

        Assert.Equal(0, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
    }

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

    private static ActivityClassificationRecomputeService CreateService(PimDbContext db) =>
        new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationRuleService(db),
            new AuditLogService(db),
            new FixedCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            NullLogger<ActivityClassificationRecomputeService>.Instance);

    private static SaveActivityClassificationRuleRequest CodeRuleRequest(int priority = 1000) =>
        new(
            "Code windows",
            "activity",
            "\u7f16\u7a0b",
            null,
            "#6B5EE4",
            priority,
            """
            {"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}
            """,
            0.95,
            "Matched Code windows.");

    private static ActivityCategoryRuleEntity CodeRule(
        string categoryName,
        int priority,
        string ruleName = "Existing Code rule",
        DateTimeOffset? withCreatedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleName = ruleName,
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
            Explanation = "Existing rule.",
            CreatedAt = withCreatedAt ?? DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            UpdatedAt = withCreatedAt ?? DateTimeOffset.Parse("2026-05-24T00:00:00Z")
        };

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

    private static AwEventEntity WebEvent(long sourceId, string timestamp, double duration, string url, string title) =>
        new()
        {
            Id = sourceId,
            DeviceId = "device-1",
            Timestamp = DateTimeOffset.Parse(timestamp),
            Duration = duration,
            EventType = "web",
            BucketId = "aw-watcher-web-edge_device-1",
            BucketType = "web.tab.current",
            SourceEventId = sourceId,
            WindowTitle = title,
            DataJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["url"] = url,
                ["title"] = title,
                ["audible"] = false,
                ["incognito"] = false,
                ["tabCount"] = 3
            })
        };

    private static AwEventEntity WindowEvent(DateTimeOffset timestamp, double duration, string appName, string title) =>
        WindowEvent(timestamp.ToUniversalTime().ToString("O"), duration, appName, title);

    private static PcDetailRecord WindowRecord(AwEventEntity entity) =>
        new(
            "window",
            entity.Timestamp.ToUniversalTime().ToString("O"),
            entity.Timestamp.AddSeconds(entity.Duration).ToUniversalTime().ToString("O"),
            entity.Duration,
            entity.DeviceId,
            entity.AppName,
            AppNameNormalizer.Normalize(entity.AppNameNormalized ?? entity.AppName),
            "\u5176\u4ed6",
            entity.WindowTitle,
            null,
            null,
            null,
            null,
            null,
            null,
            SourceWindowEventIds: entity.SourceEventId is long sourceId ? [sourceId] : []);

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "User";
    }
}
