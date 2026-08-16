using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_activity_category_rules")]
public class ActivityCategoryRuleEntity
{
    [Key][Column("id")] public Guid Id { get; set; }
    [Column("rule_name")][MaxLength(128)] public string RuleName { get; set; } = string.Empty;
    [Column("scope")][MaxLength(16)] public string Scope { get; set; } = "activity";
    [Column("category_name")][MaxLength(64)] public string? CategoryName { get; set; }
    [Column("category_id")] public Guid? CategoryId { get; set; }
    [Column("project_tag")][MaxLength(128)] public string? ProjectTag { get; set; }
    [Column("color")][MaxLength(7)] public string Color { get; set; } = "#64748b";
    [Column("priority")] public int Priority { get; set; }
    [Column("source")][MaxLength(32)] public string Source { get; set; } = "user";
    [Column("status")][MaxLength(16)] public string Status { get; set; } = "active";
    [Column("conditions_json", TypeName = "jsonb")] public string ConditionsJson { get; set; } = "{}";
    [Column("confidence")] public double Confidence { get; set; } = 1;
    [Column("explanation")] public string? Explanation { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
