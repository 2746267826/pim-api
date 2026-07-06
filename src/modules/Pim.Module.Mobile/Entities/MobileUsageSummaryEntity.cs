using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_usage_summaries")]
public sealed class MobileUsageSummaryEntity
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

    [Column("window_start_utc")]
    public DateTimeOffset WindowStartUtc { get; set; }

    [Column("window_end_utc")]
    public DateTimeOffset WindowEndUtc { get; set; }

    [Column("total_time_visible_ms")]
    public long TotalTimeVisibleMs { get; set; }

    [Column("last_time_used_utc")]
    public DateTimeOffset? LastTimeUsedUtc { get; set; }

    [Column("source_kind")]
    [MaxLength(64)]
    public string SourceKind { get; set; } = string.Empty;

    [Column("raw_json", TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    [Column("quality_flags_json", TypeName = "jsonb")]
    public string QualityFlagsJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
