using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

public sealed class MobileDeviceEntityConfiguration : IEntityTypeConfiguration<MobileDeviceEntity>
{
    public void Configure(EntityTypeBuilder<MobileDeviceEntity> builder)
    {
        builder.Property(e => e.MetadataJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.LastSeenAtUtc });
    }
}

public sealed class MobileForensicEventEntityConfiguration : IEntityTypeConfiguration<MobileForensicEventEntity>
{
    public void Configure(EntityTypeBuilder<MobileForensicEventEntity> builder)
    {
        builder.Property(e => e.PayloadJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        // 幂等键（AC-5.2）：同一设备重复提交同一批事件时命中唯一约束被跳过，事件条数不变。
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.ClientItemKey }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.EventType, e.OccurredAtUtc });
    }
}

public sealed class MobileDroppedReasonDailyEntityConfiguration : IEntityTypeConfiguration<MobileDroppedReasonDailyEntity>
{
    public void Configure(EntityTypeBuilder<MobileDroppedReasonDailyEntity> builder)
    {
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        // 天然键（AC-9.2）：同一天同一原因只有一行，重复上报走覆盖而不是累加。
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.LocalDate, e.Reason }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.LocalDate });
    }
}

public sealed class MobileAppCatalogEntityConfiguration : IEntityTypeConfiguration<MobileAppCatalogEntity>
{
    public void Configure(EntityTypeBuilder<MobileAppCatalogEntity> builder)
    {
        builder.Property(e => e.RawJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.PackageName }).IsUnique();
    }
}

public sealed class MobileUsageEventEntityConfiguration : IEntityTypeConfiguration<MobileUsageEventEntity>
{
    public void Configure(EntityTypeBuilder<MobileUsageEventEntity> builder)
    {
        builder.Property(e => e.RawJson).HasDefaultValue("{}");
        builder.Property(e => e.QualityFlagsJson).HasDefaultValue("[]");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new
        {
            e.UserId,
            e.DeviceId,
            e.PackageName,
            e.EventType,
            e.EventTimestampUtc,
            e.ClassName
        }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.EventTimestampUtc });
    }
}

public sealed class MobileUsageSummaryEntityConfiguration : IEntityTypeConfiguration<MobileUsageSummaryEntity>
{
    public void Configure(EntityTypeBuilder<MobileUsageSummaryEntity> builder)
    {
        builder.Property(e => e.RawJson).HasDefaultValue("{}");
        builder.Property(e => e.QualityFlagsJson).HasDefaultValue("[]");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new
        {
            e.UserId,
            e.DeviceId,
            e.PackageName,
            e.WindowStartUtc,
            e.WindowEndUtc,
            e.SourceKind
        }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.WindowStartUtc });
    }
}

public sealed class MobileUsageSessionEntityConfiguration : IEntityTypeConfiguration<MobileUsageSessionEntity>
{
    public void Configure(EntityTypeBuilder<MobileUsageSessionEntity> builder)
    {
        builder.Property(e => e.QualityFlagsJson).HasDefaultValue("[]");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.StartUtc });
        builder.HasIndex(e => new { e.UserId, e.PackageName, e.StartUtc });
    }
}

public sealed class MobileLocationPointEntityConfiguration : IEntityTypeConfiguration<MobileLocationPointEntity>
{
    public void Configure(EntityTypeBuilder<MobileLocationPointEntity> builder)
    {
        builder.Property(e => e.Latitude).HasPrecision(10, 7);
        builder.Property(e => e.Longitude).HasPrecision(10, 7);
        builder.Property(e => e.HorizontalAccuracyMeters).HasPrecision(9, 2);
        builder.Property(e => e.AltitudeMeters).HasPrecision(10, 2);
        builder.Property(e => e.VerticalAccuracyMeters).HasPrecision(9, 2);
        builder.Property(e => e.SpeedMetersPerSecond).HasPrecision(9, 2);
        builder.Property(e => e.SpeedAccuracyMetersPerSecond).HasPrecision(9, 2);
        builder.Property(e => e.BearingDegrees).HasPrecision(6, 2);
        builder.Property(e => e.BearingAccuracyDegrees).HasPrecision(6, 2);
        builder.Property(e => e.Quality).HasDefaultValue("usable");
        builder.Property(e => e.RawJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        // 天然键（#246）：同一设备、同一时刻、同一坐标只允许一行。
        // 否则客户端重试/批量补传会静默叠加重复点，污染停留判定、里程与常去地点。
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.RecordedAtUtc, e.Latitude, e.Longitude }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.RecordedAtUtc });
        builder.HasIndex(e => new { e.UserId, e.Quality, e.RecordedAtUtc });
    }
}

public sealed class MobileSyncBatchEntityConfiguration : IEntityTypeConfiguration<MobileSyncBatchEntity>
{
    public void Configure(EntityTypeBuilder<MobileSyncBatchEntity> builder)
    {
        builder.Property(e => e.Status).HasDefaultValue("completed");
        builder.Property(e => e.ErrorJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.BatchId }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.CreatedAt });
    }
}

public sealed class MobileAppCatalogOverrideEntityConfiguration : IEntityTypeConfiguration<MobileAppCatalogOverrideEntity>
{
    public void Configure(EntityTypeBuilder<MobileAppCatalogOverrideEntity> builder)
    {
        builder.Property(e => e.LifeCategory).HasDefaultValue(MobileLifeCategories.Uncategorized);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.PackageName }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.LifeCategory });
        builder.HasIndex(e => new { e.UserId, e.IsSystemNoise });
    }
}

public sealed class MobileAppCategoryRuleEntityConfiguration : IEntityTypeConfiguration<MobileAppCategoryRuleEntity>
{
    public void Configure(EntityTypeBuilder<MobileAppCategoryRuleEntity> builder)
    {
        builder.Property(e => e.RuleType).HasDefaultValue("package-exact");
        builder.Property(e => e.LifeCategory).HasDefaultValue(MobileLifeCategories.Uncategorized);
        builder.Property(e => e.Priority).HasDefaultValue(100);
        builder.Property(e => e.IsEnabled).HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.RuleType, e.Pattern }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.IsEnabled, e.Priority });
        builder.HasIndex(e => new { e.UserId, e.LifeCategory });
    }
}

public sealed class MobileUsageAggregateEntityConfiguration : IEntityTypeConfiguration<MobileUsageAggregateEntity>
{
    public void Configure(EntityTypeBuilder<MobileUsageAggregateEntity> builder)
    {
        builder.Property(e => e.DeviceId).HasDefaultValue(string.Empty);
        builder.Property(e => e.Granularity).HasDefaultValue("hour");
        builder.Property(e => e.Timezone).HasDefaultValue(MobileAnalyticsDefaults.DefaultTimezone);
        builder.Property(e => e.PackageName).HasDefaultValue(string.Empty);
        builder.Property(e => e.DisplayName).HasDefaultValue(string.Empty);
        builder.Property(e => e.LifeCategory).HasDefaultValue(MobileLifeCategories.Uncategorized);
        builder.Property(e => e.Source).HasDefaultValue("events");
        builder.Property(e => e.QualityFlagsJson).HasDefaultValue("[]");
        builder.Property(e => e.GeneratedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new
        {
            e.UserId,
            e.DeviceId,
            e.Granularity,
            e.BucketStartUtc,
            e.BucketEndUtc,
            e.PackageName,
            e.LifeCategory
        }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.BucketStartUtc });
        builder.HasIndex(e => new { e.UserId, e.LifeCategory, e.BucketStartUtc });
        builder.HasIndex(e => new { e.UserId, e.PackageName, e.BucketStartUtc });
        builder.HasIndex(e => new { e.UserId, e.IsStale });
    }
}

public sealed class MobileTimelineBlockEntityConfiguration : IEntityTypeConfiguration<MobileTimelineBlockEntity>
{
    public void Configure(EntityTypeBuilder<MobileTimelineBlockEntity> builder)
    {
        builder.Property(e => e.BlockId).HasDefaultValue(string.Empty);
        builder.Property(e => e.Timezone).HasDefaultValue(MobileAnalyticsDefaults.DefaultTimezone);
        builder.Property(e => e.LifeCategory).HasDefaultValue(MobileLifeCategories.Uncategorized);
        builder.Property(e => e.TopAppsJson).HasDefaultValue("[]");
        builder.Property(e => e.SourceMixJson).HasDefaultValue("{}");
        builder.Property(e => e.QualityFlagsJson).HasDefaultValue("[]");
        builder.Property(e => e.GeneratedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.StartUtc });
        builder.HasIndex(e => new { e.UserId, e.LifeCategory, e.StartUtc });
        builder.HasIndex(e => new { e.UserId, e.LocalDate });
        builder.HasIndex(e => new { e.UserId, e.IsStale });
    }
}

public sealed class MobileAnalyticsMaterializationEntityConfiguration
    : IEntityTypeConfiguration<MobileAnalyticsMaterializationEntity>
{
    public void Configure(EntityTypeBuilder<MobileAnalyticsMaterializationEntity> builder)
    {
        builder.Property(e => e.Timezone).HasDefaultValue(MobileAnalyticsDefaults.DefaultTimezone);
        builder.Property(e => e.GeneratedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.CoveredFromUtc, e.CoveredToUtc }).IsUnique();
    }
}

public sealed class MobileUsageGoalEntityConfiguration : IEntityTypeConfiguration<MobileUsageGoalEntity>
{
    public void Configure(EntityTypeBuilder<MobileUsageGoalEntity> builder)
    {
        builder.Property(e => e.Scope).HasDefaultValue("total-daily");
        builder.Property(e => e.Label).HasDefaultValue("每日手机总时长");
        builder.Property(e => e.Timezone).HasDefaultValue(MobileAnalyticsDefaults.DefaultTimezone);
        builder.Property(e => e.IsEnabled).HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.Scope, e.PackageName, e.LifeCategory }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.IsEnabled });
        builder.HasIndex(e => new { e.UserId, e.LifeCategory });
        builder.HasIndex(e => new { e.UserId, e.PackageName });
    }
}
