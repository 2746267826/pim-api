export interface ApiResponse<T> {
  code: number;
  message: string;
  data: T;
  timestamp: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: { id: string; username: string; displayName: string; role: string };
}

export interface CalendarResponse {
  id: string;
  name: string;
  color: string;
  kind: string;
  isDefault: boolean;
  eventCount?: number;
  source?: string;
  outlookCalendarBindingId?: string;
  canEdit?: boolean;
}

export interface EventPerson {
  name?: string | null;
  email?: string | null;
}

export interface EventAttendee {
  name?: string | null;
  email: string;
  type?: string;
}

export interface EventAttachmentReference {
  kind: string;
  id: string;
  name: string;
  contentType?: string | null;
  size?: number | null;
  canDownload?: boolean;
}

export interface OutlookAdditionalInfoItem {
  key: string;
  label: string;
  value: string;
}

export interface OutlookAdditionalInfoGroup {
  key: string;
  label: string;
  items: OutlookAdditionalInfoItem[];
}

export interface OutlookAdditionalInfo {
  groups: OutlookAdditionalInfoGroup[];
  hiddenFieldCount: number;
}

export interface EventResponse {
  id: string;
  calendarId: string;
  uid: string;
  title: string;
  description?: string;
  location?: string;
  dtStart: string;
  dtEnd: string;
  rrule?: string;
  status: string;
  source: string;
  originalEventId?: string;
  isAllDay?: boolean;
  timeZoneId?: string;
  sourceTimeZoneId?: string;
  sourceUid?: string;
  recurrenceId?: string;
  exDatesJson?: string;
  recurrenceMetadataJson?: string;
  outlookCalendarBindingId?: string;
  outlookEventId?: string;
  outlookEtag?: string;
  outlookEventType?: string;
  outlookAdditionalInfo?: OutlookAdditionalInfo | null;
  descriptionFormat?: string | null;
  showAs?: string | null;
  importance?: string | null;
  sensitivity?: string | null;
  categories?: string[] | null;
  isReminderOn?: boolean | null;
  reminderMinutesBeforeStart?: number | null;
  organizer?: EventPerson | null;
  attendees?: EventAttendee[] | null;
  isOnlineMeeting?: boolean | null;
  onlineMeetingProvider?: string | null;
  onlineMeetingUrl?: string | null;
  externalLink?: string | null;
  attachmentReferences?: EventAttachmentReference[] | null;
  isSeriesMaster?: boolean | null;
  isException?: boolean | null;
  seriesMasterId?: string | null;
  isCancelled?: boolean | null;
}

export interface TaskResponse {
  id: string;
  calendarId?: string;
  taskBookId?: string;
  title: string;
  description?: string;
  priority: number;
  estimatedDuration?: string;
  minimumSegment?: string;
  percentComplete?: number;
  dtStart?: string;
  due?: string;
  plannedEnd?: string;
  status: string;
  isInbox: boolean;
  sortOrder?: number;
  subTasks?: TaskResponse[];
  checklistItems?: TaskChecklistItem[];
}

export type TaskPlanningState =
  | 'Inbox'
  | 'ToPlan'
  | 'Planned'
  | 'InProgress'
  | 'Waiting'
  | 'Blocked'
  | 'Deferred'
  | 'Paused'
  | 'Completed'
  | 'Cancelled';

export type CalendarLayerId = 'events' | 'task-segments' | 'habits' | 'availability' | 'ai-placeholders';

export type EndpointPlatform = 'windows' | 'android';

export type NotificationActionResult = 'Executed' | 'OpenDetailRequired' | 'Rejected' | 'Failed';

export type WorkbenchDensityMode = 'standard' | 'dense' | 'focus';

export interface DomainProject {
  id: string;
  name: string;
  description?: string | null;
  status: string;
}

export interface CreateDomainProjectRequest {
  name: string;
  description?: string | null;
  status?: string | null;
}

export interface TaskBook {
  id: string;
  domainProjectId?: string | null;
  name: string;
  kind: string;
  status: string;
  taskCount?: number;
}

export interface CreateTaskBookRequest {
  domainProjectId?: string | null;
  name: string;
  kind?: string | null;
  status?: string | null;
}

export interface TaskChecklistItem {
  id: string;
  taskId: string;
  title: string;
  isDone: boolean;
  sortOrder: number;
}

export interface AddTaskChecklistItemRequest {
  title: string;
  sortOrder?: number | null;
}

export interface HabitRoutine {
  id: string;
  title: string;
  cadence: 'Daily' | 'Weekly' | 'Monthly' | string;
  source: string;
  status: string;
}

export interface CreateHabitRequest {
  title: string;
  description?: string | null;
  cadence?: string | null;
  source?: string | null;
  status?: string | null;
  ruleJson?: string | null;
}

export interface ReminderSummary {
  id: string;
  relatedObjectType?: string;
  relatedObjectId?: string;
  title: string;
  body?: string;
  triggerReason?: string;
  riskLevel: OperationRiskLevel;
  channels: string[];
  doNotDisturbStart?: string | null;
  doNotDisturbEnd?: string | null;
  scheduledAt?: string;
  status: string;
  escalationPolicy?: string | null;
  deliveryHistory?: ReminderDelivery[];
  responseHistory?: ReminderDelivery[];
}

export interface ReminderDelivery {
  id: string;
  reminderId: string;
  channel: string;
  status: string;
  payloadJson: string;
  createdAt: string;
  respondedAt?: string | null;
}

export interface ReminderActionResponse {
  kind: string;
  status: string;
  detailUrl?: string | null;
}

export interface CreateReminderRequest {
  relatedObjectType: string;
  relatedObjectId: string;
  title: string;
  body?: string;
  triggerReason?: string;
  riskLevel?: OperationRiskLevel;
  channels?: string[];
  doNotDisturbStart?: string | null;
  doNotDisturbEnd?: string | null;
  scheduledAt: string;
}

export interface GenerateReportRequest {
  kind: 'Daily' | 'Weekly' | 'Monthly' | 'Project' | string;
  date: string;
  projectId?: string | null;
}

export interface ReportArtifact {
  id: string;
  kind: string;
  title?: string;
  projectId?: string | null;
  riskLevel: OperationRiskLevel;
  contentMarkdown?: string;
  metricsJson?: string;
  generatedAt: string;
  status?: string;
  suggestions?: ReportSuggestion[];
  confirmationId?: string | null;
}

export interface ReportSuggestion {
  id: string;
  reportId: string;
  action: string;
  summary: string;
  status: string;
  confirmationId?: string | null;
}

export interface SyncConflict {
  id: string;
  provider: string;
  objectType: string;
  objectId: string;
  graphEventId?: string | null;
  conflictKind?: string;
  changedFields: string[];
  status: string;
  resolvedConfirmationId?: string | null;
}

export interface AuditVersion {
  id: string;
  objectType: string;
  objectId: string;
  confirmationId?: string | null;
  source?: string;
  actor?: string;
  beforeJson: string;
  afterJson: string;
  changedFields: string[];
  changedFieldsJson?: string;
  createdAt: string;
}

export interface AuditTimelineResponse {
  items: AuditVersion[];
}

export interface AuditExportResponse {
  fileName: string;
  contentType: string;
  content: string;
}

export interface RestorePreviewResponse {
  objectType: string;
  objectId: string;
  summary: string;
  requiresConfirmation: boolean;
  changedFields: string[];
  beforeJson?: string | null;
  afterJson?: string | null;
}

export interface DataCenterObjectRef {
  objectType: string;
  objectId: string;
}

export interface DataCenterBatchOperationRequest {
  action: string;
  objects: DataCenterObjectRef[];
  reason?: string | null;
}

export interface DataCenterBatchPreviewResponse {
  riskLevel: OperationRiskLevel;
  requiresStrictConfirmation: boolean;
  summary: string;
  affectedObjectTypes: string[];
  affectedCount: number;
}

export interface DataCenterBatchExecutionResponse {
  confirmationId: string;
  status: string;
  affectedCount: number;
}

export interface EndpointStatus {
  deviceId: string;
  platform: EndpointPlatform;
  uploadStatus: PimHealthStatus | string;
  collectionCacheCount: number;
  onlineOnlyBlockedCount: number;
  lastHeartbeatAt?: string | null;
}

export interface EndpointHeartbeatRequest {
  platform: EndpointPlatform;
  appVersion?: string | null;
  uploadStatus?: string | null;
  collectionCacheCount?: number;
}

export interface EndpointCollectionQuality {
  deviceId: string;
  platform: EndpointPlatform;
  uploadStatus: PimHealthStatus | string;
  issueCount: number;
  checkedAt: string;
}

export interface EndpointNotificationActionRequest {
  action: string;
  riskLevel: OperationRiskLevel;
  confirmationId?: string | null;
  relatedObjectType?: string | null;
  relatedObjectId?: string | null;
}

export interface EndpointNotificationActionResponse {
  result: NotificationActionResult;
  detailUrl?: string | null;
  message?: string | null;
}

export interface CreateTaskExecutionSegmentRequest {
  startsAt: string;
  endsAt: string;
  status: string;
  source: string;
  planningReason?: string | null;
}

export interface TaskExecutionSegmentResponse {
  id: string;
  taskId: string;
  taskTitle: string;
  startsAt: string;
  endsAt: string;
  status: string;
  source: string;
  planningReason?: string | null;
  confirmationId?: string | null;
}

export interface CalendarLayerQueryRequest {
  start: string;
  end: string;
  layers?: Array<CalendarLayerId | string>;
  outlookOnly?: boolean;
}

export interface CalendarLayerItem {
  id: string;
  layer: CalendarLayerId | string;
  objectType: string;
  objectId: string;
  title: string;
  startsAt: string;
  endsAt: string;
  source: string;
  status: string;
  color: string;
  requiresConfirmation: boolean;
}

export interface CalendarLayerResponse {
  start: string;
  end: string;
  items: CalendarLayerItem[];
}

export interface DataCenterQueryRequest {
  search?: string | null;
  objectType?: string | null;
  source?: string | null;
  pendingOnly: boolean;
  page?: number;
  pageSize?: number;
}

export interface DataCenterItem {
  objectType: string;
  objectId: string;
  title: string;
  source: string;
  status: string;
  startsAt?: string | null;
  endsAt?: string | null;
  summary: string;
}

export interface DataCenterQueryResponse {
  items: DataCenterItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface OutlookSettingsResponse {
  provider: string;
  tenantId?: string | null;
  clientId?: string | null;
  scopes?: string | null;
  status: string;
  tokenHealth?: string | null;
  lastSyncedAt?: string | null;
  lastError?: string | null;
  uiStatus?: string | null;
  activeAuthorization?: OutlookAuthorizationSessionResponse | null;
}

export interface UpdateOutlookSettingsRequest {
  clientId: string;
}

export interface OutlookAuthorizationSessionResponse {
  id: string;
  status: string;
  verificationUri?: string | null;
  userCode?: string | null;
  expiresAt?: string | null;
  accountDisplayName?: string | null;
  accountLoginHint?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  recoveryAction?: string | null;
}

export interface OutlookCalendarBindingResponse {
  id: string;
  pimCalendarId: string;
  graphCalendarId: string;
  groupId?: string | null;
  groupName?: string | null;
  name: string;
  color?: string | null;
  ownerName?: string | null;
  ownerAddress?: string | null;
  isDefault: boolean;
  canEdit: boolean;
  isSelected: boolean;
  remoteState: string;
  lastSyncedAt?: string | null;
  lastError?: string | null;
}

export interface OutlookSyncRequest {
  mode: 'normal' | 'full-resources' | 'range-instances';
  calendarBindingIds?: string[];
  rangeStart?: string;
  rangeEnd?: string;
  retryOfBatchId?: string;
}

export interface OutlookSyncStep {
  name: string;
  status: string;
  detail: string;
  at: string;
}

export interface OutlookSyncBatchResponse {
  id: string;
  provider: string;
  status: string;
  readCount: number;
  createdCount: number;
  updatedCount: number;
  conflictCount: number;
  confirmationCount: number;
  failureCount: number;
  steps: OutlookSyncStep[];
  errorSummary?: string | null;
  startedAt: string;
  finishedAt?: string | null;
  mode?: string;
  requestedWindowStart?: string | null;
  requestedWindowEnd?: string | null;
  perCalendarJson?: string | null;
  cancelRequested: boolean;
}

export interface OutlookPerCalendarChange {
  id: string;
  title: string;
  action: string;
}

export interface OutlookPerCalendarFailure {
  eventId?: string;
  title?: string;
  code?: string;
  message?: string;
}

export interface OutlookPerCalendarResult {
  bindingId: string;
  calendarName: string;
  status: string;
  /**
   * #309：该日历在 Outlook 端已确认删除，PIM 已跟随删除并移入回收站。
   * 此时 status 是 'completed'（对用户来说这是成功结果），因此需要这个独立标志
   * 才能把它与普通同步结果区分开。
   */
  mirrorDeleted?: boolean;
  readCount: number;
  createdCount: number;
  updatedCount: number;
  deletedCount: number;
  failureCount: number;
  changes: OutlookPerCalendarChange[];
  failures: OutlookPerCalendarFailure[];
  retryOfBatchId?: string;
}

export interface OutlookSyncBatchPage {
  items: OutlookSyncBatchResponse[];
  total: number;
  page: number;
  pageSize: number;
}

export interface OutlookLocalDataPreview {
  bindingCount: number;
  calendarCount: number;
  eventCount: number;
}

export interface UnifiedEventDraft {
  calendarId: string;
  title: string;
  description?: string | null;
  descriptionFormat?: string | null;
  location?: string | null;
  dtStart: string;
  dtEnd: string;
  rrule?: string | null;
  isAllDay?: boolean;
  timeZoneId?: string | null;
  showAs?: string | null;
  importance?: string | null;
  sensitivity?: string | null;
  categories?: string[] | null;
  isReminderOn?: boolean | null;
  reminderMinutesBeforeStart?: number | null;
  organizer?: EventPerson | null;
  attendees?: EventAttendee[] | null;
  isOnlineMeeting?: boolean | null;
  onlineMeetingProvider?: string | null;
  onlineMeetingUrl?: string | null;
  externalLink?: string | null;
  attachmentReferences?: EventAttachmentReference[] | null;
  isSeriesMaster?: boolean | null;
  isException?: boolean | null;
  seriesMasterId?: string | null;
  recurrenceId?: string | null;
}

export interface OutlookEventDraft extends UnifiedEventDraft {
  rRule?: string;
  uid?: string;
}

export interface OutlookWriteRequest {
  operation: 'create' | 'update' | 'delete';
  calendarBindingId: string;
  eventId?: string;
  draft?: OutlookEventDraft;
  scope: 'instance' | 'series';
  clientOperationId: string;
  expectedEtag?: string;
  originalEventId?: string;
  recurrenceId?: string;
}

export interface OutlookWriteResult {
  status: string;
  event?: EventResponse | null;
  /** @deprecated Raw Outlook JSON snapshot; use latestEvent instead. */
  latestOutlookJson?: string | null;
  latestEvent?: EventResponse | null;
  latestEtag?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export type OperationRiskLevel =
  | 'Low'
  | 'Medium'
  | 'High'
  | 'L0AutomaticArtifact'
  | 'L1LowRiskAction'
  | 'L2PimFactChange'
  | 'L3ExternalSourceOrWriteback'
  | 'L4BatchOrDestructiveGovernance';

export type OperationConfirmationStatus =
  | 'Pending'
  | 'Confirmed'
  | 'Rejected'
  | 'Expired'
  | 'Executed';

export interface OperationConfirmation {
  id: string;
  requestedByUserId?: string | null;
  operationType: string;
  summary: string;
  riskLevel: OperationRiskLevel;
  source: string;
  payloadJson: string;
  previewJson: string;
  status: OperationConfirmationStatus;
  expiresAt: string;
  createdAt: string;
  confirmedAt?: string | null;
  executedAt?: string | null;
  resultJson?: string | null;
  correlationId?: string | null;
  changedFields?: string[] | null;
  allowedActions?: string[] | null;
  objectType?: string | null;
  objectId?: string | null;
  requiresSecondLevelConfirmation: boolean;
  beforeJson?: string | null;
  afterJson?: string | null;
  requiresStrictConfirmation?: boolean;
  auditBatchId?: string | null;
  aiRecommendation?: string | null;
  externalEffect?: string | null;
  recoveryPath?: string | null;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface ImportResult {
  imported: number;
  skipped: number;
}

export interface CalendarOperationSample {
  id: string;
  type: string;
  title: string;
  start?: string;
  end?: string;
  bookName?: string;
}

export interface CalendarDeletePreviewResponse {
  targetType: string;
  targetId: string;
  title: string;
  operationKind: string;
  affectedCount: number;
  samples: CalendarOperationSample[];
  summary: string;
  requiresStrictConfirmation: boolean;
}

export interface CalendarOperationResult {
  operation: string;
  operationId: string;
  affectedCount: number;
  affectedIds: string[];
  samples: CalendarOperationSample[];
  message: string;
}

export interface CalendarRestoreConflict {
  deletedId: string;
  deletedType: string;
  activeId: string;
  activeType: string;
  reason: string;
  title: string;
}

export interface CalendarRestorePreviewResponse {
  targetType: string;
  targetId: string;
  title: string;
  restoreCount: number;
  samples: CalendarOperationSample[];
  conflicts: CalendarRestoreConflict[];
  canRestoreWithoutConflict: boolean;
}

export interface CalendarRecycleBinItem {
  id: string;
  type: string;
  title: string;
  deletedAt: string;
  bookName?: string;
  start?: string;
  end?: string;
  source: string;
  deletedByOperationId?: string;
  deletedByOperationKind?: string;
}

export interface ImportSkippedItem {
  reason: string;
  title: string;
  start?: string;
  uid?: string;
}

export interface ImportReport {
  imported: number;
  skipped: number;
  skippedReasons: Record<string, number>;
  samples: ImportSkippedItem[];
}

export type PimHealthStatus = 'Unknown' | 'Healthy' | 'Warning' | 'Critical';

export interface SystemStatusSummary {
  status: PimHealthStatus;
  label: string;
  message: string;
  checkedAt: string;
}

export interface StatusComponent {
  key: string;
  name: string;
  kind: string;
  status: PimHealthStatus;
  message: string;
  checkedAt: string;
  details: Record<string, string>;
}

export interface SystemStatusDetail {
  summary: SystemStatusSummary;
  components: StatusComponent[];
  nextSteps: string[];
}

export interface PcQualityComponent {
  key: string;
  name: string;
  status: PimHealthStatus;
  message: string;
  details: Record<string, string>;
}

export interface PcQualityIssue {
  code: string;
  severity: PimHealthStatus;
  componentKey: string;
  message: string;
  nextStep: string | null;
}

export interface PcQualityResponse {
  overallStatus: PimHealthStatus;
  label: string;
  message: string;
  checkedAt: string;
  components: PcQualityComponent[];
  issues: PcQualityIssue[];
  nextSteps: string[];
}

export interface PcQualityQueryParams {
  date?: string;
  dateFrom?: string;
  dateTo?: string;
}

// PC Tracker types
export interface PcSummaryResponse {
  keystats: KeystatsSummary | null;
  heatmap: HeatmapBucket[];
  appRanking: AppRankingItem[];
  timeline: TimelineItem[];
  sessions: WorkSessionItem[];
  metrics: DerivedMetrics | null;
  categories: CategorySummary[];
}

export interface KeystatsSummary {
  date: string;
  keyPresses: number;
  totalClicks: number;
  leftClicks: number;
  rightClicks: number;
  middleClicks: number;
  sideBackClicks: number;
  sideForwardClicks: number;
  mouseDistance: number;
  scrollDistance: number;
  peakKps: number;
  peakCps: number;
  keyPressCounts: Record<string, number>;
  topKeys: KeyCountItem[];
}

export interface KeyCountItem {
  keyName: string;
  count: number;
  share: number;
}

export interface HeatmapBucket {
  start: string;
  end: string;
  hour: number;
  activeMinutes: number;
  totalEvents: number;
  intensityScore: number;
}

export interface AppRankingItem {
  appName: string;
  displayName: string;
  keyPresses: number;
  totalClicks: number;
  scrollDistance: number;
  share: number;
}

export interface TimelineItem {
  start: string;
  end: string;
  durationMinutes: number;
  appName: string;
  windowTitle: string | null;
  categoryName: string;
  categoryColor: string;
  projectTag: string | null;
  classificationConfidence: number;
  classificationSource: string;
  classificationExplanation: string;
}

export interface WorkSessionItem {
  start: string;
  end: string;
  durationMinutes: number;
  mainApp: string;
  appSwitchCount: number;
}

export interface DerivedMetrics {
  totalRecordedDuration: string;
  activeInputDuration: string;
  idleDuration: string;
  sessionCount: number;
  activeAppCount: number;
  totalKeyPresses: number;
  totalClicks: number;
  appSwitchCount: number;
  switchFrequency: number;
  mostFocusedApp: string;
  keyClickRatio: number;
}

export interface CategorySummary {
  categoryName: string;
  color: string;
  share: number;
  keyPresses: number;
  totalClicks: number;
}

export interface AppCategoryRule {
  id: string;
  appPattern: string;
  categoryName: string;
  color: string;
  priority: number;
  isBuiltin: boolean;
}

export interface ActivityClassificationRule {
  id: string;
  ruleName: string;
  scope: string;
  categoryName: string | null;
  projectTag: string | null;
  color: string;
  priority: number;
  source: string;
  status: string;
  conditionsJson: string;
  confidence: number;
  explanation: string | null;
}

export interface ActivityClassificationSuggestion {
  id: string;
  clusterKey: string;
  sampleCount: number;
  totalDurationSeconds: number;
  sampleRecordsJson: string;
  sanitizedContextJson: string;
  currentCategory: string | null;
  suggestedCategory: string | null;
  suggestedProjectTag: string | null;
  suggestedRulesJson: string | null;
  userFeedback: string | null;
  llmResponseJson: string | null;
  status: string;
  appDisplayName?: string | null;
  appIcon?: string | null;
  recognitionSource?: string | null;
}

export interface ActivityClassificationApplyRange {
  mode: 'today' | 'range';
  dateFrom?: string | null;
  dateTo?: string | null;
}

export interface SaveActivityClassificationRuleRequest {
  ruleName: string;
  scope: string;
  categoryName: string | null;
  projectTag: string | null;
  color: string;
  priority: number;
  conditionsJson: string;
  confidence: number;
  explanation: string | null;
}

export interface ActivityClassificationPreview {
  affectedRecordCount: number;
  affectedDurationSeconds: number;
  currentCategoryCounts: Record<string, number>;
  newCategoryCounts: Record<string, number>;
  samples: PcDetailRecord[];
  requiresConfirmation: boolean;
  summary: string;
}

export interface SuggestionClassificationPreviewRequest {
  categoryName: string | null;
  projectTag: string | null;
  range: ActivityClassificationApplyRange;
}

export interface ActivityClassificationSuggestionPreview {
  rule: SaveActivityClassificationRuleRequest;
  preview: ActivityClassificationPreview;
}

export interface SuggestionClassificationApplyRequest {
  categoryName: string | null;
  projectTag: string | null;
  range: ActivityClassificationApplyRange;
}

export interface ActivityClassificationSuggestionApply {
  rule: ActivityClassificationRule;
  preview: ActivityClassificationPreview;
  auditId: string;
  suggestionStatus: string;
}

export interface PcActivityAnalysisResponse {
  date: string;
  blockMinutes: number;
  blocks: PcActivityAnalysisBlock[];
}

export interface PcActivityAnalysisBlock {
  start: string;
  end: string;
  intensityScore: number;
  activeDurationSeconds: number;
  pendingClassificationCount: number;
  contextSwitchCount: number;
  categoryChangeCount: number;
  categories: PcActivityAnalysisCategory[];
  apps: PcActivityAnalysisApp[];
}

export interface PcActivityAnalysisCategory {
  categoryName: string;
  color: string;
  durationSeconds: number;
}

export interface PcActivityAnalysisApp {
  appName: string;
  durationSeconds: number;
}

export interface ActivityClassificationSettings {
  recommendedMinimumClassificationDurationMinutes: number;
  supportedRecommendedMinimumDurations: number[];
}

export interface DetailQueryParams {
  dateFrom?: string;
  dateTo?: string;
  dimension?: 'hour' | 'day' | 'month' | 'year';
  deviceId?: string;
  appName?: string;
  categoryName?: string;
  keyName?: string;
  domain?: string;
  title?: string;
  url?: string;
  view?: 'raw' | 'interpreted' | string;
  eventType?: string;
  sortBy?: string;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

export type PcDetailRecordType = 'window' | 'afk' | 'input-minute' | 'app-input' | 'key-input' | 'web' | 'web-page';

export interface PcDetailRecord {
  recordType: PcDetailRecordType | string;
  start: string;
  end: string | null;
  durationSeconds: number | null;
  deviceId: string;
  appName: string | null;
  displayName: string | null;
  categoryName: string | null;
  categoryColor?: string | null;
  projectTag?: string | null;
  classificationConfidence?: number | null;
  classificationSource?: string | null;
  classificationExplanation?: string | null;
  title: string | null;
  url?: string | null;
  domain?: string | null;
  path?: string | null;
  isLocalFile?: boolean | null;
  browserAppName?: string | null;
  browserWindowTitle?: string | null;
  audible?: boolean | null;
  incognito?: boolean | null;
  tabCount?: number | null;
  absorbedShortEventsCount?: number | null;
  absorbedDurationSeconds?: number | null;
  sourceWebEventIds?: number[] | null;
  sourceWindowEventIds?: number[] | null;
  recordKey?: string | null;
  recordKeyVersion?: string | null;
  recordKeyStability?: string | null;
  sourceBucketIds?: string[] | null;
  sourceType?: string | null;
  interpretationVersion?: string | null;
  keyPresses: number | null;
  totalClicks: number | null;
  mouseDistance: number | null;
  scrollDistance: number | null;
  keyCounts: Record<string, number> | null;
  raw: unknown;
}

export interface DetailQueryResponse {
  items: PcDetailRecord[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface HeatmapGridResponse {
  grid: HeatmapBucket[][];
  dimension: string;
  maxKeyCount: number;
}

export type TodaySectionStatus =
  | 'available'
  | 'normal'
  | 'empty'
  | 'warning'
  | 'critical'
  | 'unavailable';

export interface TodayLink {
  rel: 'self' | 'details' | 'api' | string;
  href: string;
}

export interface TodaySectionError {
  code: string;
  message: string;
}

export interface TodaySectionRegistryItem {
  id: string;
  kind: TodaySectionKind | string;
  status: TodaySectionStatus;
  links: TodayLink[];
}

export interface TodaySectionRegistry {
  date: string;
  pcBusinessDate: string;
  generatedAt: string;
  sections: TodaySectionRegistryItem[];
}

export interface TodaySection<TData = unknown> {
  id: string;
  kind: TodaySectionKind | string;
  status: TodaySectionStatus;
  generatedAt: string;
  data: TData;
  links: TodayLink[];
  error: TodaySectionError | null;
}

export type TodaySectionKind =
  | 'calendar.schedule'
  | 'calendar.tasks'
  | 'pc.activity'
  | 'pc.quality'
  | 'operations.health'
  | 'pc.classification_suggestions'
  | 'operations.confirmations'
  | 'sync.outlook'
  | 'reminders.queue'
  | 'reports.available'
  | 'endpoints.status'
  | 'calendar.availability'
  | 'calendar.habits'
  | 'calendar.ai_placeholders';

export interface CalendarScheduleTodayData {
  events: EventResponse[];
  scheduledTasks: TaskResponse[];
}

export interface CalendarTasksTodayData {
  incompleteCount: number;
  dueTodayTasks: TaskResponse[];
  overdueTasks: TaskResponse[];
  unscheduledTasks: TaskResponse[];
}

export interface PcActivityTodayData {
  summary: PcSummaryResponse;
}

export interface PcQualityTodayData {
  quality: PcQualityResponse;
  issueCount: number;
}

export interface OperationsHealthTodayData {
  detail: SystemStatusDetail;
  summary: SystemStatusSummary;
}

export interface ClassificationSuggestionsTodayData {
  pendingCount: number;
  suggestions: ActivityClassificationSuggestion[];
}

export type QuickNoteStatus = 'inbox' | 'processed' | 'archived';

export interface QuickNoteAttachment {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  downloadUrl: string;
  previewUrl: string | null;
  createdAt: string;
}

export interface QuickNoteListItem {
  id: string;
  contentPreview: string;
  status: QuickNoteStatus;
  source: string;
  attachmentCount: number;
  attachments?: QuickNoteAttachment[];
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
}

export interface QuickNoteDetail extends QuickNoteListItem {
  contentMarkdown: string;
  attachments: QuickNoteAttachment[];
  metadataJson: string;
}

export interface CreateQuickNoteRequest {
  contentMarkdown: string;
  source?: string;
  attachmentIds?: string[];
}

export interface UpdateQuickNoteRequest {
  contentMarkdown: string;
  status?: QuickNoteStatus;
  attachmentIds?: string[];
}

export interface QuickNoteAttachmentUpload {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  downloadUrl: string;
  previewUrl: string | null;
}

export type AiRequestStatus = 'Succeeded' | 'Failed' | 'Blocked' | 'TimedOut' | 'FailedValidation';

export interface AiStatus {
  enabled: boolean;
  provider: string;
  baseUrl: string;
  defaultModel: string;
  lastHealthCheckAt?: string | null;
  lastError?: string | null;
  recentSuccessfulCallAt?: string | null;
}

export interface AiRequestLogListItem {
  id: string;
  startedAt: string;
  module: string;
  purpose: string;
  model: string;
  status: AiRequestStatus;
  totalTokens?: number | null;
  estimatedCost?: number | null;
  durationMs?: number | null;
  sourceObjectType: string;
  sourceObjectId: string;
  errorSummary?: string | null;
}

export interface AiRequestLogDetail extends AiRequestLogListItem {
  userId?: string | null;
  provider: string;
  liteLlmRequestId?: string | null;
  correlationId: string;
  attemptNumber: number;
  maxAttempts: number;
  finishedAt?: string | null;
  requestMessagesJson: string;
  requestPayloadJson: string;
  responseRawJson: string;
  responseText?: string | null;
  parsedOutputJson?: string | null;
  schemaName?: string | null;
  schemaVersion?: string | null;
  schemaJsonSnapshot?: string | null;
  schemaValidationErrorsJson: string;
  usage: {
    promptTokens?: number | null;
    completionTokens?: number | null;
    totalTokens?: number | null;
    estimatedCost?: number | null;
    currency?: string | null;
  };
  errorCode?: string | null;
  errorMessage?: string | null;
  metadataJson: string;
}

export interface AiUsageGroup {
  groupKey: string;
  requestCount: number;
  successCount: number;
  failureCount: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  estimatedCost: number;
}

export interface AiUsageSummary {
  requestCount: number;
  successCount: number;
  failureCount: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  estimatedCost: number;
  byModule: AiUsageGroup[];
  byPurpose: AiUsageGroup[];
  byModel: AiUsageGroup[];
  byStatus: AiUsageGroup[];
}

// Files module types
export interface FileProvider {
  id: string;
  provider: string;
  baseUrl: string;
  internalBaseUrl: string | null;
  username: string;
  status: string;
  lastSyncAt: string | null;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
  clientId?: string | null;
  driveId?: string | null;
  accountId?: string | null;
  accountName?: string | null;
  syncStatus?: string;
  syncedItemCount?: number;
  /** #353：已索引总量（未删除），与最近一次运行的变更数区分。 */
  totalIndexedCount?: number;
  deltaResetAt?: string | null;
  tokenExpiresAt?: string | null;
}

export interface OneDriveBindingStart {
  providerId: string;
  userCode: string;
  verificationUri: string;
  expiresIn: number;
}

export interface OneDriveBindingStatus {
  status: 'pending' | 'connected' | 'expired' | 'denied';
  driveId: string | null;
  accountId: string | null;
  accountName: string | null;
  userCode: string | null;
  verificationUri: string | null;
  deviceCodeExpiresAt: string | null;
  pollIntervalSeconds?: number | null;
}

export interface OneDriveSyncResult {
  pagesProcessed: number;
  itemsApplied: number;
  itemsDeleted: number;
  fullRecrawl: boolean;
}

export interface OneDriveLink {
  url: string;
}

export interface OneDriveTextContent {
  content: string;
  mimeType: string | null;
  size: number;
  truncated: boolean;
}

export interface FileTextSnapshot {
  id: string;
  path: string;
  name: string;
  content: string;
  byteSize: number;
  reason: string;
  createdAt: string;
}

export interface BindNextcloudProviderRequest {
  baseUrl: string;
  internalBaseUrl: string | null;
  username: string;
  appPassword: string;
}

export interface FileProviderTest {
  success: boolean;
  status: string;
  errorMessage: string | null;
}

export interface FileItem {
  id: string;
  providerId: string;
  externalFileId: string;
  parentExternalFileId: string | null;
  path: string;
  name: string;
  itemType: string;
  mimeType: string | null;
  size: number | null;
  etag: string | null;
  contentHash: string | null;
  currentVersionId: string | null;
  permissions: string | null;
  isDeleted: boolean;
  deletedAt: string | null;
  lastSeenAt: string | null;
  createdAt: string;
  modifiedAt: string;
  syncedAt: string;
  indexStatus: string;
  ai: FileAiResult | null;
}

export interface FileVersion {
  id: string;
  fileItemId: string;
  externalVersionId: string;
  etag: string | null;
  size: number | null;
  modifiedAt: string;
  source: string;
  isCurrent: boolean;
  syncedAt: string;
}

export interface FileAiResult {
  id: string;
  fileItemId: string;
  versionId: string;
  summary: string;
  tags: string[];
  language: string | null;
  sensitivity: string | null;
  generatedAt: string;
  model: string | null;
  aiRequestLogId: string | null;
  evidenceChunkIds: string[];
}

export interface FileSuggestion {
  id: string;
  fileItemId: string;
  suggestionType: string;
  title: string;
  reason: string;
  confidence: number;
  payloadJson: string;
  status: string;
  aiRequestLogId: string | null;
  createdAt: string;
  updatedAt: string;
}

/** REQ-25：手动同步已入队的即时反馈。 */
export interface OneDriveSyncStarted {
  started: boolean;
  message: string;
}

/** REQ-25：顶部横幅读取的同步状态。 */
export interface OneDriveSyncStatus {
  syncStatus: string;
  lastError: string | null;
  lastSyncAt: string | null;
  /**
   * **最近一次同步运行**中应用（新增/更新）的项目数；每次运行覆盖写入，
   * 无增量变更的日常同步即为 0。展示时不得当作累计总量（#353）。
   */
  syncedItemCount: number;
  /**
   * #353：当前已索引的条目总量（未删除）。与 `syncedItemCount` 语义不同，
   * 横幅按「上次同步时间 · 本次变更数 · 已索引总量」的组合口径展示。
   */
  totalIndexedCount?: number;
}

/** REQ-21：分享链接。 */
export interface FileShare {
  itemId: string;
  itemName: string;
  path: string;
  permissionType: string;
  permissionId: string | null;
  webUrl: string;
  expiresAt: string | null;
  createdAt: string;
}

/** REQ-14：上传会话（uploadUrl 已预授权，分片 PUT 不得带 Authorization）。 */
export interface OneDriveUploadSessionInfo {
  uploadUrl: string;
  expirationDateTime: string | null;
  /** 记录创建会话时用的路径，便于上传完成后登记元数据 */
  path: string;
  fileName: string;
}

export interface FileListResponse {
  result: PagedResult<FileItem>;
}

/** 列表排序键（REQ-9）；默认 name（文件夹恒在前）。 */
export type FileSortKey = 'name' | 'modified' | 'size';
export type FileSortOrder = 'asc' | 'desc';

/** 列表条目类型过滤（REQ-4：树只用 folder）。 */
export type FileItemTypeFilter = 'folder' | 'file';

/**
 * 搜索模式（REQ-8）：folder = 只过滤当前文件夹（服务端过滤）；
 * global = 全盘搜索（/files/search，结果带完整路径）。
 */
export type FileSearchScope = 'folder' | 'global';

/** 兼容旧的元数据搜索模式取值（MCP search_files 沿用）。 */
export type FileSearchMode = 'keyword' | 'semantic' | 'hybrid';

export interface FileSearchResult {
  items: FileItem[];
  chunks: FileChunkSearchHit[];
  /** 命中总数（PR-1 新增，用于「共 N 项 · 第 X/Y 页」与翻页）。 */
  totalCount: number;
  totalPages: number;
}

export interface FileChunkSearchHit {
  chunkId: string;
  fileItemId: string;
  versionId: string;
  text: string;
  score: number;
}

export interface MoveFileRequest {
  destinationPath: string;
}

export interface RenameFileRequest {
  name: string;
}

export interface FileOpenLink {
  url: string;
  mode: string;
}

export type FileOpenLinkMode = 'view' | 'edit' | 'nextcloud';

export interface VersionRestorePreview {
  fileItemId: string;
  versionId: string;
  currentVersionLabel: string;
  restoreVersionLabel: string;
  requiresConfirmation: boolean;
  summary: string;
}

export interface FileIndexJob {
  id: string;
  fileItemId: string;
  versionId: string | null;
  status: string;
  stage: string;
  attemptCount: number;
  lastError: string | null;
}

export interface FileSuggestionStatusRequest {
  status: string;
}

export interface FileTrashItem {
  trashId: string;
  originalLocation: string;
  name: string;
  itemType: string;
  size: number | null;
  deletedAt: string;
}

// === MCP module types ===

export type McpPermissionMap = Record<string, boolean>;
export type McpPermissions = { read: McpPermissionMap; write: McpPermissionMap };

export interface McpToolInfo {
  name: string;
  group: string;
  description: string;
  isWrite: boolean;
}

export interface McpCatalog {
  read: McpToolInfo[];
  write: McpToolInfo[];
}

export interface McpClient {
  id: string;
  name: string;
  status: 'active' | 'revoked';
  tokenPrefix: string;
  permissions: McpPermissions;
  createdAt: string;
  revokedAt: string | null;
  lastSeenAt: string | null;
  callCount: number;
  writeCallCount: number;
  lastTool: string | null;
  online: boolean;
  createdByUsername: string | null;
}

export interface McpClientCreateResult {
  client: McpClient;
  token: string;
}

export interface McpClientUpdateRequest {
  name?: string;
  permissions?: McpPermissions;
}

export interface McpActivityLogEntry {
  timestamp: string;
  clientName: string;
  toolName: string;
  statusCode: number;
  durationMs: number;
  argumentsSummary: string;
}

export interface AiPlanPlaceholderViewDto {
  id: string;
  title: string;
  startsAt: string;
  endsAt: string;
  reason: string;
  status: string;
  source: string;
  confirmationId?: string | null;
}

export interface GenerateAiPlanRequest {
  horizonDays?: number;
  taskIds?: string[];
}

export interface GenerateAiPlanResponse {
  source: string;
  placeholders: AiPlanPlaceholderViewDto[];
}

export interface ActivityClassificationSuggestionV2 {
  id: string;
  clusterKey: string;
  processName: string | null;
  domain: string | null;
  appDisplayName: string | null;
  appIcon: string | null;
  currentCategory: string | null;
  recommendedCategoryName: string | null;
  recommendedCategoryId: string | null;
  recommendedProductivity: string | null;
  confidence: number;
  recognitionSource: string;
  isOnlineLookup: boolean;
  totalDurationSeconds: number;
  sampleCount: number;
  status: string;
  createdAt: string;
}

export interface BatchAcceptSuggestionItem {
  suggestionId: string;
  categoryId?: string | null;
  categoryName?: string | null;
  createRule?: boolean;
}

export interface BatchAcceptSuggestionsRequest {
  items: BatchAcceptSuggestionItem[];
}

export interface BatchAcceptResult {
  acceptedCount: number;
  rulesCreatedCount: number;
  failuresCount: number;
}

export interface AppSignature {
  id: string;
  processName: string;
  displayName: string;
  categoryPath?: string | null;
  productivity?: string | null;
  description?: string | null;
  source: string;
  confidence: number;
  icon?: string | null;
  lastSeenAt?: string | null;
  createdAt: string;
}

export interface ProductivityGoal {
  dailyProductiveHours: number;
}

export interface TimelineV2Item {
  // 业务日固定为 Asia/Shanghai 04:00 起算（服务端决定，不接受 timezone 参数）；
  // start/end 为带 +08:00 偏移的 ISO-8601，块之间互不重叠（#235 / #236 / #237）。
  start: string;
  end: string;
  appName: string;
  appDisplayName?: string | null;
  windowTitle?: string | null;
  categoryName: string;
  categoryColor?: string | null;
  productivity: string;
  confidence: number;
  durationMinutes: number;
}
