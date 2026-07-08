using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("habit_occurrences")]
public class HabitOccurrenceEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("habit_routine_id")] public Guid HabitRoutineId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("starts_at")] public DateTimeOffset StartsAt { get; set; }
    [Column("ends_at")] public DateTimeOffset EndsAt { get; set; }
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Planned";
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(HabitRoutineId))]
    public HabitRoutineEntity HabitRoutine { get; set; } = null!;
}
