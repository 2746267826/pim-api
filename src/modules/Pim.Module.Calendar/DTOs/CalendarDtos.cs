using System.ComponentModel.DataAnnotations;

namespace Pim.Module.Calendar.DTOs;

public record CreateCalendarRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(7)] string? Color,
    string? Kind = null
);

public record CalendarResponse(
    Guid Id, string Name, string Color, string Kind, bool IsDefault, int EventCount,
    string Source = "manual",
    Guid? OutlookCalendarBindingId = null,
    bool CanEdit = true
)
{
    public CalendarResponse(Guid Id, string Name, string Color, string Kind, bool IsDefault, int EventCount)
        : this(Id, Name, Color, Kind, IsDefault, EventCount, "manual", null, true) { }
}

public record EventPersonDto(string? Name, string? Email);

public record EventAttendeeDto(string? Name, string Email, string Type = "required");

public record EventAttachmentReferenceDto(
    string Kind,
    string Id,
    string Name,
    string? ContentType = null,
    long? Size = null,
    bool CanDownload = false);

public record OutlookAdditionalInfoItemDto(string Key, string Label, string Value);

public record OutlookAdditionalInfoGroupDto(
    string Key,
    string Label,
    IReadOnlyList<OutlookAdditionalInfoItemDto> Items);

public record OutlookAdditionalInfoDto(
    IReadOnlyList<OutlookAdditionalInfoGroupDto> Groups,
    int HiddenFieldCount);

public record CreateEventRequest(
    [Required] Guid CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    [MaxLength(500)] string? Location,
    [Required] DateTimeOffset DtStart,
    [Required] DateTimeOffset DtEnd,
    string? RRule,
    string? Uid = null,
    bool IsAllDay = false,
    string? TimeZoneId = null,
    string? DescriptionFormat = null,
    string? ShowAs = null,
    string? Importance = null,
    string? Sensitivity = null,
    IReadOnlyList<string>? Categories = null,
    bool? IsReminderOn = null,
    int? ReminderMinutesBeforeStart = null,
    EventPersonDto? Organizer = null,
    IReadOnlyList<EventAttendeeDto>? Attendees = null,
    bool? IsOnlineMeeting = null,
    string? OnlineMeetingProvider = null,
    string? OnlineMeetingUrl = null,
    string? ExternalLink = null,
    IReadOnlyList<EventAttachmentReferenceDto>? AttachmentReferences = null,
    bool? IsSeriesMaster = null,
    bool? IsException = null,
    Guid? SeriesMasterId = null,
    string? RecurrenceId = null
);

public enum UpdateEventScope
{
    This,
    Series
}

public record UpdateEventRequest(
    [Required] Guid CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    [MaxLength(500)] string? Location,
    [Required] DateTimeOffset DtStart,
    [Required] DateTimeOffset DtEnd,
    string? RRule,
    string? Uid = null,
    bool? IsAllDay = null,
    string? TimeZoneId = null,
    string? DescriptionFormat = null,
    string? ShowAs = null,
    string? Importance = null,
    string? Sensitivity = null,
    IReadOnlyList<string>? Categories = null,
    bool? IsReminderOn = null,
    int? ReminderMinutesBeforeStart = null,
    EventPersonDto? Organizer = null,
    IReadOnlyList<EventAttendeeDto>? Attendees = null,
    bool? IsOnlineMeeting = null,
    string? OnlineMeetingProvider = null,
    string? OnlineMeetingUrl = null,
    string? ExternalLink = null,
    IReadOnlyList<EventAttachmentReferenceDto>? AttachmentReferences = null,
    bool? IsSeriesMaster = null,
    bool? IsException = null,
    Guid? SeriesMasterId = null,
    string? RecurrenceId = null
);

public record EventResponse(
    Guid Id, Guid CalendarId, string Uid, string Title,
    string? Description, string? Location,
    DateTimeOffset DtStart, DateTimeOffset DtEnd,
    string? RRule, string Status, string Source,
    Guid? OriginalEventId = null,
    bool IsAllDay = false,
    string? TimeZoneId = null,
    string? SourceTimeZoneId = null,
    string? SourceUid = null,
    string? RecurrenceId = null,
    string ExDatesJson = "[]",
    string RecurrenceMetadataJson = "{}",
    Guid? OutlookCalendarBindingId = null,
    string? OutlookEventId = null,
    string? OutlookEtag = null,
    string? OutlookEventType = null,
    OutlookAdditionalInfoDto? OutlookAdditionalInfo = null,
    string? DescriptionFormat = null,
    string? ShowAs = null,
    string? Importance = null,
    string? Sensitivity = null,
    IReadOnlyList<string>? Categories = null,
    bool? IsReminderOn = null,
    int? ReminderMinutesBeforeStart = null,
    EventPersonDto? Organizer = null,
    IReadOnlyList<EventAttendeeDto>? Attendees = null,
    bool? IsOnlineMeeting = null,
    string? OnlineMeetingProvider = null,
    string? OnlineMeetingUrl = null,
    string? ExternalLink = null,
    IReadOnlyList<EventAttachmentReferenceDto>? AttachmentReferences = null,
    bool IsSeriesMaster = false,
    bool IsException = false,
    Guid? SeriesMasterId = null,
    bool IsCancelled = false
);

public record CreateTaskRequest(
    Guid? CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    int Priority,
    string? EstimatedDuration,
    string? MinimumSegment,
    DateTimeOffset? Due,
    DateTimeOffset? DtStart,
    string? Status = null,
    DateTimeOffset? PlannedEnd = null
);

public record UpdateTaskRequest(
    Guid? CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    int Priority,
    string? EstimatedDuration,
    string? MinimumSegment,
    DateTimeOffset? Due,
    DateTimeOffset? DtStart,
    string? Status = null,
    DateTimeOffset? PlannedEnd = null
);

public record TaskResponse(
    Guid Id, Guid? CalendarId, string Uid, string Title,
    string? Description, int Priority,
    string? EstimatedDuration, string? MinimumSegment,
    DateTimeOffset? DtStart, DateTimeOffset? Due,
    string Status, bool IsInbox, int SortOrder,
    List<TaskResponse> SubTasks,
    DateTimeOffset? PlannedEnd = null
);

public record MoveTaskRequest(
    DateTimeOffset? ScheduledStart,
    TimeSpan? Duration,
    int? NewSortOrder,
    DateTimeOffset? PlannedEnd = null
);

public record ScheduleRequest(
    List<Guid> TaskIds
);

public record SchedulePlanResponse(
    Guid PlanId,
    string AlgorithmName,
    List<ScheduledTaskSlot> Slots
);

public record ScheduledTaskSlot(
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset Start,
    DateTimeOffset End
);

public record ImportResult(int Imported, int Skipped);

public record BatchDeleteRequest(List<Guid> Ids);

public record BatchDeleteResult(int DeletedCount);

public record CalendarOperationSample(
    Guid Id,
    string Type,
    string Title,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? BookName
);

public record CalendarDeletePreviewResponse(
    string TargetType,
    Guid TargetId,
    string Title,
    string OperationKind,
    int AffectedCount,
    IReadOnlyList<CalendarOperationSample> Samples,
    string Summary,
    bool RequiresStrictConfirmation
);

public record CalendarOperationResult(
    string Operation,
    Guid OperationId,
    int AffectedCount,
    IReadOnlyList<Guid> AffectedIds,
    IReadOnlyList<CalendarOperationSample> Samples,
    string Message
);

public record CalendarRestoreConflict(
    Guid DeletedId,
    string DeletedType,
    Guid ActiveId,
    string ActiveType,
    string Reason,
    string Title
);

public record CalendarRestorePreviewResponse(
    string TargetType,
    Guid TargetId,
    string Title,
    int RestoreCount,
    IReadOnlyList<CalendarOperationSample> Samples,
    IReadOnlyList<CalendarRestoreConflict> Conflicts,
    bool CanRestoreWithoutConflict
);

public record CalendarRestoreRequest(bool RestoreAsCopy = false);

public record CalendarRecycleBinItem(
    Guid Id,
    string Type,
    string Title,
    DateTimeOffset DeletedAt,
    string? BookName,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string Source,
    Guid? DeletedByOperationId,
    string? DeletedByOperationKind
);

public record CalendarRecycleBinDetail(
    CalendarRecycleBinItem Item,
    string? Description,
    string MetadataJson,
    IReadOnlyList<CalendarOperationSample> ChildSamples
);

public record BatchIdsRequest(IReadOnlyList<Guid> Ids);

public record BatchTaskUpdateRequest(
    IReadOnlyList<Guid> Ids,
    string? Status,
    int? Priority,
    Guid? CalendarId
);

public record PlanTaskRequest(
    DateTimeOffset PlannedStart,
    DateTimeOffset? PlannedEnd,
    string? EstimatedDuration
);

public record CreateTaskExecutionSegmentRequest(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [Required][MaxLength(40)] string Status,
    [Required][MaxLength(40)] string Source,
    string? PlanningReason
);

public record TaskExecutionSegmentResponse(
    Guid Id,
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string Source,
    string? PlanningReason,
    Guid? ConfirmationId
);

public record CreateDomainProjectRequest(
    [Required][MaxLength(255)] string Name,
    string? Description,
    [MaxLength(40)] string? Status = null
);

public record CreateTaskBookRequest(
    Guid? DomainProjectId,
    [Required][MaxLength(255)] string Name,
    [MaxLength(40)] string? Kind = null,
    [MaxLength(40)] string? Status = null
);

public record AddTaskChecklistItemRequest(
    [Required][MaxLength(255)] string Title,
    int? SortOrder = null
);

public record CreateHabitRequest(
    [Required][MaxLength(255)] string Title,
    string? Description,
    [MaxLength(40)] string? Cadence = null,
    [MaxLength(40)] string? Source = null,
    [MaxLength(40)] string? Status = null,
    string? RuleJson = null
);

public record CreateHabitOccurrenceRequest(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [MaxLength(40)] string? Status = null,
    [MaxLength(40)] string? Source = null
);

public record CreateAvailabilityWindowRequest(
    [Required][MaxLength(255)] string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [MaxLength(40)] string? Kind = null,
    [MaxLength(40)] string? Source = null
);

public record CreateAiPlanningPlaceholderRequest(
    [Required][MaxLength(255)] string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [Required] string Reason,
    [MaxLength(40)] string? Source = null
);

public record CalendarLayerQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string>? Layers,
    bool OutlookOnly = false
);

public record CalendarLayerItem(
    string Id,
    string Layer,
    string ObjectType,
    Guid ObjectId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Source,
    string Status,
    string Color,
    bool RequiresConfirmation
);

public record CalendarLayerResponse(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<CalendarLayerItem> Items
);

public record DataCenterQueryRequest(
    string? Search,
    string? ObjectType,
    string? Source,
    bool PendingOnly,
    int Page = 1,
    int PageSize = 50
);

public record DataCenterItem(
    string ObjectType,
    Guid ObjectId,
    string Title,
    string Source,
    string Status,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string Summary
);

public record DataCenterQueryResponse(
    IReadOnlyList<DataCenterItem> Items,
    int Page,
    int PageSize,
    int TotalCount
);

public record DataCenterObjectRef(
    string ObjectType,
    Guid ObjectId
);

public record DataCenterBatchOperationRequest(
    string Action,
    IReadOnlyList<DataCenterObjectRef> Objects,
    string? Reason = null
);

public record DataCenterBatchPreviewResponse(
    string RiskLevel,
    bool RequiresStrictConfirmation,
    string Summary,
    IReadOnlyList<string> AffectedObjectTypes,
    int AffectedCount
);

public record DataCenterBatchExecutionResponse(
    Guid ConfirmationId,
    string Status,
    int AffectedCount
);

public record DataCenterExecuteBatchRequest(
    Guid ConfirmationId
);

public record DataCenterRestoreRequest(
    Guid AuditVersionId,
    string? Reason = null
);

public record ImportSkippedItem(
    string Reason,
    string Title,
    DateTimeOffset? Start,
    string? Uid
);

public record ImportReport(
    int Imported,
    int Skipped,
    IReadOnlyDictionary<string, int> SkippedReasons,
    IReadOnlyList<ImportSkippedItem> Samples
);

public record OutlookSettingsResponse(
    string Provider,
    string TenantId,
    string? ClientId,
    string Scopes,
    string Status,
    string TokenHealth,
    DateTimeOffset? LastSyncedAt,
    string? LastError,
    string UiStatus = "unknown",
    OutlookAuthorizationSessionResponse? ActiveAuthorization = null
);

public record UpdateOutlookSettingsRequest(
    string TenantId,
    string? ClientId,
    string Scopes
);

public record OutlookDeviceCodeRequestResponse(
    string Endpoint,
    string VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAt,
    string Message,
    string? DeviceCode = null
);

public record OutlookDeviceCodePollRequest(
    [Required] string DeviceCode
);

public record OutlookSyncStep(
    string Name,
    string Status,
    string Detail,
    DateTimeOffset At
);

public record OutlookSyncBatchResponse(
    Guid Id,
    string Provider,
    string Status,
    int ReadCount,
    int CreatedCount,
    int UpdatedCount,
    int ConflictCount,
    int ConfirmationCount,
    int FailureCount,
    IReadOnlyList<OutlookSyncStep> Steps,
    string? ErrorSummary,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Mode = null,
    DateTimeOffset? RequestedWindowStart = null,
    DateTimeOffset? RequestedWindowEnd = null,
    string? PerCalendarJson = null,
    bool CancelRequested = false
);

public record ConflictResolutionRequest(
    string Action,
    string? MergedFieldsJson,
    string? Reason
);

public record OutlookStopSyncExecuteRequest(
    Guid ConfirmationId
);

public record SyncConflictDetailDto(
    Guid Id,
    string Provider,
    string ObjectType,
    Guid ObjectId,
    string? GraphEventId,
    string ConflictKind,
    string Status,
    string PimSnapshotJson,
    string ExternalSnapshotJson,
    Guid? ResolvedConfirmationId
);

public record CreateReminderRequest(
    string RelatedObjectType,
    Guid RelatedObjectId,
    string Title,
    string Body,
    string TriggerReason,
    string RiskLevel,
    IReadOnlyList<string> Channels,
    string? DoNotDisturbStart,
    string? DoNotDisturbEnd,
    DateTimeOffset ScheduledAt
);

public record ReminderResponse(
    Guid Id,
    string RelatedObjectType,
    Guid RelatedObjectId,
    string Title,
    string Body,
    string TriggerReason,
    string RiskLevel,
    IReadOnlyList<string> Channels,
    string? DoNotDisturbStart,
    string? DoNotDisturbEnd,
    DateTimeOffset ScheduledAt,
    string Status
);

public record ReminderActionResponse(
    string Kind,
    string Status,
    string? DetailUrl
);

public record ReminderNotificationPayloadDto(
    Guid ReminderId,
    string Title,
    string Body,
    string RiskLevel,
    string RelatedObjectType,
    Guid RelatedObjectId,
    string DetailUrl,
    IReadOnlyList<string> Actions
);

public record ReminderDeliveryDto(
    Guid Id,
    Guid ReminderId,
    string Channel,
    string Status,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RespondedAt
);

public record GenerateReportRequest(
    string Kind,
    DateOnly Date,
    Guid? ProjectId
);

public record ReportArtifactDto(
    Guid Id,
    string Kind,
    Guid? ProjectId,
    string RiskLevel,
    string ContentMarkdown,
    string MetricsJson,
    DateTimeOffset GeneratedAt,
    string Status
);

public record ReportSuggestionDto(
    Guid Id,
    Guid ReportId,
    string Action,
    string Summary,
    string Status,
    Guid? ConfirmationId
);
