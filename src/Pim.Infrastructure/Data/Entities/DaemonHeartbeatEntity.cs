using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Operations;

namespace Pim.Infrastructure.Data.Entities;

[Table("daemon_heartbeats")]
public sealed class DaemonHeartbeatEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("daemon_kind")]
    [MaxLength(32)]
    public string DaemonKind { get; set; } = "windows";

    [Column("version")]
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    [Column("server_url")]
    [MaxLength(512)]
    public string ServerUrl { get; set; } = string.Empty;

    [Column("last_successful_upload_at")]
    public DateTimeOffset? LastSuccessfulUploadAt { get; set; }

    [Column("last_attempted_upload_at")]
    public DateTimeOffset? LastAttemptedUploadAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("upload_queue_count")]
    public int? UploadQueueCount { get; set; }

    [Column("activity_watch_state")]
    [MaxLength(32)]
    public string ActivityWatchState { get; set; } = DaemonSourceState.Unknown.ToString();

    [Column("key_stats_state")]
    [MaxLength(32)]
    public string KeyStatsState { get; set; } = DaemonSourceState.Unknown.ToString();

    [Column("collection_paused")]
    public bool CollectionPaused { get; set; }

    [Column("status_json", TypeName = "jsonb")]
    public string StatusJson { get; set; } = "{}";

    [Column("received_at")]
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("planned_offline_at")]
    public DateTimeOffset? PlannedOfflineAt { get; set; }

    [Column("offline_reason")]
    [MaxLength(32)]
    public string? OfflineReason { get; set; }
}
