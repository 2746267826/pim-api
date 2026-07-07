using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_timeline_blocks")]
public sealed class MobileTimelineBlockEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("start_utc")]
    public DateTimeOffset StartUtc { get; set; }

    [Column("end_utc")]
    public DateTimeOffset EndUtc { get; set; }

    [Column("local_date")]
    [MaxLength(10)]
    public string LocalDate { get; set; } = string.Empty;

    [Column("timezone")]
    [MaxLength(64)]
    public string Timezone { get; set; } = MobileAnalyticsDefaults.DefaultTimezone;

    [Column("life_category")]
    [MaxLength(128)]
    public string LifeCategory { get; set; } = MobileLifeCategories.Uncategorized;

    [Column("foreground_seconds")]
    public long ForegroundSeconds { get; set; }

    [Column("session_count")]
    public int SessionCount { get; set; }

    [Column("app_count")]
    public int AppCount { get; set; }

    [Column("top_apps_json", TypeName = "jsonb")]
    public string TopAppsJson { get; set; } = "[]";

    [Column("source_mix_json", TypeName = "jsonb")]
    public string SourceMixJson { get; set; } = "{}";

    [Column("quality_flags_json", TypeName = "jsonb")]
    public string QualityFlagsJson { get; set; } = "[]";

    [Column("includes_system_noise")]
    public bool IncludesSystemNoise { get; set; }

    [Column("is_stale")]
    public bool IsStale { get; set; }

    [Column("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
