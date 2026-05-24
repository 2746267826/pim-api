using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Operations;

namespace Pim.Infrastructure.Data.Entities;

[Table("operation_confirmations")]
public sealed class OperationConfirmationEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("requested_by_user_id")]
    public Guid? RequestedByUserId { get; set; }

    [Column("operation_type")]
    [MaxLength(128)]
    public string OperationType { get; set; } = string.Empty;

    [Column("summary")]
    [MaxLength(512)]
    public string Summary { get; set; } = string.Empty;

    [Column("risk_level")]
    [MaxLength(32)]
    public string RiskLevel { get; set; } = string.Empty;

    [Column("source")]
    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    [Column("payload_json", TypeName = "jsonb")]
    public string PayloadJson { get; set; } = "{}";

    [Column("preview_json", TypeName = "jsonb")]
    public string PreviewJson { get; set; } = "{}";

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = OperationConfirmationStatus.Pending.ToString();

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("confirmed_at")]
    public DateTimeOffset? ConfirmedAt { get; set; }

    [Column("rejected_at")]
    public DateTimeOffset? RejectedAt { get; set; }

    [Column("executed_at")]
    public DateTimeOffset? ExecutedAt { get; set; }

    [Column("result_json", TypeName = "jsonb")]
    public string? ResultJson { get; set; }

    [Column("correlation_id")]
    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}
