using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_usage_goals")]
public sealed class MobileUsageGoalEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("scope")]
    [MaxLength(64)]
    public string Scope { get; set; } = "total-daily";

    [Column("package_name")]
    [MaxLength(256)]
    public string? PackageName { get; set; }

    [Column("life_category")]
    [MaxLength(128)]
    public string? LifeCategory { get; set; }

    [Column("label")]
    [MaxLength(128)]
    public string Label { get; set; } = "每日手机总时长";

    [Column("limit_seconds")]
    public long LimitSeconds { get; set; }

    [Column("timezone")]
    [MaxLength(64)]
    public string Timezone { get; set; } = MobileAnalyticsDefaults.DefaultTimezone;

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
