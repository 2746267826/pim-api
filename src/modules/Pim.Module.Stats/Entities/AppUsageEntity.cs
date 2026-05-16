using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Stats.Entities;

[Table("app_usage")]
public class AppUsageEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("package_name")][MaxLength(256)] public string PackageName { get; set; } = string.Empty;
    [Column("start_time")] public DateTimeOffset StartTime { get; set; }
    [Column("end_time")] public DateTimeOffset EndTime { get; set; }
    [Column("duration_ms")] public long DurationMs { get; set; }
    [Column("last_time_used")] public DateTimeOffset LastTimeUsed { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
