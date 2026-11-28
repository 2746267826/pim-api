using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Infrastructure.Notifications;

[Table("notification_preferences")]
public sealed class NotificationPreferenceEntity : IUserOwnedEntity
{
    [Key][Column("user_id")] public Guid UserId { get; set; }
    [Column("in_app_enabled")] public bool InAppEnabled { get; set; } = true;
    [Column("push_enabled")] public bool PushEnabled { get; set; } = true;
    [Column("task_completed")] public bool TaskCompleted { get; set; } = true;
    [Column("note_transcription_completed")] public bool NoteTranscriptionCompleted { get; set; } = true;
    [Column("note_derived_completed")] public bool NoteDerivedCompleted { get; set; } = true;
    [Column("calendar_reminder")] public bool CalendarReminder { get; set; } = true;
    [Column("device_overrides_json")] public string DeviceOverridesJson { get; set; } = "{}";
    [Column("version")] public Guid Version { get; set; } = Guid.NewGuid();
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("notification_subscriptions")]
public sealed class NotificationSubscriptionEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("device_id")][MaxLength(160)] public string DeviceId { get; set; } = string.Empty;
    [Column("channel")][MaxLength(40)] public string Channel { get; set; } = string.Empty;
    [Column("endpoint_ciphertext")] public string EndpointCiphertext { get; set; } = string.Empty;
    [Column("endpoint_hash")][MaxLength(64)] public string EndpointHash { get; set; } = string.Empty;
    [Column("endpoint_host")][MaxLength(255)] public string EndpointHost { get; set; } = string.Empty;
    [Column("p256dh_ciphertext")] public string P256DhCiphertext { get; set; } = string.Empty;
    [Column("auth_ciphertext")] public string AuthCiphertext { get; set; } = string.Empty;
    [Column("status")][MaxLength(20)] public string Status { get; set; } = "Active";
    [Column("version")] public Guid Version { get; set; } = Guid.NewGuid();
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("last_seen_at")] public DateTimeOffset? LastSeenAt { get; set; }
    [Column("revoked_at")] public DateTimeOffset? RevokedAt { get; set; }
    [Column("last_error_code")][MaxLength(80)] public string? LastErrorCode { get; set; }
    [Column("last_error_message")][MaxLength(300)] public string? LastErrorMessage { get; set; }
}

[Table("notification_events")]
public sealed class NotificationEventEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("sequence")] public long Sequence { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("event_type")][MaxLength(80)] public string EventType { get; set; } = string.Empty;
    [Column("dedupe_key")][MaxLength(200)] public string DedupeKey { get; set; } = string.Empty;
    [Column("title")][MaxLength(255)] public string Title { get; set; } = string.Empty;
    [Column("body")][MaxLength(1000)] public string Body { get; set; } = string.Empty;
    [Column("detail_url")][MaxLength(500)] public string? DetailUrl { get; set; }
    [Column("in_app_visible")] public bool InAppVisible { get; set; } = true;
    [Column("push_eligible")] public bool PushEligible { get; set; } = true;
    [Column("eligible_subscription_ids_json")] public string? EligibleSubscriptionIdsJson { get; set; }
    [Column("fanout_status")][MaxLength(20)] public string FanoutStatus { get; set; } = "Pending";
    [Column("fanout_attempts")] public int FanoutAttempts { get; set; }
    [Column("next_fanout_at")] public DateTimeOffset NextFanoutAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("locked_until")] public DateTimeOffset? LockedUntil { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("fanout_completed_at")] public DateTimeOffset? FanoutCompletedAt { get; set; }
    [Column("last_error_code")][MaxLength(80)] public string? LastErrorCode { get; set; }
    [Column("last_error_message")][MaxLength(300)] public string? LastErrorMessage { get; set; }
    [Column("read_at")] public DateTimeOffset? ReadAt { get; set; }
}

[Table("notification_deliveries")]
public sealed class NotificationDeliveryEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("event_id")] public Guid EventId { get; set; }
    [Column("subscription_id")] public Guid SubscriptionId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("channel")][MaxLength(40)] public string Channel { get; set; } = string.Empty;
    [Column("device_id")][MaxLength(160)] public string DeviceId { get; set; } = string.Empty;
    [Column("status")][MaxLength(20)] public string Status { get; set; } = "Pending";
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("next_attempt_at")] public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("locked_until")] public DateTimeOffset? LockedUntil { get; set; }
    [Column("locked_by")][MaxLength(64)] public string? LockedBy { get; set; }
    [Column("last_error_code")][MaxLength(80)] public string? LastErrorCode { get; set; }
    [Column("last_error_message")][MaxLength(300)] public string? LastErrorMessage { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("delivered_at")] public DateTimeOffset? DeliveredAt { get; set; }
    [Column("dead_lettered_at")] public DateTimeOffset? DeadLetteredAt { get; set; }

    [ForeignKey(nameof(EventId))]
    public NotificationEventEntity Event { get; set; } = null!;
}
