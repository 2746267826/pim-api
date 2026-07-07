using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("task_execution_segments")]
public class TaskExecutionSegmentEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("task_id")] public Guid TaskId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("starts_at")] public DateTimeOffset StartsAt { get; set; }
    [Column("ends_at")] public DateTimeOffset EndsAt { get; set; }
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "planned";
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("planning_reason")] public string? PlanningReason { get; set; }
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(TaskId))]
    public TaskEntity Task { get; set; } = null!;
}
