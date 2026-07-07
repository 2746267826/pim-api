using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileAppClassificationServiceTests
{
    [Fact]
    public async Task ClassifyAsync_UserOverrideWinsBeforeRulesAndMetadata()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var now = DateTimeOffset.Parse("2026-07-07T08:00:00Z");

        db.Set<MobileAppCatalogOverrideEntity>().Add(new MobileAppCatalogOverrideEntity
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = "com.tencent.mm",
            DisplayNameOverride = "微信手动",
            LifeCategory = MobileLifeCategories.Learning,
            IsSystemNoise = false,
            HideShortEvents = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Set<MobileAppCategoryRuleEntity>().Add(new MobileAppCategoryRuleEntity
        {
            UserId = MobileTestHelpers.UserId,
            RuleType = MobileAppClassificationService.RuleTypePackageExact,
            Pattern = "com.tencent.mm",
            LifeCategory = MobileLifeCategories.Social,
            Priority = 1000,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Set<MobileAppCatalogEntity>().Add(App("com.tencent.mm", "微信", "communication", now));
        await db.SaveChangesAsync();

        var result = await service.ClassifyAsync("com.tencent.mm");

        Assert.Equal("com.tencent.mm", result.PackageName);
        Assert.Equal("微信手动", result.DisplayName);
        Assert.Equal(MobileLifeCategories.Learning, result.LifeCategory);
        Assert.True(result.HideShortEvents);
        Assert.False(result.IsSystemNoise);
        Assert.Equal("user-override", result.Source);
    }

    [Fact]
    public async Task ClassifyAsync_UserRulesApplyExactPrefixThenKeyword()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var now = DateTimeOffset.Parse("2026-07-07T08:00:00Z");

        db.Set<MobileAppCategoryRuleEntity>().AddRange(
            Rule(MobileAppClassificationService.RuleTypeKeyword, "video", MobileLifeCategories.ShortVideoEntertainment, 900, now),
            Rule(MobileAppClassificationService.RuleTypePackagePrefix, "com.example.", MobileLifeCategories.WorkProductivity, 100, now),
            Rule(MobileAppClassificationService.RuleTypePackageExact, "com.example.reader", MobileLifeCategories.ReadingNews, 1, now));
        await db.SaveChangesAsync();

        var exact = await service.ClassifyAsync("com.example.reader");
        var prefix = await service.ClassifyAsync("com.example.notes");
        var keyword = await service.ClassifyAsync(new MobileAppClassificationInput(
            "com.creator.clip",
            DisplayName: "Video Studio"));

        Assert.Equal(MobileLifeCategories.ReadingNews, exact.LifeCategory);
        Assert.Equal("user-rule:package-exact", exact.Source);
        Assert.Equal(MobileLifeCategories.WorkProductivity, prefix.LifeCategory);
        Assert.Equal("user-rule:package-prefix", prefix.Source);
        Assert.Equal(MobileLifeCategories.ShortVideoEntertainment, keyword.LifeCategory);
        Assert.Equal("user-rule:keyword", keyword.Source);
    }

    [Fact]
    public async Task ClassifyAsync_UsesAndroidMetadataBuiltInRulesThenFallback()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var now = DateTimeOffset.Parse("2026-07-07T08:00:00Z");

        db.Set<MobileAppCatalogEntity>().Add(App("com.spotify.music", "Spotify", "audio", now));
        await db.SaveChangesAsync();

        var metadata = await service.ClassifyAsync("com.spotify.music");
        var builtIn = await service.ClassifyAsync("com.tencent.mobileqq");
        var fallback = await service.ClassifyAsync("com.unknown.app");

        Assert.Equal(MobileLifeCategories.MusicAudio, metadata.LifeCategory);
        Assert.Equal("android-metadata", metadata.Source);
        Assert.Equal(MobileLifeCategories.Social, builtIn.LifeCategory);
        Assert.Equal("built-in-package", builtIn.Source);
        Assert.Equal(MobileLifeCategories.Uncategorized, fallback.LifeCategory);
        Assert.Equal("fallback", fallback.Source);
    }

    [Theory]
    [InlineData("0", MobileLifeCategories.Game)]
    [InlineData("1", MobileLifeCategories.MusicAudio)]
    [InlineData("2", MobileLifeCategories.ShortVideoEntertainment)]
    [InlineData("4", MobileLifeCategories.Social)]
    [InlineData("7", MobileLifeCategories.WorkProductivity)]
    public async Task ClassifyAsync_MapsLegacyAndroidNumericCategories(string androidCategory, string expectedCategory)
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);

        var result = await service.ClassifyAsync(new MobileAppClassificationInput(
            "com.example.android.category",
            DisplayName: "Android Category",
            AndroidCategory: androidCategory));

        Assert.Equal(expectedCategory, result.LifeCategory);
        Assert.Equal("android-metadata", result.Source);
    }

    [Theory]
    [InlineData("com.android.systemui", null)]
    [InlineData("com.google.android.inputmethod.latin", null)]
    [InlineData("com.miui.home", "launcher")]
    public async Task ClassifyAsync_MarksSystemNoisePackages(string packageName, string? androidCategory)
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);

        var result = await service.ClassifyAsync(new MobileAppClassificationInput(
            packageName,
            AndroidCategory: androidCategory,
            IsSystemApp: true));

        Assert.True(result.IsSystemNoise);
        Assert.True(result.HideShortEvents);
        Assert.Equal(MobileLifeCategories.ToolsSystem, result.LifeCategory);
    }

    [Fact]
    public async Task ClassifyAsync_ResolvesDisplayNameFromLatestMetadataBuiltInFriendlyNameThenPackage()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = Service(db);
        var old = DateTimeOffset.Parse("2026-07-01T08:00:00Z");
        var latest = DateTimeOffset.Parse("2026-07-07T08:00:00Z");

        db.Set<MobileAppCatalogEntity>().AddRange(
            App("com.example.reader", "Reader Old", "productivity", old),
            App("com.example.reader", "Reader Latest", "productivity", latest, "android-secondary"));
        await db.SaveChangesAsync();

        var metadata = await service.ClassifyAsync("com.example.reader");
        var builtIn = await service.ClassifyAsync("com.tencent.mm");
        var fallback = await service.ClassifyAsync("com.unknown.no.name");

        Assert.Equal("Reader Latest", metadata.DisplayName);
        Assert.Equal("微信", builtIn.DisplayName);
        Assert.Equal("com.unknown.no.name", fallback.DisplayName);
    }

    private static MobileAppClassificationService Service(DbContext db)
        => new((Pim.Infrastructure.Data.PimDbContext)db, MobileTestHelpers.CurrentUser());

    private static MobileAppCategoryRuleEntity Rule(
        string ruleType,
        string pattern,
        string lifeCategory,
        int priority,
        DateTimeOffset now)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            RuleType = ruleType,
            Pattern = pattern,
            LifeCategory = lifeCategory,
            Priority = priority,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static MobileAppCatalogEntity App(
        string packageName,
        string displayName,
        string? category,
        DateTimeOffset updatedAt,
        string deviceId = "android-main")
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            PackageName = packageName,
            DisplayName = displayName,
            Category = category,
            InstallerPackage = "com.android.vending",
            RawJson = "{}",
            CreatedAt = updatedAt.AddDays(-1),
            UpdatedAt = updatedAt
        };
}
