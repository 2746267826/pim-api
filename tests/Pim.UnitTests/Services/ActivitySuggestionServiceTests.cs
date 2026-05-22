using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivitySuggestionServiceTests
{
    [Fact]
    public async Task BuildSuggestionsAsync_GroupsFallbackWebRecordsByDomain()
    {
        using var db = CreateDbContext();
        var service = new ActivitySuggestionService(db);
        var records = new[]
        {
            NewWebRecord(
                durationSeconds: 120,
                url: "https://unknown.example.com/path?token=secret#frag",
                classificationSource: "fallback"),
            NewWebRecord(
                durationSeconds: 60,
                url: "https://unknown.example.com/other?token=secret",
                classificationSource: "fallback")
        };

        var suggestions = await service.BuildSuggestionsAsync(records, CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("web:unknown.example.com", suggestion.ClusterKey);
        Assert.Equal(2, suggestion.SampleCount);
        Assert.Equal(180, suggestion.TotalDurationSeconds);
        Assert.DoesNotContain("token=secret", suggestion.SanitizedContextJson);
        Assert.DoesNotContain("?token=", suggestion.SanitizedContextJson);
    }

    [Fact]
    public async Task AcceptSuggestionAsync_CreatesActiveRuleAndMarksAccepted()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "web:learn.example.com",
            Status = "pending",
            SampleCount = 1,
            TotalDurationSeconds = 90
        });
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);
        var request = new AcceptActivityClassificationSuggestionRequest(
            "Learning site",
            "activity",
            "学习",
            null,
            "#22c55e",
            200,
            """{"all":[{"field":"domain","op":"domainSuffix","value":"learn.example.com"}]}""",
            0.95,
            "Accepted from suggestion.");

        var rule = await service.AcceptSuggestionAsync(suggestionId, request, CancellationToken.None);

        Assert.Equal("学习", rule.CategoryName);
        Assert.Equal("active", rule.Status);
        Assert.Equal("user", rule.Source);
        var acceptedSuggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("accepted", acceptedSuggestion.Status);
        var entity = await db.Set<ActivityCategoryRuleEntity>().SingleAsync();
        Assert.Equal(rule.Id, entity.Id);
        Assert.Equal("学习", entity.CategoryName);
    }

    [Fact]
    public async Task RejectSuggestionAsync_MarksSuggestionRejected()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:unknown",
            Status = "pending",
            SampleCount = 3,
            TotalDurationSeconds = 45
        });
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);

        await service.RejectSuggestionAsync(suggestionId, CancellationToken.None);

        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("rejected", suggestion.Status);
        Assert.Empty(await db.Set<ActivityCategoryRuleEntity>().ToListAsync());
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationSuggestionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PimDbContext(options);
    }

    private static PcDetailRecord NewWebRecord(
        double durationSeconds,
        string url,
        string classificationSource)
    {
        return new PcDetailRecord(
            RecordType: "web-page",
            Start: "2026-05-22T01:00:00Z",
            End: "2026-05-22T01:02:00Z",
            DurationSeconds: durationSeconds,
            DeviceId: "device-1",
            AppName: "Firefox",
            DisplayName: "Firefox",
            CategoryName: "其他",
            Title: "Unknown page",
            KeyPresses: null,
            TotalClicks: null,
            MouseDistance: null,
            ScrollDistance: null,
            KeyCounts: null,
            Raw: null,
            Url: url,
            Domain: "unknown.example.com",
            Path: "/path",
            BrowserAppName: "Firefox",
            ClassificationConfidence: 0.2,
            ClassificationSource: classificationSource,
            ClassificationExplanation: "No rule matched.");
    }
}
