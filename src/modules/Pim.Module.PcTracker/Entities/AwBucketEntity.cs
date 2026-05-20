using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_aw_buckets")]
public class AwBucketEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("pim_device_id")][MaxLength(64)] public string PimDeviceId { get; set; } = string.Empty;
    [Column("aw_device_id")][MaxLength(128)] public string? AwDeviceId { get; set; }
    [Column("bucket_id")][MaxLength(256)] public string BucketId { get; set; } = string.Empty;
    [Column("name")][MaxLength(256)] public string? Name { get; set; }
    [Column("type")][MaxLength(64)] public string BucketType { get; set; } = string.Empty;
    [Column("client")][MaxLength(128)] public string Client { get; set; } = string.Empty;
    [Column("hostname")][MaxLength(128)] public string Hostname { get; set; } = string.Empty;
    [Column("created_at_source")] public DateTimeOffset? CreatedAtSource { get; set; }
    [Column("last_updated_source")] public DateTimeOffset? LastUpdatedSource { get; set; }
    [Column("data_json", TypeName = "jsonb")] public string DataJson { get; set; } = "{}";
    [Column("seen_at")] public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
}
