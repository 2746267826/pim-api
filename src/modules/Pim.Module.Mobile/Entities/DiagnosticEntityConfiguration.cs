using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

public sealed class DiagnosticEventEntityConfiguration : IEntityTypeConfiguration<DiagnosticEventEntity>
{
    public void Configure(EntityTypeBuilder<DiagnosticEventEntity> builder)
    {
        builder.Property(e => e.AttributesJson).HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.ReceivedAtUtc).HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.ClientEventId }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.ReceivedAtUtc });
        builder.HasIndex(e => new { e.UserId, e.DeviceId, e.OccurredAtUtc });
    }
}
