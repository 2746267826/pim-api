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
                url: "https://user:password@unknown.example.com/path/eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c?token=secret#frag",
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
        AssertNoSensitiveUrlMaterial(suggestion.SanitizedContextJson);
        AssertNoSensitiveUrlMaterial(suggestion.SampleRecordsJson);
        Assert.Contains("[redacted]", suggestion.SanitizedContextJson);
        Assert.Contains("[redacted]", suggestion.SampleRecordsJson);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ReturnsOnlyPendingSuggestionsOrderedByDuration()
    {
        using var db = CreateDbContext();
        db.Set<ActivityClassificationSuggestionEntity>().AddRange(
            NewSuggestion("web:short.example.com", "pending", 10),
            NewSuggestion("web:accepted.example.com", "accepted", 1000),
            NewSuggestion("web:rejected.example.com", "rejected", 900),
            NewSuggestion("web:long.example.com", "pending", 20));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);

        var suggestions = await service.GetSuggestionsAsync(CancellationToken.None);

        Assert.Collection(
            suggestions,
            first =>
            {
                Assert.Equal("web:long.example.com", first.ClusterKey);
                Assert.Equal("pending", first.Status);
            },
            second =>
            {
                Assert.Equal("web:short.example.com", second.ClusterKey);
                Assert.Equal("pending", second.Status);
            });
    }

    [Fact]
    public async Task BuildSuggestionsAsync_RejectedSuggestionSuppressesRecreation()
    {
        using var db = CreateDbContext();
        db.Set<ActivityClassificationSuggestionEntity>().Add(
            NewSuggestion("web:unknown.example.com", "rejected", 90));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);
        var records = new[]
        {
            NewWebRecord(
                durationSeconds: 120,
                url: "https://unknown.example.com/path",
                classificationSource: "fallback"),
            NewWebRecord(
                durationSeconds: 60,
                url: "https://unknown.example.com/other",
                classificationSource: "fallback")
        };

        var suggestions = await service.BuildSuggestionsAsync(records, CancellationToken.None);

        Assert.Empty(suggestions);
        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("web:unknown.example.com", suggestion.ClusterKey);
        Assert.Equal("rejected", suggestion.Status);
        Assert.Equal(1, suggestion.SampleCount);
        Assert.Equal(90, suggestion.TotalDurationSeconds);
    }

    [Fact]
    public async Task AcceptSuggestionAsync_CreatesActiveRuleAndMarksAccepted()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(NewSuggestion(
            suggestionId,
            "web:learn.example.com",
            "pending",
            90));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);

        var rule = await service.AcceptSuggestionAsync(suggestionId, NewAcceptRequest(), CancellationToken.None);

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
    public async Task AcceptSuggestionAsync_ThrowsForRepeatedAcceptWithoutCreatingDuplicateRule()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(NewSuggestion(
            suggestionId,
            "web:learn.example.com",
            "pending",
            90));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);
        var request = NewAcceptRequest();
        await service.AcceptSuggestionAsync(suggestionId, request, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AcceptSuggestionAsync(suggestionId, request, CancellationToken.None));

        Assert.Contains("pending", ex.Message);
        Assert.Equal(1, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("accepted", suggestion.Status);
    }

    [Fact]
    public async Task RejectSuggestionAsync_MarksSuggestionRejected()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(NewSuggestion(
            suggestionId,
            "app:unknown",
            "pending",
            45));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);

        await service.RejectSuggestionAsync(suggestionId, CancellationToken.None);

        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("rejected", suggestion.Status);
        Assert.Empty(await db.Set<ActivityCategoryRuleEntity>().ToListAsync());
    }

    [Fact]
    public async Task RejectSuggestionAsync_ThrowsForAcceptedSuggestionWithoutChangingStatus()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(NewSuggestion(
            suggestionId,
            "web:learn.example.com",
            "pending",
            90));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);
        await service.AcceptSuggestionAsync(suggestionId, NewAcceptRequest(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RejectSuggestionAsync(suggestionId, CancellationToken.None));

        Assert.Contains("pending", ex.Message);
        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("accepted", suggestion.Status);
        Assert.Equal(1, await db.Set<ActivityCategoryRuleEntity>().CountAsync());
    }

    [Fact]
    public async Task AcceptSuggestionAsync_ThrowsForRejectedSuggestionWithoutCreatingRule()
    {
        using var db = CreateDbContext();
        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(NewSuggestion(
            suggestionId,
            "web:learn.example.com",
            "rejected",
            90));
        await db.SaveChangesAsync();
        var service = new ActivitySuggestionService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AcceptSuggestionAsync(suggestionId, NewAcceptRequest(), CancellationToken.None));

        Assert.Contains("pending", ex.Message);
        Assert.Empty(await db.Set<ActivityCategoryRuleEntity>().ToListAsync());
        var suggestion = await db.Set<ActivityClassificationSuggestionEntity>().SingleAsync();
        Assert.Equal("rejected", suggestion.Status);
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationSuggestionEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PimDbContext(options);
    }

    private static ActivityClassificationSuggestionEntity NewSuggestion(
        string clusterKey,
        string status,
        double totalDurationSeconds)
    {
        return NewSuggestion(Guid.NewGuid(), clusterKey, status, totalDurationSeconds);
    }

    private static ActivityClassificationSuggestionEntity NewSuggestion(
        Guid id,
        string clusterKey,
        string status,
        double totalDurationSeconds)
    {
        return new ActivityClassificationSuggestionEntity
        {
            Id = id,
            ClusterKey = clusterKey,
            Status = status,
            SampleCount = 1,
            TotalDurationSeconds = totalDurationSeconds
        };
    }

    private static AcceptActivityClassificationSuggestionRequest NewAcceptRequest()
    {
        return new AcceptActivityClassificationSuggestionRequest(
            "Learning site",
            "activity",
            "学习",
            null,
            "#22c55e",
            200,
            """{"all":[{"field":"domain","op":"domainSuffix","value":"learn.example.com"}]}""",
            0.95,
            "Accepted from suggestion.");
    }

    private static void AssertNoSensitiveUrlMaterial(string json)
    {
        Assert.DoesNotContain("token=secret", json);
        Assert.DoesNotContain("?token=", json);
        Assert.DoesNotContain("#frag", json);
        Assert.DoesNotContain("user:password", json);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", json);
        Assert.DoesNotContain("SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", json);
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
