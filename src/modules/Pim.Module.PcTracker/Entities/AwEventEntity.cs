using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_aw_events")]
public class AwEventEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [Column("duration")] public double Duration { get; set; }
    [Column("event_type")][MaxLength(16)] public string EventType { get; set; } = "window";
    [Column("app_name")][MaxLength(256)] public string? AppName { get; set; }
    [Column("window_title")] public string? WindowTitle { get; set; }
    [Column("afk_status")][MaxLength(16)] public string? AfkStatus { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("aw_device_id")][MaxLength(128)] public string? AwDeviceId { get; set; }
    [Column("aw_hostname")][MaxLength(128)] public string? AwHostname { get; set; }
    [Column("bucket_id")][MaxLength(256)] public string? BucketId { get; set; }
    [Column("bucket_type")][MaxLength(64)] public string? BucketType { get; set; }
    [Column("bucket_client")][MaxLength(128)] public string? BucketClient { get; set; }
    [Column("source_event_id")] public long? SourceEventId { get; set; }
    [Column("data_json", TypeName = "jsonb")] public string DataJson { get; set; } = "{}";
    [Column("app_name_normalized")][MaxLength(256)] public string? AppNameNormalized { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
