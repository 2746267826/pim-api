using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

[Table("pending_confirmations")]
public class PendingConfirmationEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("type")][MaxLength(50)] public string Type { get; set; } = string.Empty;
    [Column("summary")] public string Summary { get; set; } = string.Empty;
    [Column("payload", TypeName = "jsonb")] public string Payload { get; set; } = "{}";
    [Column("status")][MaxLength(20)] public string Status { get; set; } = "pending";
    [Column("confirmed_at")] public DateTimeOffset? ConfirmedAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
