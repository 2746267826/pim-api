using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_tracker_health")]
public class TrackerHealthEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("status")][MaxLength(32)] public string Status { get; set; } = "running";
    [Column("uptime_seconds")] public double UptimeSeconds { get; set; }
    [Column("hook_active")] public bool HookActive { get; set; }
    [Column("poll_count")] public long PollCount { get; set; }
    [Column("sessions_created")] public long SessionsCreated { get; set; }
    [Column("events_uploaded")] public long EventsUploaded { get; set; }
    [Column("upload_failures")] public long UploadFailures { get; set; }
    [Column("last_error")] public string? LastError { get; set; }
    [Column("browser_connected")] public bool BrowserConnected { get; set; }
    [Column("browser_heartbeat_age_seconds")] public double? BrowserHeartbeatAgeSeconds { get; set; }
    [Column("reported_at")] public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
