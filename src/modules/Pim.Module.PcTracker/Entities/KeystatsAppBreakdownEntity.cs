using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_keystats_app_breakdown")]
public class KeystatsAppBreakdownEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("daily_snapshot_id")] public long DailySnapshotId { get; set; }
    [Column("app_name")][MaxLength(256)] public string AppName { get; set; } = string.Empty;
    [Column("display_name")][MaxLength(512)] public string DisplayName { get; set; } = string.Empty;
    [Column("key_presses")] public int KeyPresses { get; set; }
    [Column("left_clicks")] public int LeftClicks { get; set; }
    [Column("right_clicks")] public int RightClicks { get; set; }
    [Column("middle_clicks")] public int MiddleClicks { get; set; }
    [Column("side_back_clicks")] public int SideBackClicks { get; set; }
    [Column("side_forward_clicks")] public int SideForwardClicks { get; set; }
    [Column("scroll_distance")] public double ScrollDistance { get; set; }

    [ForeignKey(nameof(DailySnapshotId))]
    public KeystatsDailyEntity DailySnapshot { get; set; } = null!;
}
