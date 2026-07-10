using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("outlook_operation_executions")]
public sealed class OutlookOperationExecutionEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("confirmation_id")] public Guid ConfirmationId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("operation_type"), MaxLength(128)] public string OperationType { get; set; } = string.Empty;
    [Column("proposed_hash"), MaxLength(64)] public string ProposedHash { get; set; } = string.Empty;
    [Column("payload_json", TypeName = "jsonb")] public string PayloadJson { get; set; } = "{}";
    [Column("state"), MaxLength(32)] public string State { get; set; } = "queued";
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("next_attempt_at")] public DateTimeOffset? NextAttemptAt { get; set; }
    [Column("last_error_code"), MaxLength(128)] public string? LastErrorCode { get; set; }
    [Column("last_error_message")] public string? LastErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
}
