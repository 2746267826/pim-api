using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationRuleServiceTests
{
    [Fact]
    public async Task SaveAsync_NormalizesAppScopeAndRequiresKnownCategory()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = "Programming", Color = "#2563eb" });
        await db.SaveChangesAsync();
        var service = new ActivityClassificationRuleService(db);

        var rule = await service.SaveAsync(NewRule() with { Scope = "app", CategoryName = "Programming" }, CancellationToken.None);

        Assert.Equal("activity", rule.Scope);
        Assert.Equal("Programming", rule.CategoryName);
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownCategory()
    {
        await using var db = CreateDb();
        var service = new ActivityClassificationRuleService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(NewRule() with { CategoryName = "Missing" }, CancellationToken.None));

        Assert.Contains("CategoryName", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_RejectsDuplicateRuleName()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity { Id = Guid.NewGuid(), Name = "Programming", Color = "#2563eb" });
        db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = "Code windows",
            Scope = "activity",
            CategoryName = "Programming",
            Status = "active",
            ConditionsJson = """{"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}"""
        });
        await db.SaveChangesAsync();
        var service = new ActivityClassificationRuleService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(NewRule(), CancellationToken.None));
    }

    private static SaveActivityClassificationRuleRequest NewRule() =>
        new(
            "Code windows",
            "activity",
            "Programming",
            null,
            "#2563eb",
            900,
            """{"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}""",
            0.95,
            "Matched Code.");

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityCategoryRuleEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
