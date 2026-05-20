using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_keystats_daily")]
public class KeystatsDailyEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("snapshot_date", TypeName = "date")] public DateTime SnapshotDate { get; set; }
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
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<KeystatsKeyCountEntity> KeyCounts { get; set; } = new();
    public List<KeystatsAppBreakdownEntity> AppBreakdowns { get; set; } = new();
}
