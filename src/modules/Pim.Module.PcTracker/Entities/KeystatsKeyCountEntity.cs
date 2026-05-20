using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_keystats_key_counts")]
public class KeystatsKeyCountEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("daily_snapshot_id")] public long DailySnapshotId { get; set; }
    [Column("key_name")][MaxLength(128)] public string KeyName { get; set; } = string.Empty;
    [Column("count")] public int Count { get; set; }

    [ForeignKey(nameof(DailySnapshotId))]
    public KeystatsDailyEntity DailySnapshot { get; set; } = null!;
}
