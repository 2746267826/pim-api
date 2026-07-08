using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Endpoints;

[Table("endpoint_notification_actions")]
public class EndpointNotificationActionEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("device_id")][MaxLength(160)] public string DeviceId { get; set; } = string.Empty;
    [Column("action")][MaxLength(80)] public string Action { get; set; } = string.Empty;
    [Column("risk_level")][MaxLength(80)] public string RiskLevel { get; set; } = string.Empty;
    [Column("result")][MaxLength(80)] public string Result { get; set; } = string.Empty;
    [Column("detail_url")][MaxLength(500)] public string? DetailUrl { get; set; }
    [Column("message")][MaxLength(500)] public string? Message { get; set; }
    [Column("confirmation_id")][MaxLength(160)] public string? ConfirmationId { get; set; }
    [Column("related_object_type")][MaxLength(80)] public string? RelatedObjectType { get; set; }
    [Column("related_object_id")][MaxLength(160)] public string? RelatedObjectId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
