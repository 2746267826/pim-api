using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_app_catalog_overrides")]
public sealed class MobileAppCatalogOverrideEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("package_name")]
    [MaxLength(256)]
    public string PackageName { get; set; } = string.Empty;

    [Column("display_name_override")]
    [MaxLength(256)]
    public string? DisplayNameOverride { get; set; }

    [Column("life_category")]
    [MaxLength(128)]
    public string LifeCategory { get; set; } = MobileLifeCategories.Uncategorized;

    [Column("is_system_noise")]
    public bool IsSystemNoise { get; set; }

    [Column("hide_short_events")]
    public bool HideShortEvents { get; set; }

    [Column("notes")]
    [MaxLength(1024)]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
