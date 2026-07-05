using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ClassificationRuleDraftServiceTests
{
    [Fact]
    public async Task BuildSuggestionDraftAsync_CreatesDomainRuleForWebCluster()
    {
        await using var db = CreateDb();
        var suggestion = NewSuggestion("web:docs.example.com");
        db.Set<ActivityClassificationSuggestionEntity>().Add(suggestion);
        await db.SaveChangesAsync();
        var service = new ClassificationRuleDraftService(db);

        var rule = await service.BuildSuggestionDraftAsync(
            suggestion.Id,
            new SuggestionClassificationPreviewRequest(
                "Learning",
                "Docs",
                new ActivityClassificationApplyRangeRequest("today", "2026-07-05", "2026-07-05")),
            CancellationToken.None);

        Assert.Equal("activity", rule.Scope);
        Assert.Equal("Learning", rule.CategoryName);
        Assert.Equal("Docs", rule.ProjectTag);
        Assert.Contains("\"field\":\"domain\"", rule.ConditionsJson);
        Assert.Contains("\"op\":\"domainSuffix\"", rule.ConditionsJson);
        Assert.Contains("\"value\":\"docs.example.com\"", rule.ConditionsJson);
    }

    [Fact]
    public async Task BuildSuggestionDraftAsync_CreatesAppRuleForAppCluster()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "Programming",
            Color = "#2563eb"
        });
        var suggestion = NewSuggestion("app:code");
        db.Set<ActivityClassificationSuggestionEntity>().Add(suggestion);
        await db.SaveChangesAsync();
        var service = new ClassificationRuleDraftService(db);

        var rule = await service.BuildSuggestionDraftAsync(
            suggestion.Id,
            new SuggestionClassificationPreviewRequest(
                "Programming",
                null,
                new ActivityClassificationApplyRangeRequest("today", "2026-07-05", "2026-07-05")),
            CancellationToken.None);

        Assert.Contains("\"field\":\"appNameNormalized\"", rule.ConditionsJson);
        Assert.Contains("\"value\":\"code\"", rule.ConditionsJson);
        Assert.Equal("#2563eb", rule.Color);
    }

    [Fact]
    public async Task BuildSuggestionDraftAsync_UsesDeterministicBoundedRuleName()
    {
        await using var db = CreateDb();
        var suggestion = NewSuggestion($"web:{new string('a', 180)}.example.com");
        db.Set<ActivityClassificationSuggestionEntity>().Add(suggestion);
        await db.SaveChangesAsync();
        var service = new ClassificationRuleDraftService(db);
        var request = new SuggestionClassificationPreviewRequest(
            "Learning",
            null,
            new ActivityClassificationApplyRangeRequest("today", "2026-07-05", "2026-07-05"));

        var first = await service.BuildSuggestionDraftAsync(suggestion.Id, request, CancellationToken.None);
        var second = await service.BuildSuggestionDraftAsync(suggestion.Id, request, CancellationToken.None);

        Assert.Equal(first.RuleName, second.RuleName);
        Assert.True(first.RuleName.Length <= 128);
        Assert.Contains(suggestion.Id.ToString("N"), first.RuleName);
    }

    private static ActivityClassificationSuggestionEntity NewSuggestion(string clusterKey) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClusterKey = clusterKey,
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 600,
            CurrentCategory = "Other"
        };

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationSuggestionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
