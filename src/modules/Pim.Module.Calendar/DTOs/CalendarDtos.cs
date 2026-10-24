using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Pim.Module.Calendar.DTOs;

public record CreateCalendarRequest(
    [property:Required][property:MaxLength(100)] string Name,
    [property:MaxLength(7)] string? Color,
    string? Kind = null
);

public record CalendarResponse(
    Guid Id, string Name, string Color, string Kind, bool IsDefault, int EventCount,
    string Source = "manual",
    Guid? OutlookCalendarBindingId = null,
    bool CanEdit = true,
    bool DefaultMuted = false
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
    [property:Required] Guid CalendarId,
    [property:Required][property:MaxLength(255)] string Title,
    string? Description,
    [property:MaxLength(500)] string? Location,
    [property:Required] DateTimeOffset DtStart,
    [property:Required] DateTimeOffset DtEnd,
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
    string? RecurrenceId = null,
    /// <summary>该日程期间是否静音：true 静音 / false 不静音 / null（省略）未指定，生效值取所属日历本的 defaultMuted。</summary>
    bool? IsMuted = null
);

public enum UpdateEventScope
{
    This,
    Series
}

public record UpdateEventRequest(
    [property:Required] Guid CalendarId,
    [property:Required][property:MaxLength(255)] string Title,
    string? Description,
    [property:MaxLength(500)] string? Location,
    [property:Required] DateTimeOffset DtStart,
    [property:Required] DateTimeOffset DtEnd,
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
    string? RecurrenceId = null,
    /// <summary>该日程期间是否静音：true 静音 / false 不静音 / null 未指定（生效值取所属日历本的 defaultMuted）。PUT 为整体替换，null 即回到未指定。</summary>
    bool? IsMuted = null
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
    bool IsCancelled = false,
    /// <summary>日程自身设定：true 静音 / false 不静音 / null 未指定（生效值取所属日历本的 defaultMuted）。</summary>
    bool? IsMuted = null
);

/// <summary>
/// 日历本静音策略的批量设定请求（E1.3）：把该本下既有日程统一设为开或关，
/// 并作为该本后续同步日程的默认。
///
/// <para>
/// <c>IsMuted</c> 声明为可空并在服务端显式校验"必须出现"：System.Text.Json 会把缺失的字段
/// 绑成 <c>false</c>，若直接声明成 <c>bool</c>，一个 <c>{}</c> 请求体就会被静默当成
/// 「统一设为不静音」并改掉整个日历本的日程（独立 review I-1）。Swagger 的 required 只是文档元数据，
/// 不参与运行时校验，所以必须在这里挡：<c>[Required]</c> 让契约把该字段标成必须出现，
/// 服务层再把 <c>null</c> 也挡掉（比契约更严，属有意为之）。
/// </para>
/// </summary>
public record SetCalendarMutePolicyRequest(
    [property:Required] bool? IsMuted);

/// <summary>日历本静音策略的批量设定结果。</summary>
public record CalendarMutePolicyResponse(Guid CalendarId, bool DefaultMuted, int AffectedEventCount);

public record CreateTaskRequest(
    Guid? CalendarId,
    [property:Required][property:MaxLength(255)] string Title,
    string? Description,
    int Priority,
    string? EstimatedDuration,
    string? MinimumSegment,
    DateTimeOffset? Due,
    DateTimeOffset? DtStart,
    string? Status = null,
    DateTimeOffset? PlannedEnd = null,
    Guid? TaskBookId = null,
    int? PercentComplete = null
);

[JsonConverter(typeof(Services.UpdateTaskRequestJsonConverter))]
public record UpdateTaskRequest(
    Guid? CalendarId,
    [property:MaxLength(255)] string? Title,
    string? Description,
    int? Priority,
    string? EstimatedDuration,
    string? MinimumSegment,
    DateTimeOffset? Due,
    DateTimeOffset? DtStart,
    string? Status = null,
    DateTimeOffset? PlannedEnd = null,
    Guid? TaskBookId = null,
    int? PercentComplete = null)
{
    // JSON distinguishes omission from null; direct constructor callers keep their prior semantics.
    [JsonIgnore]
    internal HashSet<string>? ProvidedFields { get; init; }

    internal bool WasProvided(string field) => ProvidedFields?.Contains(field) ?? field switch
    {
        nameof(Status) => Status is not null,
        nameof(PlannedEnd) => PlannedEnd.HasValue,
        nameof(PercentComplete) => PercentComplete.HasValue,
        _ => true,
    };
}

public record TaskResponse(
    Guid Id, Guid? CalendarId, string Uid, string Title,
    string? Description, int Priority,
    string? EstimatedDuration, string? MinimumSegment,
    DateTimeOffset? DtStart, DateTimeOffset? Due,
    string Status, bool IsInbox, int SortOrder,
    List<TaskResponse> SubTasks,
    DateTimeOffset? PlannedEnd = null,
    Guid? TaskBookId = null,
    int PercentComplete = 0
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
    [property:Required][property:MaxLength(40)] string Status,
    [property:Required][property:MaxLength(40)] string Source,
    string? PlanningReason
);

public record TaskExecutionSegmentResponse(
    Guid Id,
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset StartsAt,
    // NULL = 该段正在计时（工单 03 D2.1）
    DateTimeOffset? EndsAt,
    string Status,
    string Source,
    string? PlanningReason,
    Guid? ConfirmationId
);

/// <summary>
/// 任务计时状态 + 累计实际时长（工单 03 D2.1「时长可累加、可查询」/ D3 报告侧数据源）。
///
/// <c>AccumulatedSeconds</c> = 该任务所有 <c>active</c> / <c>paused</c> / <c>completed</c> /
/// <c>cancelled</c> 段之和（正在计时的段按「查询时刻 - startsAt」计入；只有 <c>planned</c> 不计）。
///
/// ⚠️ 语义边界：它是**实际花在任务上的时间**，包含「任务被取消/删除前已经发生的那段」，
/// **不代表任务成功完成**（是否完成看 <c>TaskStatus</c>，报告统计完成数请用
/// <c>status == COMPLETED</c>，不要把这个时长当作完成时长的替代）。
/// 逐段四舍五入取秒；与 pim-web 行内时长同口径。
/// </summary>
public record TaskTimerStateResponse(
    Guid TaskId,
    string TaskStatus,
    Guid? RunningSegmentId,
    DateTimeOffset? RunningSince,
    long AccumulatedSeconds,
    int SegmentCount
);

public record CreateDomainProjectRequest(
    [property:Required][property:MaxLength(255)] string Name,
    string? Description,
    [property:MaxLength(40)] string? Status = null
);

public record CreateTaskBookRequest(
    Guid? DomainProjectId,
    [property:Required][property:MaxLength(255)] string Name,
    [property:MaxLength(40)] string? Kind = null,
    [property:MaxLength(40)] string? Status = null
);

public record AddTaskChecklistItemRequest(
    [property:Required][property:MaxLength(255)] string Title,
    int? SortOrder = null
);

public record UpdateTaskChecklistItemRequest(
    string? Title = null,
    bool? IsDone = null
);

public record CreateHabitRequest(
    [property:Required][property:MaxLength(255)] string Title,
    string? Description,
    [property:MaxLength(40)] string? Cadence = null,
    [property:MaxLength(40)] string? Source = null,
    [property:MaxLength(40)] string? Status = null,
    string? RuleJson = null
);

/// <summary>
/// #351：编辑习惯。所有字段可选，**只更新显式传入的字段**（PATCH 语义）——
/// 未传的字段保持原值，避免调用方漏传就静默清空长期习惯事实。
/// </summary>
public record UpdateHabitRequest(
    [property:MaxLength(255)] string? Title = null,
    string? Description = null,
    [property:MaxLength(40)] string? Cadence = null,
    [property:MaxLength(40)] string? Status = null,
    string? RuleJson = null
);

public record CreateHabitOccurrenceRequest(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [property:MaxLength(40)] string? Status = null,
    [property:MaxLength(40)] string? Source = null
);

public record CreateAvailabilityWindowRequest(
    [property:Required][property:MaxLength(255)] string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [property:MaxLength(40)] string? Kind = null,
    [property:MaxLength(40)] string? Source = null
);

public record CreateAiPlanningPlaceholderRequest(
    [property:Required][property:MaxLength(255)] string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    [property:Required] string Reason,
    [property:MaxLength(40)] string? Source = null
);

public record GenerateAiPlanRequest(
    [property:Range(1, 30)] int? HorizonDays = null,
    IReadOnlyList<Guid>? TaskIds = null
);

/// <summary>排程建议视图（含状态与来源），用于工作台 AI 规划面板。</summary>
public record AiPlanPlaceholderViewDto(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    string Status,
    string Source,
    Guid? ConfirmationId);

public record GenerateAiPlanResponse(
    string Source,
    IReadOnlyList<AiPlanPlaceholderViewDto> Placeholders);

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
    [property:Required] string DeviceCode
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

/// <summary>
/// 提醒创建请求。
///
/// <para>
/// 【已废弃 / Deprecated，工单 04】<paramref name="DoNotDisturbStart"/> / <paramref name="DoNotDisturbEnd"/>
/// 是提醒上的 string(16) 时间串，无格式校验、全仓没有任何调度或下发逻辑消费它。
/// 「日程期间静音」用 Events 的 <c>isMuted</c>（三态）与日历本的 <c>defaultMuted</c>，
/// 不要在这两个字段上新增逻辑；保留仅为兼容既有调用方。
/// </para>
/// </summary>
public record CreateReminderRequest(
    string RelatedObjectType,
    Guid RelatedObjectId,
    string Title,
    string? Body,
    string? TriggerReason,
    string? RiskLevel,
    IReadOnlyList<string>? Channels,
    string? DoNotDisturbStart,
    string? DoNotDisturbEnd,
    DateTimeOffset? ScheduledAt
);

/// <summary>
/// 提醒响应。【已废弃 / Deprecated，工单 04】<c>doNotDisturbStart</c> / <c>doNotDisturbEnd</c> 同上，
/// 无消费方；日程静音见 Events 的 <c>isMuted</c>。
/// </summary>
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

/// <summary>
/// <c>POST /api/v1/calendar/import-ics</c> 的 multipart 请求体（issue #417 第二节第 1 条）。
///
/// 端点仍直接读 <see cref="Microsoft.AspNetCore.Http.HttpRequest"/> 的表单（匹配、绑定、错误文案
/// 都不变），这个 DTO 由 <c>IcsImportRequestBodyOperationFilter</c> 用来生成 requestBody ——
/// 此前契约里该 operation <b>完全没有 requestBody</b>，客户端生成不出上传接口。
/// 不用 <c>.Accepts&lt;T&gt;</c>：那个元数据会参与端点匹配，非 multipart 请求会变成 404。
/// </summary>
public record IcsImportForm(
    Microsoft.AspNetCore.Http.IFormFile File,
    string? CalendarId
);
