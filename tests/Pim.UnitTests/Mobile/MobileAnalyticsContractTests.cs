using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileAnalyticsContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void Defaults_ExposeExpectedTimezonePagingAndLifeCategories()
    {
        Assert.Equal("Asia/Shanghai", MobileAnalyticsDefaults.DefaultTimezone);
        Assert.Equal(50, MobileAnalyticsDefaults.DefaultPageSize);
        Assert.Equal(200, MobileAnalyticsDefaults.MaxPageSize);
        Assert.Equal(1, MobileAnalyticsDefaults.DefaultShortEventThresholdSeconds);

        string[] expectedCategories =
        [
            "社交通讯",
            "短视频/娱乐",
            "游戏",
            "音乐/音频",
            "阅读/资讯",
            "学习",
            "工作/生产力",
            "工具/系统",
            "浏览器/搜索",
            "出行/地图",
            "购物/外卖",
            "金融/支付",
            "健康/运动",
            "相机/创作",
            "生活服务",
            "未分类"
        ];

        Assert.Equal(expectedCategories, MobileLifeCategories.All);
        Assert.Equal(MobileLifeCategories.All, MobileAnalyticsDefaults.LifeCategories);
    }

    [Fact]
    public void OverrideEntity_IsUserGlobalByPackageName()
    {
        var entity = new MobileAppCatalogOverrideEntity
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = "com.tencent.mobileqq",
            DisplayNameOverride = "QQ",
            LifeCategory = MobileLifeCategories.Social,
            IsSystemNoise = false,
            HideShortEvents = false
        };

        Assert.Equal("com.tencent.mobileqq", entity.PackageName);
        Assert.Equal("QQ", entity.DisplayNameOverride);
        Assert.Equal("社交通讯", entity.LifeCategory);
    }

    [Fact]
    public void OverviewResponse_SerializesQualityGoalAnomalyAndSuggestionState()
    {
        var response = new MobileAnalyticsOverviewResponse(
            new MobileAnalyticsRangeDto(
                DateTimeOffset.Parse("2026-07-01T16:00:00Z"),
                DateTimeOffset.Parse("2026-07-08T16:00:00Z"),
                "Asia/Shanghai",
                "2026-07-02",
                "2026-07-08"),
            DateTimeOffset.Parse("2026-07-08T10:00:00Z"),
            false,
            3600,
            600,
            0.25,
            "2026-07-08",
            21,
            12,
            42,
            0.94,
            new MobileAnalyticsQualitySummaryDto(
                0.92,
                0.08,
                1,
                0.03,
                0.02,
                0,
                DateTimeOffset.Parse("2026-07-08T09:59:00Z"),
                []),
            new MobileGoalProgressDto("total-daily", "每日手机总时长", 14400, 3600, false, 10800),
            [new MobileAnomalyDto("night-use", "Warning", "夜间使用偏高", "22:00 后使用增加", "heatmap:night")],
            [new MobileSuggestionDto("short-video-night", "短视频/娱乐集中在 22:00 后", "category:短视频/娱乐")]);

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("\"timezone\":\"Asia/Shanghai\"", json);
        Assert.Contains("\"goalProgress\"", json);
        Assert.Contains("每日手机总时长", json);
        Assert.Contains("夜间使用偏高", json);
        Assert.Contains("短视频/娱乐集中在 22:00 后", json);
    }

    [Fact]
    public void MobileAnalyticsEntities_AreRegisteredWithPracticalIndexesAndLengths()
    {
        using var db = MobileTestHelpers.CreateDb();
        var entityTypes = db.Model.GetEntityTypes().Select(entity => entity.ClrType).ToHashSet();

        Assert.Contains(typeof(MobileAppCatalogOverrideEntity), entityTypes);
        Assert.Contains(typeof(MobileAppCategoryRuleEntity), entityTypes);
        Assert.Contains(typeof(MobileUsageAggregateEntity), entityTypes);
        Assert.Contains(typeof(MobileTimelineBlockEntity), entityTypes);
        Assert.Contains(typeof(MobileUsageGoalEntity), entityTypes);

        Assert.True(Index<MobileAppCatalogOverrideEntity>(
            db,
            nameof(MobileAppCatalogOverrideEntity.UserId),
            nameof(MobileAppCatalogOverrideEntity.PackageName)).IsUnique);
        Assert.True(Index<MobileAppCategoryRuleEntity>(
            db,
            nameof(MobileAppCategoryRuleEntity.UserId),
            nameof(MobileAppCategoryRuleEntity.RuleType),
            nameof(MobileAppCategoryRuleEntity.Pattern)).IsUnique);
        Assert.True(Index<MobileUsageAggregateEntity>(
            db,
            nameof(MobileUsageAggregateEntity.UserId),
            nameof(MobileUsageAggregateEntity.DeviceId),
            nameof(MobileUsageAggregateEntity.Granularity),
            nameof(MobileUsageAggregateEntity.BucketStartUtc),
            nameof(MobileUsageAggregateEntity.BucketEndUtc),
            nameof(MobileUsageAggregateEntity.PackageName),
            nameof(MobileUsageAggregateEntity.LifeCategory)).IsUnique);
        Assert.True(Index<MobileUsageGoalEntity>(
            db,
            nameof(MobileUsageGoalEntity.UserId),
            nameof(MobileUsageGoalEntity.Scope),
            nameof(MobileUsageGoalEntity.PackageName),
            nameof(MobileUsageGoalEntity.LifeCategory)).IsUnique);

        Assert.Equal(256, Property<MobileAppCatalogOverrideEntity>(db, nameof(MobileAppCatalogOverrideEntity.PackageName)).GetMaxLength());
        Assert.Equal(128, Property<MobileAppCatalogOverrideEntity>(db, nameof(MobileAppCatalogOverrideEntity.LifeCategory)).GetMaxLength());
        Assert.Equal(512, Property<MobileAppCategoryRuleEntity>(db, nameof(MobileAppCategoryRuleEntity.Pattern)).GetMaxLength());
        Assert.Equal(64, Property<MobileUsageAggregateEntity>(db, nameof(MobileUsageAggregateEntity.Timezone)).GetMaxLength());
        Assert.Equal(128, Property<MobileUsageGoalEntity>(db, nameof(MobileUsageGoalEntity.LifeCategory)).GetMaxLength());
    }

    private static IProperty Property<TEntity>(DbContext db, string propertyName)
    {
        var property = Entity<TEntity>(db).FindProperty(propertyName);
        Assert.NotNull(property);
        return property;
    }

    private static IEntityType Entity<TEntity>(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        return entity;
    }

    private static IIndex Index<TEntity>(DbContext db, params string[] propertyNames)
    {
        var index = Entity<TEntity>(db)
            .GetIndexes()
            .SingleOrDefault(candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.NotNull(index);
        return index;
    }
}
