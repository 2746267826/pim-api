using System.ComponentModel.DataAnnotations;

namespace Pim.Module.Calendar.DTOs;

public record CreateCalendarRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(7)] string? Color,
    string? Kind = null
);

public record CalendarResponse(
    Guid Id, string Name, string Color, string Kind, bool IsDefault, int EventCount
);

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
    string? TimeZoneId = null
);

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
    string? TimeZoneId = null
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
    string ExternalMetadataJson = "{}",
    string? RecurrenceId = null,
    string ExDatesJson = "[]",
    string RecurrenceMetadataJson = "{}"
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
    string? LastError
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
    DateTimeOffset? FinishedAt
);

public record ConflictResolutionRequest(
    string Action,
    string? MergedFieldsJson,
    string? Reason
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
