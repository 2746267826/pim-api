using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("tasks")]
public class TaskEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("calendar_id")] public Guid? CalendarId { get; set; }
    [Column("uid")][MaxLength(255)] public string Uid { get; set; } = string.Empty;
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("description")] public string? Description { get; set; }
    [Column("priority")] public int Priority { get; set; }
    [Column("estimated_duration")] public TimeSpan? EstimatedDuration { get; set; }
    [Column("minimum_segment")] public TimeSpan? MinimumSegment { get; set; }
    [Column("dtstart")] public DateTimeOffset? DtStart { get; set; }
    [Column("planned_end")] public DateTimeOffset? PlannedEnd { get; set; }
    [Column("deleted_by_operation_id")] public Guid? DeletedByOperationId { get; set; }
    [Column("deleted_by_operation_kind")][MaxLength(64)] public string? DeletedByOperationKind { get; set; }
    [Column("due")] public DateTimeOffset? Due { get; set; }
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
    [Column("status")][MaxLength(20)] public string Status { get; set; } = "NEEDS-ACTION";
    [Column("percent_complete")] public int PercentComplete { get; set; }
    [Column("parent_task_id")] public Guid? ParentTaskId { get; set; }
    [Column("is_inbox")] public bool IsInbox { get; set; } = true;
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("schedule_plan_id")] public Guid? SchedulePlanId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(CalendarId))]
    public CalendarEntity? Calendar { get; set; }

    [ForeignKey(nameof(ParentTaskId))]
    public TaskEntity? ParentTask { get; set; }

    public ICollection<TaskEntity> SubTasks { get; set; } = new List<TaskEntity>();
}
