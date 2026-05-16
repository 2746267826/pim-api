using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Stats.Entities;

public class AppUsageEntityConfiguration : IEntityTypeConfiguration<AppUsageEntity>
{
    public void Configure(EntityTypeBuilder<AppUsageEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId);
        builder.HasIndex(e => e.PackageName);
        builder.HasIndex(e => e.CreatedAt);
    }
}
