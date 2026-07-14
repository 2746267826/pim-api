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
    EventResponse? Event,
    string? LatestOutlookJson,
    string? LatestEtag,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record UpdateOutlookClientIdRequest(Guid ClientId);
public sealed record OutlookAuthorizationSessionRequest(Guid SessionId);
public sealed record OutlookLocalDataPreview(int BindingCount, int CalendarCount, int EventCount);

public sealed record UpdateCalendarSelectionRequest(IReadOnlyCollection<Guid> SelectedBindingIds);
