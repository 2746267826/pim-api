using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("audit_logs")]
public sealed class AuditLogEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("actor_type")]
    [MaxLength(32)]
    public string ActorType { get; set; } = string.Empty;

    [Column("action")]
    [MaxLength(128)]
    public string Action { get; set; } = string.Empty;

    [Column("resource_type")]
    [MaxLength(128)]
    public string ResourceType { get; set; } = string.Empty;

    [Column("resource_id")]
    [MaxLength(128)]
    public string? ResourceId { get; set; }

    [Column("source")]
    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    [Column("result")]
    [MaxLength(32)]
    public string Result { get; set; } = string.Empty;

    [Column("ip_address")]
    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [Column("user_agent")]
    [MaxLength(512)]
    public string? UserAgent { get; set; }

    [Column("correlation_id")]
    [MaxLength(128)]
    public string? CorrelationId { get; set; }

    [Column("metadata_json", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";

    [Column("error_code")]
    public int? ErrorCode { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
