using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

/// <summary>
/// 执行段状态（<c>task_execution_segments.status</c>）取值 —— 工单 03 D1.4(a)/D2.1。
///
/// 全部**小写**，与数据库既有默认值、pim-web 既有硬编码、既有数据一致；
/// 原 <c>Pim.Core.Planning.TaskSegmentStatus</c>（PascalCase、零引用死代码）已按
/// 工单 D1.4(a) 清理，不再存在第二套大小写值域。
///
/// 语义：<see cref="Planned"/> 计划段（未执行，不计入实际时长）；
/// <see cref="Active"/> 正在计时（<c>ends_at IS NULL</c>，同一任务同时只允许一条）；
/// <see cref="Paused"/> 已暂停（段已闭合，算作已发生的执行时长）；
/// <see cref="Completed"/> 已结束；<see cref="Cancelled"/> 随任务取消而中止。
/// </summary>
public static class TaskSegmentStatuses
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Planned, Active, Paused, Completed, Cancelled];

    /// <summary>
    /// 计入「实际执行时长」的取值：只有 <see cref="Planned"/>（尚未执行）不计。
    /// <see cref="Cancelled"/> 段虽然被中止，但那段墙钟时间确实花在了任务上，
    /// 必须计入 —— 否则「跑一会儿再取消」会把已发生的时长抹掉，与「先暂停再取消」不一致（复审 I7）。
    /// </summary>
    public static bool CountsAsExecuted(string? status) => status is Active or Paused or Completed or Cancelled;

    public static bool IsValid(string? status) => status is Planned or Active or Paused or Completed or Cancelled;
}

[Table("task_execution_segments")]
public class TaskExecutionSegmentEntity : ISoftDeletable, IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("task_id")] public Guid TaskId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("starts_at")] public DateTimeOffset StartsAt { get; set; }

    /// <summary>
    /// 段结束时刻。<c>NULL</c> 表示**该段正在计时**（工单 03 D2.1 引入的开放段）：
    /// 迁移前该列 <c>NOT NULL</c>，只能表达已闭合的段，因此无法表达「正在跑」。
    /// 既有数据的 <c>ends_at</c> 全部非空且保持不变。
    /// </summary>
    [Column("ends_at")] public DateTimeOffset? EndsAt { get; set; }

    [Column("status")][MaxLength(40)] public string Status { get; set; } = TaskSegmentStatuses.Planned;
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "manual";
    [Column("planning_reason")] public string? PlanningReason { get; set; }
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(TaskId))]
    public TaskEntity Task { get; set; } = null!;
}
