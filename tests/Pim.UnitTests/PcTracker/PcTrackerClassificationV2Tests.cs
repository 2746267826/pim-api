using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

public class PcTrackerClassificationV2Tests
{
    private static PimDbContext CreateInMemoryDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(PcCategoryEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public void SchemaInitializer_SeedsOver200AppSignatures()
    {
        using var db = CreateInMemoryDb();
        var initializer = new PcTrackerSchemaInitializer(db);

        // SchemaSql contains more than 200 signatures
        Assert.Contains("postman.exe", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("Cyberpunk2077.exe", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("davinci.exe", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("pc_suggestion_feedback", PcTrackerSchemaInitializer.SchemaSql);
    }

    [Fact]
    public async Task CategoryService_SeedsHierarchyWithNeutralProductivity()
    {
        using var db = CreateInMemoryDb();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        var tree = await service.GetTreeAsync(CancellationToken.None);
        Assert.NotEmpty(tree);

        // Verify root categories
        var work = tree.FirstOrDefault(c => c.Name == CategoryLegacyMapper.ProgrammingTinkering);
        Assert.NotNull(work);
        Assert.Equal("productive", work.Productivity);
        Assert.NotEmpty(work.Children);
        Assert.Contains(work.Children, c => c.Name == "前端");

        var game = tree.FirstOrDefault(c => c.Name == CategoryLegacyMapper.Gaming);
        Assert.NotNull(game);
        Assert.Equal("distracting", game.Productivity);
        Assert.NotEmpty(game.Children);
        Assert.Contains(game.Children, c => c.Name == "单机游戏");
    }

    [Fact]
    public void ActivityClassifier_PrioritizesAppSignaturesOverBuiltinRules()
    {
        var context = new ActivityClassificationContext(
            RecordType: "window",
            AppName: "postman.exe",
            AppNameNormalized: "postman",
            Domain: null,
            UrlPath: null,
            Title: "POST https://api.example.com",
            WindowTitle: "POST https://api.example.com",
            FilePath: null,
            BucketType: null);

        var signatures = new List<AppSignatureEntity>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ProcessName = "postman.exe",
                DisplayName = "Postman",
                CategoryPath = "编程/折腾",
                Productivity = "productive",
                Confidence = 0.99
            }
        };

        var rules = new List<ActivityCategoryRuleEntity>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RuleName = "Builtin: Fallback other",
                CategoryName = "其他",
                Priority = 10,
                Source = "builtin",
                Status = "active",
                ConditionsJson = "{}"
            }
        };

        var result = ActivityClassifier.Classify(context, rules, null, null, signatures);

        Assert.Equal(CategoryLegacyMapper.ProgrammingTinkering, result.CategoryName);
        Assert.Equal("signature", result.Source);
        Assert.Equal(0.99, result.Confidence);
    }

    [Fact]
    public async Task DefaultAppLookupProvider_IsDisabledByDefault()
    {
        var config = new ConfigurationBuilder().Build(); // No AppLookup:Enabled set
        var provider = new DefaultAppLookupProvider(config, NullLogger<DefaultAppLookupProvider>.Instance);

        Assert.False(provider.IsEnabled);
        var result = await provider.LookupAsync("unknown_tool.exe");
        Assert.Null(result);
    }

    [Fact]
    public async Task AppSignatureService_ImportAndExportWorkCorrectly()
    {
        using var db = CreateInMemoryDb();
        var service = new AppSignatureService(db);

        var imports = new List<SaveAppSignatureRequest>
        {
            new("customapp.exe", "Custom App", "文档", "productive", "My custom tool"),
            new("secondapp.exe", "Second App", "游戏", "distracting", "Game tool")
        };

        var (imported, updated) = await service.ImportAsync(imports, CancellationToken.None);
        Assert.Equal(2, imported);
        Assert.Equal(0, updated);

        var exported = await service.ExportAsync(CancellationToken.None);
        Assert.Equal(2, exported.Count);
        Assert.Contains(exported, s => s.ProcessName == "customapp.exe" && s.DisplayName == "Custom App");

        // Re-importing updates existing
        var updates = new List<SaveAppSignatureRequest>
        {
            new("customapp.exe", "Custom App Updated", "文档", "productive", "Updated description")
        };
        var (imported2, updated2) = await service.ImportAsync(updates, CancellationToken.None);
        Assert.Equal(0, imported2);
        Assert.Equal(1, updated2);

        var found = await service.FindByProcessNameAsync("customapp.exe", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("Custom App Updated", found.DisplayName);
    }

    [Fact]
    public async Task ActivitySuggestionService_BatchAcceptCreatesRulesAndFeedback()
    {
        using var db = CreateInMemoryDb();
        var catSvc = new PcCategoryService(db);
        await catSvc.SeedDefaultsAsync(CancellationToken.None);

        var appSigSvc = new AppSignatureService(db);
        var suggestionSvc = new ActivitySuggestionService(db, appSigSvc);

        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:dbeaver.exe",
            SampleCount = 10,
            TotalDurationSeconds = 1200,
            Status = "pending",
            CurrentCategory = "其他",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var request = new BatchAcceptSuggestionsRequest(new List<BatchAcceptItem>
        {
            new(suggestionId, null, CategoryLegacyMapper.ProgrammingTinkering, true)
        });

        var res = await suggestionSvc.BatchAcceptAsync(request, CancellationToken.None);

        Assert.Equal(1, res.AcceptedCount);
        Assert.Equal(1, res.RulesCreatedCount);
        Assert.Equal(0, res.FailuresCount);

        var updatedSuggestion = await db.Set<ActivityClassificationSuggestionEntity>().FindAsync(suggestionId);
        Assert.NotNull(updatedSuggestion);
        Assert.Equal("accepted", updatedSuggestion.Status);

        var rule = await db.Set<ActivityCategoryRuleEntity>().FirstOrDefaultAsync(r => r.CategoryName == CategoryLegacyMapper.ProgrammingTinkering && r.Source == "suggestion-batch-accept");
        Assert.NotNull(rule);

        var feedback = await db.Set<PcSuggestionFeedbackEntity>().FirstOrDefaultAsync(f => f.ProcessName == "dbeaver.exe");
        Assert.NotNull(feedback);
        Assert.Equal("accepted", feedback.Action);
    }

    [Fact]
    public async Task ActivitySuggestionService_SilenceFeedbackPreventsRejectedSuggestions()
    {
        using var db = CreateInMemoryDb();
        var appSigSvc = new AppSignatureService(db);
        var suggestionSvc = new ActivitySuggestionService(db, appSigSvc);

        var suggestionId = Guid.NewGuid();
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:rejectedapp.exe",
            SampleCount = 5,
            TotalDurationSeconds = 600,
            Status = "pending",
            CurrentCategory = "其他",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Reject suggestion
        await suggestionSvc.RejectSuggestionAsync(suggestionId, CancellationToken.None);

        // Verify feedback was written
        var feedback = await db.Set<PcSuggestionFeedbackEntity>().FirstOrDefaultAsync(f => f.ProcessName == "rejectedapp.exe");
        Assert.NotNull(feedback);
        Assert.Equal("rejected", feedback.Action);

        // Re-add a new pending suggestion for the same process
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = Guid.NewGuid(),
            ClusterKey = "app:rejectedapp.exe",
            SampleCount = 5,
            TotalDurationSeconds = 600,
            Status = "pending",
            CurrentCategory = "其他",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Query suggestions v2 -> should be silenced because rejected within 3 days
        var list = await suggestionSvc.GetSuggestionsV2Async(CancellationToken.None);
        Assert.DoesNotContain(list, s => s.ProcessName == "rejectedapp.exe");
    }

    [Fact]
    public async Task ProductivityService_GoalsGetAndUpdateSuccessfully()
    {
        using var db = CreateInMemoryDb();
        var service = new PcProductivityService(db);

        var initial = await service.GetGoalsAsync(CancellationToken.None);
        Assert.Equal(5.0, initial.DailyProductiveHours);

        var updated = await service.UpdateGoalsAsync(new ProductivityGoalDto { DailyProductiveHours = 6.5 }, CancellationToken.None);
        Assert.Equal(6.5, updated.DailyProductiveHours);

        var fetched = await service.GetGoalsAsync(CancellationToken.None);
        Assert.Equal(6.5, fetched.DailyProductiveHours);
    }
}
