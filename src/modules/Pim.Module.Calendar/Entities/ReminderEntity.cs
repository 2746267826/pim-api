using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("reminders")]
public class ReminderEntity : ISoftDeletable
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
    [Column("dnd_start")][MaxLength(16)] public string? DoNotDisturbStart { get; set; }
    [Column("dnd_end")][MaxLength(16)] public string? DoNotDisturbEnd { get; set; }
    [Column("scheduled_at")] public DateTimeOffset ScheduledAt { get; set; }
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Open";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ReminderDeliveryEntity> Deliveries { get; set; } = new List<ReminderDeliveryEntity>();
}
