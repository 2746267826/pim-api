using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileAppCatalogOverrideServiceTests
{
    [Fact]
    public async Task UpsertOverrideAsync_CreatesAndUpdatesUserGlobalOverrideByPackageName()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);

        var created = await service.UpsertOverrideAsync(new MobileAppCatalogOverrideUpsertRequest(
            " COM.EXAMPLE.APP ",
            "Example",
            MobileLifeCategories.Chat,
            false,
            false));

        var updated = await service.UpsertOverrideAsync(new MobileAppCatalogOverrideUpsertRequest(
            "com.example.app",
            "Study App",
            MobileLifeCategories.Learning,
            true,
            true));

        Assert.Equal("com.example.app", created.PackageName);
        Assert.Equal("com.example.app", updated.PackageName);
        Assert.Equal("Study App", updated.DisplayNameOverride);
        Assert.Equal(MobileLifeCategories.Learning, updated.LifeCategory);
        Assert.True(updated.IsSystemNoise);
        Assert.True(updated.HideShortEvents);
        Assert.Equal(1, await db.Set<MobileAppCatalogOverrideEntity>().CountAsync());
    }

    [Fact]
    public async Task DeleteAndClearOverrides_RemoveOnlyCurrentUserOverrides()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var now = DateTimeOffset.Parse("2026-07-07T08:00:00Z");
        var otherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        db.Set<MobileAppCatalogOverrideEntity>().AddRange(
            Override(MobileTestHelpers.UserId, "com.example.one", now),
            Override(MobileTestHelpers.UserId, "com.example.two", now),
            Override(otherUserId, "com.example.one", now));
        await db.SaveChangesAsync();

        Assert.True(await service.DeleteOverrideAsync("com.example.one"));
        var afterDelete = await service.ListOverridesAsync();
        Assert.Single(afterDelete);
        Assert.Equal("com.example.two", afterDelete[0].PackageName);

        var cleared = await service.ClearOverridesAsync();

        Assert.Equal(1, cleared);
        var remaining = Assert.Single(await db.Set<MobileAppCatalogOverrideEntity>().ToListAsync());
        Assert.Equal(otherUserId, remaining.UserId);
        Assert.Equal("com.example.one", remaining.PackageName);
    }

    [Fact]
    public async Task CategoryRuleCrud_ListsCreatesUpdatesAndDeletesRules()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);

        var low = await service.CreateCategoryRuleAsync(new MobileAppCategoryRuleUpsertRequest(
            MobileAppClassificationService.RuleTypePackagePrefix,
            "com.example.",
            MobileLifeCategories.Documents,
            100,
            true,
            "Example Suite",
            false));
        var high = await service.CreateCategoryRuleAsync(new MobileAppCategoryRuleUpsertRequest(
            MobileAppClassificationService.RuleTypeKeyword,
            "pay",
            MobileLifeCategories.Other,
            900,
            true));

        var listed = await service.ListCategoryRulesAsync();
        var updated = await service.UpdateCategoryRuleAsync(low.Id, new MobileAppCategoryRuleUpsertRequest(
            MobileAppClassificationService.RuleTypePackageExact,
            "com.example.study",
            MobileLifeCategories.Learning,
            950,
            false,
            "Study Suite",
            true));
        var deleted = await service.DeleteCategoryRuleAsync(high.Id);

        Assert.Collection(
            listed,
            first => Assert.Equal(high.Id, first.Id),
            second => Assert.Equal(low.Id, second.Id));
        Assert.Equal(MobileAppClassificationService.RuleTypePackageExact, updated.RuleType);
        Assert.Equal("com.example.study", updated.Pattern);
        Assert.Equal(MobileLifeCategories.Learning, updated.LifeCategory);
        Assert.Equal(950, updated.Priority);
        Assert.False(updated.IsEnabled);
        Assert.Equal("Study Suite", updated.DisplayNameOverride);
        Assert.True(updated.IsSystemNoise);
        Assert.True(deleted);
        var remaining = Assert.Single(await service.ListCategoryRulesAsync());
        Assert.Equal(low.Id, remaining.Id);
    }

    [Fact]
    public async Task MarkAnalyticsStaleAsync_MarksAffectedAggregatesAndTimelineBlocksForPackageAndRange()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var start = DateTimeOffset.Parse("2026-07-07T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-08T00:00:00Z");

        db.Set<MobileUsageAggregateEntity>().AddRange(
            Aggregate("com.example.app", start.AddHours(1), start.AddHours(2)),
            Aggregate("com.other.app", start.AddHours(1), start.AddHours(2)),
            Aggregate("com.example.app", start.AddDays(-2), start.AddDays(-2).AddHours(1)));
        db.Set<MobileTimelineBlockEntity>().AddRange(
            TimelineBlock("com.example.app", start.AddHours(1), start.AddHours(2)),
            TimelineBlock("com.other.app", start.AddHours(1), start.AddHours(2)),
            TimelineBlock("com.example.app", start.AddDays(-2), start.AddDays(-2).AddHours(1)));
        await db.SaveChangesAsync();

        var result = await service.MarkAnalyticsStaleAsync("com.example.app", start, end);

        Assert.Equal(1, result.AggregatesMarked);
        Assert.Equal(1, result.TimelineBlocksMarked);
        Assert.Equal(1, await db.Set<MobileUsageAggregateEntity>()
            .CountAsync(a => a.PackageName == "com.example.app" && a.IsStale));
        Assert.Equal(1, await db.Set<MobileTimelineBlockEntity>()
            .CountAsync(t => t.TopAppsJson.Contains("com.example.app") && t.IsStale));
        Assert.False(await db.Set<MobileUsageAggregateEntity>()
            .AnyAsync(a => a.PackageName == "com.other.app" && a.IsStale));
    }

    private static MobileAppCatalogOverrideService Service(DbContext db)
        => new((Pim.Infrastructure.Data.PimDbContext)db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-07T08:00:00Z")));

    private static MobileAppCatalogOverrideEntity Override(Guid userId, string packageName, DateTimeOffset now)
        => new()
        {
            UserId = userId,
            PackageName = packageName,
            DisplayNameOverride = packageName,
            LifeCategory = MobileLifeCategories.Chat,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static MobileUsageAggregateEntity Aggregate(
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            Granularity = "hour",
            BucketStartUtc = start,
            BucketEndUtc = end,
            PackageName = packageName,
            DisplayName = packageName,
            LifeCategory = MobileLifeCategories.Chat,
            ForegroundSeconds = 60,
            CreatedAt = start,
            UpdatedAt = start
        };

    private static MobileTimelineBlockEntity TimelineBlock(
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            StartUtc = start,
            EndUtc = end,
            LocalDate = "2026-07-07",
            LifeCategory = MobileLifeCategories.Chat,
            ForegroundSeconds = 60,
            SessionCount = 1,
            AppCount = 1,
            TopAppsJson = $"[{{\"packageName\":\"{packageName}\",\"displayName\":\"{packageName}\",\"foregroundSeconds\":60}}]",
            SourceMixJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = start,
            UpdatedAt = start
        };
}
