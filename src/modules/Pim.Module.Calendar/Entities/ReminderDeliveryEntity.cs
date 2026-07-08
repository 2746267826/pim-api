using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("reminder_deliveries")]
public class ReminderDeliveryEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("reminder_id")] public Guid ReminderId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("channel")][MaxLength(40)] public string Channel { get; set; } = "Web";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Created";
    [Column("action")][MaxLength(80)] public string? Action { get; set; }
    [Column("payload_json", TypeName = "jsonb")] public string PayloadJson { get; set; } = "{}";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("responded_at")] public DateTimeOffset? RespondedAt { get; set; }

    [ForeignKey(nameof(ReminderId))]
    public ReminderEntity Reminder { get; set; } = null!;
}
