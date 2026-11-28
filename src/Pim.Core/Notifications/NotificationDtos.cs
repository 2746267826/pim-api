namespace Pim.Core.Notifications;

public static class NotificationEventTypes
{
    public const string TaskCompleted = "calendar.task.completed";
    public const string CalendarReminderDue = "calendar.reminder.due";
    public const string NoteTranscriptionCompleted = "note.transcription.completed";
    public const string NoteTranscriptionAttention = "note.transcription.attention";
    public const string NoteDerivedCompleted = "note.derived.completed";
    public const string NoteDerivedAttention = "note.derived.attention";
    public const string Test = "notification.test";
    /// <summary>Q06 upload attention digest. Reserved until F11 provides an upload-failure source.</summary>
    public const string UploadAttentionDigest = "upload.attention_digest";

    public static readonly string[] All =
    [
        TaskCompleted,
        CalendarReminderDue,
        NoteTranscriptionCompleted,
        NoteTranscriptionAttention,
        NoteDerivedCompleted,
        NoteDerivedAttention,
        Test
    ];
}

/// <summary>
/// Stable client-facing category keys confirmed for Q06. Clients may filter on these
/// values independently of the internal event type names.
/// </summary>
public static class NotificationCategoryKeys
{
    public const string NotesSummaryReady = "notes.summary_ready";
    public const string NotesTranscriptionAttention = "notes.transcription_attention";
    public const string NotesOrganizingReview = "notes.organizing_review";
    public const string UploadsAttentionDigest = "uploads.attention_digest";

    public static readonly string[] Required =
    [
        NotesSummaryReady,
        NotesTranscriptionAttention,
        NotesOrganizingReview,
        UploadsAttentionDigest
    ];
}

/// <summary>
/// The single event-type to category-key mapping used by scanner, delivery and clients.
/// Event types without a Q06 alias keep their event type as their category key.
/// </summary>
public static class NotificationCategories
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationEventTypes.NoteDerivedCompleted] =
                NotificationCategoryKeys.NotesSummaryReady,
            [NotificationEventTypes.NoteTranscriptionAttention] =
                NotificationCategoryKeys.NotesTranscriptionAttention,
            [NotificationEventTypes.NoteDerivedAttention] =
                NotificationCategoryKeys.NotesOrganizingReview,
            [NotificationEventTypes.UploadAttentionDigest] =
                NotificationCategoryKeys.UploadsAttentionDigest
        };

    public static string ForEventType(string eventType) =>
        Aliases.TryGetValue(eventType, out var categoryKey) ? categoryKey : eventType;

    /// <summary>Category keys that this slice can actually emit.</summary>
    public static IReadOnlyList<string> ProducibleKeys =>
        NotificationEventTypes.All
            .Select(ForEventType)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>All confirmed Q06 keys, including reserved keys without a producer yet.</summary>
    public static IReadOnlyList<string> AllKeys =>
        NotificationCategoryKeys.Required
            .Concat(ProducibleKeys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public static class NotificationChannels
{
    public const string WebPush = "WebPush";
}

public static class NotificationDeliveryStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Retrying = "Retrying";
    public const string Delivered = "Delivered";
    public const string DeadLetter = "DeadLetter";
    public const string Suppressed = "Suppressed";
    public const string Skipped = "Skipped";

    public static readonly string[] All =
    [
        Pending, Processing, Retrying, Delivered, DeadLetter, Suppressed, Skipped
    ];
}

public sealed record NotificationPreferenceDto(
    bool InAppEnabled,
    bool PushEnabled,
    bool TaskCompleted,
    bool NoteTranscriptionCompleted,
    bool NoteDerivedCompleted,
    bool CalendarReminder,
    DateTimeOffset UpdatedAt);

public sealed record UpdateNotificationPreferencesRequest(
    bool? InAppEnabled,
    bool? PushEnabled,
    bool? TaskCompleted,
    bool? NoteTranscriptionCompleted,
    bool? NoteDerivedCompleted,
    bool? CalendarReminder);

public sealed record NotificationSubscriptionKeysDto(string P256Dh, string Auth);

public sealed record CreateNotificationSubscriptionRequest(
    string DeviceId,
    string Channel,
    string Endpoint,
    NotificationSubscriptionKeysDto Keys);

public sealed record RevokeCurrentNotificationSubscriptionRequest(
    string? DeviceId,
    string? Endpoint);

public sealed record NotificationSubscriptionRevokeResponse(int Revoked);

public sealed record NotificationSubscriptionDto(
    Guid Id,
    string DeviceId,
    string Channel,
    string EndpointHost,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt);

public sealed record NotificationCapabilitiesDto(
    bool WebPushConfigured,
    string? WebPushPublicKey,
    bool SseEnabled,
    IReadOnlyList<string> EventTypes);

public sealed record NotificationDto(
    Guid Id,
    long Cursor,
    string Type,
    string Title,
    string Body,
    string? DetailUrl,
    bool IsRead,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationListResponse(
    IReadOnlyList<NotificationDto> Items,
    int UnreadCount);

public sealed record NotificationDeliveryDto(
    Guid Id,
    Guid NotificationId,
    string Type,
    string Title,
    string Channel,
    string DeviceId,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? DeadLetteredAt);

public sealed record NotificationDeliveryListResponse(IReadOnlyList<NotificationDeliveryDto> Items);

public sealed record NotificationReadResponse(Guid Id, DateTimeOffset? ReadAt);

public sealed record NotificationReadAllResponse(int Updated);

public sealed record NotificationTestResponse(bool Enqueued, string Message);
