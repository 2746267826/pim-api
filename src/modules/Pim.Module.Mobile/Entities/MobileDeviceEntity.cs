using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_devices")]
public sealed class MobileDeviceEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("device_hash")]
    [MaxLength(256)]
    public string DeviceHash { get; set; } = string.Empty;

    [Column("display_name")]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [Column("manufacturer")]
    [MaxLength(128)]
    public string Manufacturer { get; set; } = string.Empty;

    [Column("brand")]
    [MaxLength(128)]
    public string Brand { get; set; } = string.Empty;

    [Column("model")]
    [MaxLength(128)]
    public string Model { get; set; } = string.Empty;

    [Column("os_version")]
    [MaxLength(64)]
    public string OsVersion { get; set; } = string.Empty;

    [Column("api_level")]
    public int ApiLevel { get; set; }

    [Column("app_version")]
    [MaxLength(64)]
    public string AppVersion { get; set; } = string.Empty;

    [Column("metadata_json", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";

    [Column("registered_at_utc")]
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_seen_at_utc")]
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
