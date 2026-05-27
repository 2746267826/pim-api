using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_provider_settings")]
public sealed class AiProviderSettingEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("provider")]
    [MaxLength(32)]
    public string Provider { get; set; } = "litellm";

    [Column("base_url")]
    [MaxLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    [Column("virtual_key_secret")]
    public byte[] VirtualKeySecretEncrypted { get; set; } = Array.Empty<byte>();

    [Column("default_model")]
    [MaxLength(128)]
    public string DefaultModel { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = "disabled";

    [Column("last_health_check_at")]
    public DateTimeOffset? LastHealthCheckAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
