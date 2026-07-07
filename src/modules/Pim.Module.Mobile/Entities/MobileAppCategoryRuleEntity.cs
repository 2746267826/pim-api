using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_app_category_rules")]
public sealed class MobileAppCategoryRuleEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("rule_type")]
    [MaxLength(64)]
    public string RuleType { get; set; } = "package-exact";

    [Column("pattern")]
    [MaxLength(512)]
    public string Pattern { get; set; } = string.Empty;

    [Column("life_category")]
    [MaxLength(128)]
    public string LifeCategory { get; set; } = MobileLifeCategories.Uncategorized;

    [Column("display_name_override")]
    [MaxLength(256)]
    public string? DisplayNameOverride { get; set; }

    [Column("is_system_noise")]
    public bool? IsSystemNoise { get; set; }

    [Column("priority")]
    public int Priority { get; set; } = 100;

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
