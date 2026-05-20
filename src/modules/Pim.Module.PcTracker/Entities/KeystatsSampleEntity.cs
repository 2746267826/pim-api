using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_keystats_samples")]
public class KeystatsSampleEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("pim_device_id")][MaxLength(64)] public string PimDeviceId { get; set; } = string.Empty;
    [Column("sampled_at_utc")] public DateTimeOffset SampledAtUtc { get; set; }
    [Column("stats_date", TypeName = "date")] public DateTime StatsDate { get; set; }
    [Column("stats_timezone_offset_minutes")] public int StatsTimezoneOffsetMinutes { get; set; }
    [Column("key_presses")] public int KeyPresses { get; set; }
    [Column("left_clicks")] public int LeftClicks { get; set; }
    [Column("right_clicks")] public int RightClicks { get; set; }
    [Column("middle_clicks")] public int MiddleClicks { get; set; }
    [Column("side_back_clicks")] public int SideBackClicks { get; set; }
    [Column("side_forward_clicks")] public int SideForwardClicks { get; set; }
    [Column("mouse_distance")] public double MouseDistance { get; set; }
    [Column("scroll_distance")] public double ScrollDistance { get; set; }
    [Column("peak_kps")] public int PeakKps { get; set; }
    [Column("peak_cps")] public int PeakCps { get; set; }
    [Column("formatted_mouse_distance")][MaxLength(64)] public string? FormattedMouseDistance { get; set; }
    [Column("formatted_scroll_distance")][MaxLength(64)] public string? FormattedScrollDistance { get; set; }
    [Column("key_counts_json", TypeName = "jsonb")] public string KeyCountsJson { get; set; } = "{}";
    [Column("app_stats_json", TypeName = "jsonb")] public string AppStatsJson { get; set; } = "{}";
    [Column("raw_json", TypeName = "jsonb")] public string RawJson { get; set; } = "{}";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
