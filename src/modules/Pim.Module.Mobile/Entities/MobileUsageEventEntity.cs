using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_usage_events")]
public sealed class MobileUsageEventEntity
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

    [Column("event_type")]
    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    [Column("event_timestamp_utc")]
    public DateTimeOffset EventTimestampUtc { get; set; }

    [Column("class_name")]
    [MaxLength(512)]
    public string? ClassName { get; set; }

    [Column("source_window_start_utc")]
    public DateTimeOffset SourceWindowStartUtc { get; set; }

    [Column("source_window_end_utc")]
    public DateTimeOffset SourceWindowEndUtc { get; set; }

    [Column("collected_at_utc")]
    public DateTimeOffset CollectedAtUtc { get; set; }

    [Column("raw_json", TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    [Column("quality_flags_json", TypeName = "jsonb")]
    public string QualityFlagsJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
