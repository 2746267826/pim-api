using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.PcTracker.Entities;

public class KeystatsDailyEntityConfiguration : IEntityTypeConfiguration<KeystatsDailyEntity>
{
    public void Configure(EntityTypeBuilder<KeystatsDailyEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId);
        builder.HasIndex(e => e.SnapshotDate);
        builder.HasIndex(e => new { e.DeviceId, e.SnapshotDate }).IsUnique();
        builder.HasMany(e => e.KeyCounts)
            .WithOne(k => k.DailySnapshot)
            .HasForeignKey(k => k.DailySnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.AppBreakdowns)
            .WithOne(a => a.DailySnapshot)
            .HasForeignKey(a => a.DailySnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AwEventEntityConfiguration : IEntityTypeConfiguration<AwEventEntity>
{
    public void Configure(EntityTypeBuilder<AwEventEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_pc_aw_events_device_id");
        builder.HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_pc_aw_events_timestamp");
        builder.HasIndex(e => e.EventType)
            .HasDatabaseName("ix_pc_aw_events_event_type");
        builder.HasIndex(e => e.BucketId)
            .HasDatabaseName("ix_pc_aw_events_bucket_id");
        builder.HasIndex(e => e.SourceEventId)
            .HasDatabaseName("ix_pc_aw_events_source_event_id");
        builder.HasIndex(e => e.AppNameNormalized)
            .HasDatabaseName("ix_pc_aw_events_app_name_normalized");
        builder.HasIndex(e => new { e.DeviceId, e.BucketId, e.SourceEventId })
            .IsUnique()
            .HasDatabaseName("ux_pc_aw_events_source")
            .HasFilter("bucket_id IS NOT NULL AND source_event_id IS NOT NULL");
    }
}

public class AwBucketEntityConfiguration : IEntityTypeConfiguration<AwBucketEntity>
{
    public void Configure(EntityTypeBuilder<AwBucketEntity> builder)
    {
        builder.HasIndex(e => new { e.PimDeviceId, e.BucketId })
            .IsUnique()
            .HasDatabaseName("ux_pc_aw_buckets_device_bucket");
        builder.HasIndex(e => e.BucketType)
            .HasDatabaseName("ix_pc_aw_buckets_type");
        builder.HasIndex(e => e.SeenAt)
            .HasDatabaseName("ix_pc_aw_buckets_seen_at");
    }
}

public class KeystatsSampleEntityConfiguration : IEntityTypeConfiguration<KeystatsSampleEntity>
{
    public void Configure(EntityTypeBuilder<KeystatsSampleEntity> builder)
    {
        builder.HasIndex(e => new { e.PimDeviceId, e.SampledAtUtc })
            .IsUnique()
            .HasDatabaseName("ux_pc_keystats_samples_device_minute");
        builder.HasIndex(e => e.StatsDate)
            .HasDatabaseName("ix_pc_keystats_samples_stats_date");
    }
}

public class AppCategoryEntityConfiguration : IEntityTypeConfiguration<AppCategoryEntity>
{
    public void Configure(EntityTypeBuilder<AppCategoryEntity> builder)
    {
        builder.ToTable("pc_app_categories");
        builder.HasIndex(e => e.CategoryName);
        builder.HasIndex(e => e.Priority);
    }
}
