namespace Pim.Core.Notifications;

public static class NotificationEventTypes
{
    public const string TaskCompleted = "calendar.task.completed";
    public const string CalendarReminderDue = "calendar.reminder.due";
    public const string NoteTranscriptionCompleted = "note.transcription.completed";
    public const string NoteDerivedCompleted = "note.derived.completed";
    public const string Test = "notification.test";

    public static readonly string[] All =
    [
        TaskCompleted,
        CalendarReminderDue,
        NoteTranscriptionCompleted,
        NoteDerivedCompleted,
        Test
    ];
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
