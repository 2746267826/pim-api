using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Pim.Infrastructure.Data.Entities;

/// <summary>
/// Durable native-note MCP idempotency identity. The composite key is the single-winner
/// authority across API instances: once an owner binds a request id to an operation and a
/// fingerprint, that binding is retained forever. Only the replay response body may be purged
/// after its retention window; the identity and fingerprint are never deleted.
/// </summary>
[Table("note_mcp_operations")]
[PrimaryKey(nameof(OwnerId), nameof(RequestId))]
public sealed class NoteMcpOperationEntity
{
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("request_id")] public Guid RequestId { get; set; }
    [Column("note_id")] public Guid NoteId { get; set; }
    [Column("operation")] public string Operation { get; set; } = string.Empty;
    [Column("operation_id")] public string OperationId { get; set; } = string.Empty;
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [Column("state")] public string State { get; set; } = "pending";
    [Column("response_json")] public string? ResponseJson { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
    [Column("response_purged_at")] public DateTimeOffset? ResponsePurgedAt { get; set; }
    [Column("receipt_status")] public string? ReceiptStatus { get; set; }
    [Column("revision")] public Guid? Revision { get; set; }
    [Column("message")] public string? Message { get; set; }
    [Column("error_code")] public int? ErrorCode { get; set; }
    [Column("error_message")] public string? ErrorMessage { get; set; }
}
