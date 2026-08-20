namespace Pim.Module.Calendar.DTOs;

public sealed record OutlookAuthorizationSessionResponse(
    Guid Id,
    string Status,
    string? VerificationUri,
    string? UserCode,
    DateTimeOffset? ExpiresAt,
    string? AccountDisplayName,
    string? AccountLoginHint,
    string? ErrorCode,
    string? ErrorMessage,
    string? RecoveryAction);

public sealed record OutlookCalendarBindingResponse(
    Guid Id, Guid PimCalendarId, string GraphCalendarId,
    string? GroupId, string? GroupName, string Name, string? Color,
    string? OwnerName, string? OwnerAddress,
    bool IsDefault, bool CanEdit, bool IsSelected, string RemoteState,
    DateTimeOffset? LastSyncedAt, string? LastError);

public sealed record OutlookSyncRequest(
    string Mode,
    IReadOnlyList<Guid>? CalendarBindingIds = null,
    DateTimeOffset? RangeStart = null,
    DateTimeOffset? RangeEnd = null,
    Guid? RetryOfBatchId = null);

public sealed record OutlookEventDraft(
    Guid CalendarId,
    string Title,
    string? Description,
    string? DescriptionFormat,
    string? Location,
    DateTimeOffset DtStart,
    DateTimeOffset DtEnd,
    bool IsAllDay,
    string? TimeZoneId,
    string? ShowAs,
    string? Importance,
    string? Sensitivity,
    IReadOnlyList<string>? Categories,
    bool? IsReminderOn,
    int? ReminderMinutesBeforeStart,
    EventPersonDto? Organizer,
    IReadOnlyList<EventAttendeeDto>? Attendees,
    bool? IsOnlineMeeting,
    string? OnlineMeetingProvider,
    string? OnlineMeetingUrl,
    string? ExternalLink,
    IReadOnlyList<EventAttachmentReferenceDto>? AttachmentReferences);

public sealed record OutlookWriteRequest(
    string Operation,
    Guid CalendarBindingId,
    Guid? EventId,
    CreateEventRequest? Draft,
    string Scope,
    Guid ClientOperationId,
    string? ExpectedEtag = null);

public sealed record OutlookWriteResult(
    string Status,
    EventResponse? Event = null,
    EventResponse? LatestEvent = null,
    string? LatestEtag = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record UpdateOutlookClientIdRequest(Guid ClientId);
public sealed record OutlookAuthorizationSessionRequest(Guid SessionId);
public sealed record OutlookLocalDataPreview(int BindingCount, int CalendarCount, int EventCount);

public sealed record UpdateCalendarSelectionRequest(IReadOnlyCollection<Guid> SelectedBindingIds);
