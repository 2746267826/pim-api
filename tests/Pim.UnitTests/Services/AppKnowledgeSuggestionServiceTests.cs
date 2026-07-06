using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class AppKnowledgeSuggestionServiceTests
{
    [Fact]
    public async Task BuildRecommendedContextAsync_PrefersDomainFromPluralSanitizedContextAndReturnsPreviewAlternatives()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var app = NewApp("msedge.exe", "Microsoft Edge");
        db.Set<AppSignatureEntity>().Add(app);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "web:github.com",
            SampleCount = 4,
            TotalDurationSeconds = 1200,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """
            {
              "clusterKey": "web:github.com",
              "apps": ["msedge.exe"],
              "domains": ["github.com"],
              "titles": ["PIM issue triage", "PIM issue triage"],
              "urls": ["https://github.com/acme/pim/issues"]
            }
            """,
            CurrentCategory = "Other",
            SuggestedCategory = "Development",
            SuggestedProjectTag = "PIM",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = NewPreview(affectedRecordCount: 3, affectedDurationSeconds: 900);

        var result = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Engineering",
                "Task 8",
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            preview,
            CancellationToken.None);

        Assert.Equal(suggestionId, result.SuggestionId);
        Assert.Same(preview, result.Preview);
        Assert.Equal(app.Id, result.RecommendedContext.AppId);
        Assert.Equal("msedge.exe", result.RecommendedContext.ProcessName);
        Assert.Equal("domain", result.RecommendedContext.PatternType);
        Assert.Equal("github.com", result.RecommendedContext.PatternValue);
        Assert.Equal("Engineering", result.RecommendedContext.TargetCategoryName);
        Assert.Equal("Task 8", result.RecommendedContext.ProjectTag);
        Assert.Equal("app-knowledge-suggestion", result.RecommendedContext.Source);
        Assert.Equal(3, result.RecommendedContext.AffectedRecordCount);
        Assert.Equal(900, result.RecommendedContext.AffectedDurationSeconds);
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "title" && item.PatternValue == "PIM issue triage");
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "app-default" && item.PatternValue == "msedge.exe");
        Assert.Equal(
            result.Alternatives.Count,
            result.Alternatives
                .Select(item => $"{item.PatternType}:{item.PatternValue}".ToLowerInvariant())
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task BuildRecommendedContextAsync_UsesTitleWhenSingularSanitizedContextHasNoDomain()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:code",
            SampleCount = 2,
            TotalDurationSeconds = 600,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"appName":"Code.exe","title":"Program.cs"}""",
            SuggestedCategory = "Development",
            SuggestedProjectTag = "PIM",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                null,
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            preview: null,
            CancellationToken.None);

        Assert.Equal("Code.exe", result.RecommendedContext.ProcessName);
        Assert.Equal("title", result.RecommendedContext.PatternType);
        Assert.Equal("Program.cs", result.RecommendedContext.PatternValue);
        Assert.Equal("Development", result.RecommendedContext.TargetCategoryName);
        Assert.Equal("PIM", result.RecommendedContext.ProjectTag);
        Assert.Equal(2, result.Preview.AffectedRecordCount);
        Assert.Equal(600, result.Preview.AffectedDurationSeconds);
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "app-default" && item.PatternValue == "Code.exe");
    }

    [Fact]
    public async Task SaveRecommendedContextAsync_PersistsSourceSuggestionAndPreviewImpact()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var app = NewApp("chrome.exe", "Google Chrome");
        db.Set<AppSignatureEntity>().Add(app);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "web:docs.example.com",
            SampleCount = 5,
            TotalDurationSeconds = 1800,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"apps":["chrome.exe"],"domains":["docs.example.com"]}""",
            SuggestedCategory = "Research",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Documentation",
                "PIM",
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            NewPreview(affectedRecordCount: 7, affectedDurationSeconds: 2400),
            CancellationToken.None);

        var saved = await service.SaveRecommendedContextAsync(preview, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(app.Id, saved.AppId);
        Assert.Equal("chrome.exe", saved.ProcessName);
        Assert.Equal("domain", saved.PatternType);
        Assert.Equal("docs.example.com", saved.PatternValue);
        Assert.Equal("app-knowledge-suggestion", saved.Source);
        Assert.Equal(7, saved.AffectedRecordCount);
        Assert.Equal(2400, saved.AffectedDurationSeconds);
        var entity = await db.Set<AppKnowledgeContextEntity>().SingleAsync();
        Assert.Equal(suggestionId, entity.SourceSuggestionId);
        Assert.Equal("app-knowledge-suggestion", entity.Source);
        Assert.Equal(7, entity.AffectedRecordCount);
        Assert.Equal(2400, entity.AffectedDurationSeconds);
    }

    private static AppKnowledgeSuggestionService CreateService(PimDbContext db) =>
        new(db, new AppKnowledgeContextService(db));

    private static ActivityClassificationPreviewDto NewPreview(int affectedRecordCount, double affectedDurationSeconds) =>
        new(
            affectedRecordCount,
            affectedDurationSeconds,
            new Dictionary<string, int> { ["Other"] = affectedRecordCount },
            new Dictionary<string, int> { ["Engineering"] = affectedRecordCount },
            Array.Empty<PcDetailRecord>(),
            affectedRecordCount > 0,
            $"Would affect {affectedRecordCount} records.");

    private static AppSignatureEntity NewApp(string processName, string displayName) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProcessName = processName,
            DisplayName = displayName,
            Source = "builtin",
            Confidence = 0.99,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AppKnowledgeContextEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
