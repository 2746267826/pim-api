using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Entities;

[Table("diagnostic_events")]
public sealed class DiagnosticEventEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("platform")]
    [MaxLength(16)]
    public string Platform { get; set; } = string.Empty;

    [Column("client_event_id")]
    [MaxLength(128)]
    public string ClientEventId { get; set; } = string.Empty;

    [Column("client_batch_id")]
    [MaxLength(128)]
    public string ClientBatchId { get; set; } = string.Empty;

    [Column("kind")]
    [MaxLength(16)]
    public string Kind { get; set; } = string.Empty;

    [Column("severity")]
    [MaxLength(16)]
    public string Severity { get; set; } = string.Empty;

    [Column("app_version")]
    [MaxLength(64)]
    public string AppVersion { get; set; } = string.Empty;

    [Column("os_version")]
    [MaxLength(128)]
    public string OsVersion { get; set; } = string.Empty;

    [Column("message")]
    [MaxLength(DiagnosticLimits.MaxMessageLength)]
    public string Message { get; set; } = string.Empty;

    [Column("stack_trace")]
    [MaxLength(DiagnosticLimits.MaxStackTraceLength)]
    public string? StackTrace { get; set; }

    [Column("attributes_json", TypeName = "jsonb")]
    public string AttributesJson { get; set; } = "{}";

    [Column("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; set; }

    [Column("received_at_utc")]
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
