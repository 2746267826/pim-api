using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

/// <summary>
/// 任务状态（<c>tasks.status</c>）的受控值域 —— 工单 03 D1。
///
/// 这是任务状态的**唯一真值来源**：服务端校验（<c>CalendarService</c>）、契约 enum
/// （<c>StrictValueSetsSchemaFilter</c>）与 pim-web 的展示口径都由它派生。
/// 值形态沿用前端既有文案 <c>IN-PROCESS</c>（大写 + 连字符），另三值保持历史写法。
/// 历史背景：此前服务端只做 <c>MaxLength(20)</c>，任意字符串都能写库；
/// 收紧后集合外的值会以 400（02057）明确拒绝 —— 见工单 03 D1.1 的授权与影响面说明。
/// </summary>
public static class TaskStatuses
{
    public const string NeedsAction = "NEEDS-ACTION";
    public const string InProcess = "IN-PROCESS";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    /// <summary>契约 enum 与校验白名单的同一份顺序（顺序即契约里 enum 的排列）。</summary>
    public static readonly string[] All = [NeedsAction, InProcess, Completed, Cancelled];

    public static bool IsValid(string? status)
        => status is NeedsAction or InProcess or Completed or Cancelled;

    /// <summary>终态：进入后不允许存在「正在计时」的执行段（工单 03 D3 / AC-8）。</summary>
    public static bool IsTerminal(string? status) => status is Completed or Cancelled;
}

[Table("tasks")]
public class TaskEntity : ISoftDeletable, IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("calendar_id")] public Guid? CalendarId { get; set; }
    [Column("domain_project_id")] public Guid? DomainProjectId { get; set; }
    [Column("task_book_id")] public Guid? TaskBookId { get; set; }
    [Column("uid")][MaxLength(255)] public string Uid { get; set; } = string.Empty;
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("description")] public string? Description { get; set; }
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("priority")] public int Priority { get; set; }
    [Column("estimated_duration")] public TimeSpan? EstimatedDuration { get; set; }
    [Column("minimum_segment")] public TimeSpan? MinimumSegment { get; set; }
    [Column("dtstart")] public DateTimeOffset? DtStart { get; set; }
    [Column("planned_end")] public DateTimeOffset? PlannedEnd { get; set; }
    [Column("deleted_by_operation_id")] public Guid? DeletedByOperationId { get; set; }
    [Column("deleted_by_operation_kind")][MaxLength(64)] public string? DeletedByOperationKind { get; set; }
    [Column("due")] public DateTimeOffset? Due { get; set; }
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
    [Column("status")][MaxLength(20)] public string Status { get; set; } = TaskStatuses.NeedsAction;
    [Column("state_reason")] public string StateReason { get; set; } = string.Empty;
    [Column("review_outcome")] public string ReviewOutcome { get; set; } = string.Empty;
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

    [ForeignKey(nameof(DomainProjectId))]
    public DomainProjectEntity? DomainProject { get; set; }

    [ForeignKey(nameof(TaskBookId))]
    public TaskBookEntity? TaskBook { get; set; }

    [ForeignKey(nameof(ParentTaskId))]
    public TaskEntity? ParentTask { get; set; }

    public ICollection<TaskEntity> SubTasks { get; set; } = new List<TaskEntity>();
    public ICollection<TaskChecklistItemEntity> ChecklistItems { get; set; } = new List<TaskChecklistItemEntity>();
}
