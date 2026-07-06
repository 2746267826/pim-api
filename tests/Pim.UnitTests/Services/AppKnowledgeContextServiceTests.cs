using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class AppKnowledgeContextServiceTests
{
    [Fact]
    public async Task SaveAsync_CreatesDomainContextWithScopeSummaryAndDefaults()
    {
        await using var db = CreateDb();
        var app = new AppSignatureEntity
        {
            Id = Guid.NewGuid(),
            ProcessName = "chrome.exe",
            DisplayName = "Google Chrome",
            Source = "builtin",
            Confidence = 0.99,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Set<AppSignatureEntity>().Add(app);
        await db.SaveChangesAsync();
        var service = new AppKnowledgeContextService(db);

        var result = await service.SaveAsync(new SaveAppKnowledgeContextRequest(
            app.Id,
            " chrome.exe ",
            " domain ",
            " docs.example.com ",
            " Research ",
            " PIM ",
            null,
            null), CancellationToken.None);

        Assert.Equal(app.Id, result.AppId);
        Assert.Equal("chrome.exe", result.ProcessName);
        Assert.Equal("domain", result.PatternType);
        Assert.Equal("docs.example.com", result.PatternValue);
        Assert.Equal("Research", result.TargetCategoryName);
        Assert.Equal("PIM", result.ProjectTag);
        Assert.Equal("Google Chrome - domain: docs.example.com", result.ScopeSummary);
        Assert.Equal("user-confirmed", result.Source);
        Assert.Equal(1.0, result.Confidence);
        Assert.True(result.Enabled);

        var entity = Assert.Single(db.Set<AppKnowledgeContextEntity>());
        Assert.Equal(app.Id, entity.AppSignatureId);
        Assert.Equal(result.ScopeSummary, entity.ScopeSummary);
    }

    [Fact]
    public async Task SaveAsync_UpsertsByTrimmedAppPattern()
    {
        await using var db = CreateDb();
        var service = new AppKnowledgeContextService(db);

        var first = await service.SaveAsync(new SaveAppKnowledgeContextRequest(
            null,
            " chrome.exe ",
            " domain ",
            " docs.example.com ",
            " Research ",
            null,
            null,
            null), CancellationToken.None);

        var second = await service.SaveAsync(new SaveAppKnowledgeContextRequest(
            null,
            "chrome.exe",
            "domain",
            "docs.example.com",
            "Documentation",
            "Client A",
            0.8,
            false), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Documentation", second.TargetCategoryName);
        Assert.Equal("Client A", second.ProjectTag);
        Assert.Equal(0.8, second.Confidence);
        Assert.False(second.Enabled);
        Assert.Single(db.Set<AppKnowledgeContextEntity>());
    }

    [Fact]
    public async Task GetByAppAsync_ReturnsOnlyContextsForOneApp()
    {
        await using var db = CreateDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        db.Set<AppKnowledgeContextEntity>().AddRange(
            NewContext(chromeId, "chrome.exe", "docs.example.com"),
            NewContext(edgeId, "msedge.exe", "learn.example.com"),
            NewContext(null, "chrome.exe", "unlinked.example.com"));
        await db.SaveChangesAsync();
        var service = new AppKnowledgeContextService(db);

        var contexts = await service.GetByAppAsync(chromeId, CancellationToken.None);

        var context = Assert.Single(contexts);
        Assert.Equal(chromeId, context.AppId);
        Assert.Equal("docs.example.com", context.PatternValue);
    }

    [Fact]
    public async Task GetKnowledgeAppsAsync_ReturnsContextCountsAndRecentAffectedDuration()
    {
        await using var db = CreateDb();
        var chrome = NewApp("chrome.exe", "Google Chrome");
        var edge = NewApp("msedge.exe", "Microsoft Edge");
        db.Set<AppSignatureEntity>().AddRange(chrome, edge);
        db.Set<AppKnowledgeContextEntity>().AddRange(
            NewContext(chrome.Id, "chrome.exe", "docs.example.com", 30),
            NewContext(chrome.Id, "chrome.exe", "mail.example.com", 15),
            NewContext(edge.Id, "msedge.exe", "learn.example.com", 100));
        await db.SaveChangesAsync();
        var service = new AppSignatureService(db);

        var apps = await service.GetKnowledgeAppsAsync("chrome", CancellationToken.None);

        var app = Assert.Single(apps);
        Assert.Equal(chrome.Id, app.Id);
        Assert.Equal(2, app.ContextCount);
        Assert.Equal(0, app.PendingContextCount);
        Assert.Equal(45, app.RecentAffectedDurationSeconds);
    }

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

    private static AppKnowledgeContextEntity NewContext(
        Guid? appId,
        string processName,
        string patternValue,
        double affectedDurationSeconds = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            AppSignatureId = appId,
            ProcessName = processName,
            PatternType = "domain",
            PatternValue = patternValue,
            ScopeSummary = $"{processName} - domain: {patternValue}",
            Source = "user-confirmed",
            Confidence = 1.0,
            Enabled = true,
            AffectedDurationSeconds = affectedDurationSeconds,
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
