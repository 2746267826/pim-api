using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_usage_aggregates")]
public sealed class MobileUsageAggregateEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("granularity")]
    [MaxLength(32)]
    public string Granularity { get; set; } = "hour";

    [Column("bucket_start_utc")]
    public DateTimeOffset BucketStartUtc { get; set; }

    [Column("bucket_end_utc")]
    public DateTimeOffset BucketEndUtc { get; set; }

    [Column("timezone")]
    [MaxLength(64)]
    public string Timezone { get; set; } = MobileAnalyticsDefaults.DefaultTimezone;

    [Column("package_name")]
    [MaxLength(256)]
    public string PackageName { get; set; } = string.Empty;

    [Column("display_name")]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [Column("life_category")]
    [MaxLength(128)]
    public string LifeCategory { get; set; } = MobileLifeCategories.Uncategorized;

    [Column("source")]
    [MaxLength(64)]
    public string Source { get; set; } = "events";

    [Column("foreground_seconds")]
    public long ForegroundSeconds { get; set; }

    [Column("session_count")]
    public int SessionCount { get; set; }

    [Column("launch_count")]
    public int LaunchCount { get; set; }

    [Column("switch_or_pickup_count")]
    public int SwitchOrPickupCount { get; set; }

    [Column("is_system_noise")]
    public bool IsSystemNoise { get; set; }

    [Column("short_event_seconds")]
    public long ShortEventSeconds { get; set; }

    [Column("quality_flags_json", TypeName = "jsonb")]
    public string QualityFlagsJson { get; set; } = "[]";

    [Column("is_stale")]
    public bool IsStale { get; set; }

    [Column("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
