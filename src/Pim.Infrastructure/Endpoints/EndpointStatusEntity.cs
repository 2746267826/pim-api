using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Endpoints;

[Table("endpoint_statuses")]
public class EndpointStatusEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("device_id")][MaxLength(160)] public string DeviceId { get; set; } = string.Empty;
    [Column("platform")][MaxLength(40)] public string Platform { get; set; } = "windows";
    [Column("app_version")][MaxLength(80)] public string? AppVersion { get; set; }
    [Column("upload_status")][MaxLength(40)] public string UploadStatus { get; set; } = "Unknown";
    [Column("collection_cache_count")] public int CollectionCacheCount { get; set; }
    [Column("online_only_blocked_count")] public int OnlineOnlyBlockedCount { get; set; }
    [Column("last_heartbeat_at")] public DateTimeOffset? LastHeartbeatAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
