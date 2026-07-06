using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
