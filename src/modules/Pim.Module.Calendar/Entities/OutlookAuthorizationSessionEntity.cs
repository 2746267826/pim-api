using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("outlook_authorization_sessions")]
public sealed class OutlookAuthorizationSessionEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("connection_id")] public Guid ConnectionId { get; set; }
    [Column("status"), MaxLength(32)] public string Status { get; set; } = "starting";
    [Column("verification_uri"), MaxLength(512)] public string? VerificationUri { get; set; }
    [Column("user_code"), MaxLength(64)] public string? UserCode { get; set; }
    [Column("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [Column("account_display_name"), MaxLength(255)] public string? AccountDisplayName { get; set; }
    [Column("account_login_hint"), MaxLength(255)] public string? AccountLoginHint { get; set; }
    [Column("error_code"), MaxLength(128)] public string? ErrorCode { get; set; }
    [Column("error_message")] public string? ErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
