using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("habit_routines")]
public class HabitRoutineEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("description")] public string? Description { get; set; }
    [Column("cadence")][MaxLength(40)] public string Cadence { get; set; } = "Daily";
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Active";
    [Column("rule_json")] public string RuleJson { get; set; } = "{}";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<HabitOccurrenceEntity> Occurrences { get; set; } = new List<HabitOccurrenceEntity>();
}
