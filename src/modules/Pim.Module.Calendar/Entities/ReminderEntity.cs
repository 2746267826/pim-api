using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("reminders")]
public class ReminderEntity : ISoftDeletable, IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("related_object_type")][MaxLength(80)] public string RelatedObjectType { get; set; } = string.Empty;
    [Column("related_object_id")] public Guid RelatedObjectId { get; set; }
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("body")] public string Body { get; set; } = string.Empty;
    [Column("trigger_reason")] public string TriggerReason { get; set; } = string.Empty;
    [Column("risk_level")][MaxLength(80)] public string RiskLevel { get; set; } = "L1LowRiskAction";
    [Column("channels_json", TypeName = "jsonb")] public string ChannelsJson { get; set; } = "[]";
    // 【已废弃 / Deprecated，工单 04】提醒层的 dnd_start / dnd_end 是 string(16)、无格式校验、
    // 全仓没有任何调度/下发逻辑读它（只在创建与查询时原样进出）。它与「日程期间静音」不是同一个语义：
    // 前者挂在提醒上、用一天内的时间字符串描述免打扰时段，后者挂在日程上、由日程自身的起止时间决定。
    // 因此本工单不复用它：日程静音走 events.is_muted（见 EventEntity）+ calendars.default_muted。
    // 字段保留仅为兼容既有调用方，不要在此基础上新增逻辑。
    [Column("dnd_start")][MaxLength(16)] public string? DoNotDisturbStart { get; set; }

    [Column("dnd_end")][MaxLength(16)] public string? DoNotDisturbEnd { get; set; }
    [Column("scheduled_at")] public DateTimeOffset ScheduledAt { get; set; }
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Open";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ReminderDeliveryEntity> Deliveries { get; set; } = new List<ReminderDeliveryEntity>();
}
