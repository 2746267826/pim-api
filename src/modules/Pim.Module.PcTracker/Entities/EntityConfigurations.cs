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
        builder.HasIndex(e => e.DeviceId);
        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.EventType);
    }
}

public class AppCategoryEntityConfiguration : IEntityTypeConfiguration<AppCategoryEntity>
{
    public void Configure(EntityTypeBuilder<AppCategoryEntity> builder)
    {
        builder.ToTable("pc_app_categories");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AppPattern).HasMaxLength(128).IsRequired();
        builder.Property(e => e.CategoryName).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Color).HasMaxLength(7);
        builder.HasIndex(e => e.CategoryName);
        builder.HasIndex(e => e.Priority);
    }
}
