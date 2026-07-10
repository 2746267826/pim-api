using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("outlook_connections")]
public class OutlookConnectionEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("provider")][MaxLength(40)] public string Provider { get; set; } = "outlook";
    [Column("client_id")][MaxLength(255)] public string? ClientId { get; set; }
    [Column("tenant_id")][MaxLength(128)] public string TenantId { get; set; } = "common";
    [Column("authority"), MaxLength(512)]
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";
    [Column("home_account_id"), MaxLength(512)] public string? HomeAccountId { get; set; }
    [Column("account_display_name"), MaxLength(255)] public string? AccountDisplayName { get; set; }
    [Column("account_login_hint"), MaxLength(255)] public string? AccountLoginHint { get; set; }
    [Column("scopes")][MaxLength(1024)] public string Scopes { get; set; } = "Calendars.ReadWrite offline_access User.Read openid profile";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "not-connected";
    [Column("token_health")][MaxLength(40)] public string TokenHealth { get; set; } = "missing";
    [Column("access_token_encrypted")] public byte[] AccessTokenEncrypted { get; set; } = Array.Empty<byte>();
    [Column("refresh_token_encrypted")] public byte[]? RefreshTokenEncrypted { get; set; }
    [Column("msal_cache_encrypted")] public byte[]? MsalCacheEncrypted { get; set; }
    [Column("access_token_expires_at")] public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    [Column("subscription_id")][MaxLength(255)] public string? SubscriptionId { get; set; }
    [Column("subscription_expires_at")] public DateTimeOffset? SubscriptionExpiresAt { get; set; }
    [Column("delta_link")] public string? DeltaLink { get; set; }
    [Column("last_synced_at")] public DateTimeOffset? LastSyncedAt { get; set; }
    [Column("last_error")] public string? LastError { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("version"), ConcurrencyCheck] public long Version { get; set; }
}
