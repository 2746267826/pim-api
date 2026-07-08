using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("ai_planning_placeholders")]
public class AiPlanningPlaceholderEntity : ISoftDeletable
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("starts_at")] public DateTimeOffset StartsAt { get; set; }
    [Column("ends_at")] public DateTimeOffset EndsAt { get; set; }
    [Column("reason")] public string Reason { get; set; } = string.Empty;
    [Column("source")][MaxLength(40)] public string Source { get; set; } = "ai";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "Suggested";
    [Column("confirmation_id")] public Guid? ConfirmationId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}
