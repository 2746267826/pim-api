using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Mcp.Entities;

public class McpClientEntityConfiguration : IEntityTypeConfiguration<McpClientEntity>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Configure(EntityTypeBuilder<McpClientEntity> builder)
    {
        builder.ToTable("mcp_clients");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.TokenPrefix).HasColumnName("token_prefix").HasMaxLength(12).IsRequired();
        builder.Property(e => e.Permissions)
            .HasColumnName("permissions")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(v, JsonOptions) ?? new());
        builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(e => e.CallCount).HasColumnName("call_count");
        builder.Property(e => e.WriteCallCount).HasColumnName("write_call_count");
        builder.Property(e => e.LastTool).HasColumnName("last_tool").HasMaxLength(120);
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");

        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => new { e.CreatedBy, e.Name }).IsUnique();

        // CreatedBy intentionally has no navigation/FK: the users table is soft-deletable and
        // a hard FK would filter out clients when the owner is soft-deleted. Ownership is
        // enforced in service code instead.
    }
}
