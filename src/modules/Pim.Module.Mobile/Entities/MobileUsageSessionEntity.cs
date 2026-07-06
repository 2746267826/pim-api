using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_usage_sessions")]
public sealed class MobileUsageSessionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("package_name")]
    [MaxLength(256)]
    public string PackageName { get; set; } = string.Empty;

    [Column("start_utc")]
    public DateTimeOffset StartUtc { get; set; }

    [Column("end_utc")]
    public DateTimeOffset? EndUtc { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Column("quality_flags_json", TypeName = "jsonb")]
    public string QualityFlagsJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
