using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Calendar.Entities;

/// <summary>
/// Durable idempotency ledger for Outlook calendar writes. Tokens and raw Graph
/// responses are deliberately absent: only the normalized operation receipt and a
/// reversible event draft are retained.
/// </summary>
[Table("outlook_writeback_operations")]
public sealed class OutlookWritebackOperationEntity : IUserOwnedEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("client_operation_id")] public Guid ClientOperationId { get; set; }
    [Column("undo_of_operation_id")] public Guid? UndoOfOperationId { get; set; }
    [Column("operation"), MaxLength(16)] public string Operation { get; set; } = string.Empty;
    [Column("calendar_binding_id")] public Guid CalendarBindingId { get; set; }
    [Column("request_event_id")] public Guid? RequestEventId { get; set; }
    [Column("event_id")] public Guid? EventId { get; set; }
    [Column("scope"), MaxLength(16)] public string Scope { get; set; } = "instance";
    [Column("payload_hash"), MaxLength(64)] public string PayloadHash { get; set; } = string.Empty;
    [Column("state"), MaxLength(24)] public string State { get; set; } = "processing";
    [Column("attempt_count")] public int AttemptCount { get; set; } = 1;
    [Column("result_event_id")] public Guid? ResultEventId { get; set; }
    [Column("result_status"), MaxLength(24)] public string? ResultStatus { get; set; }
    [Column("result_json", TypeName = "jsonb")] public string? ResultJson { get; set; }
    [Column("previous_draft_json", TypeName = "jsonb")] public string? PreviousDraftJson { get; set; }
    [Column("undo_state"), MaxLength(24)] public string UndoState { get; set; } = "unavailable";
    [Column("error_code"), MaxLength(64)] public string? ErrorCode { get; set; }
    [Column("error_message"), MaxLength(512)] public string? ErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
}
