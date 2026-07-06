using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_app_catalog")]
public sealed class MobileAppCatalogEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("package_name")]
    [MaxLength(256)]
    public string PackageName { get; set; } = string.Empty;

    [Column("display_name")]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [Column("version_name")]
    [MaxLength(128)]
    public string? VersionName { get; set; }

    [Column("version_code")]
    public long? VersionCode { get; set; }

    [Column("is_system_app")]
    public bool IsSystemApp { get; set; }

    [Column("category")]
    [MaxLength(128)]
    public string? Category { get; set; }

    [Column("installer_package")]
    [MaxLength(256)]
    public string? InstallerPackage { get; set; }

    [Column("first_install_time_utc")]
    public DateTimeOffset? FirstInstallTimeUtc { get; set; }

    [Column("last_update_time_utc")]
    public DateTimeOffset? LastUpdateTimeUtc { get; set; }

    [Column("raw_json", TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
