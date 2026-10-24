import type { PagedResult } from '@/api/types'
import type { components } from '@/api/generated'
import type { HabitCadence } from '@/lib/enums'

/* ── 日历本 / 任务本 ─────────────────────────────────────── */

export interface CalendarBook {
  id: string
  name: string
  color: string
  kind: 'calendar' | 'task'
  isDefault: boolean
  eventCount: number
  source: string
  outlookCalendarBindingId: string | null
  canEdit: boolean
  /**
   * 工单 04：日历本默认静音设定。该本下 isMuted 为 null（未指定）的日程，生效值取它；
   * 「批量设为静音/不静音」会写这里，并把该本下既有日程统一写成显式值。
   */
  defaultMuted: boolean
}

export interface TaskBook {
  id: string
  domainProjectId: string | null
  name: string
  kind: string
  status: string
  taskCount: number
}

/* ── 日程事件（EventResponse，字段按 05/calendar.md §日程事件） ── */

export interface EventPerson {
  name?: string
  email?: string
  /** 参会人类型（规格 attendees：type 默认 required） */
  type?: string
}

export interface EventResponse {
  id: string
  calendarId: string
  uid: string
  title: string
  description: string | null
  location: string | null
  dtStart: string
  dtEnd: string
  /** 契约字段名（Create/UpdateEventRequest 与线上响应均为 rRule，PW-05） */
  rRule?: string | null
  /** 旧数据形状：历史响应里出现过的小写 rrule；只用于兼容读取，写入用 rRule */
  rrule?: string | null
  status: string
  source: string
  originalEventId: string | null
  isAllDay: boolean
  timeZoneId: string | null
  sourceTimeZoneId: string | null
  sourceUid: string | null
  recurrenceId: string | null
  descriptionFormat: string | null
  showAs: string | null
  importance: string | null
  sensitivity: string | null
  categories: string[] | null
  isReminderOn: boolean | null
  reminderMinutesBeforeStart: number | null
  organizer: EventPerson | null
  attendees: EventPerson[] | null
  isOnlineMeeting: boolean | null
  onlineMeetingProvider: string | null
  onlineMeetingUrl: string | null
  externalLink: string | null
  isSeriesMaster: boolean
  isException: boolean
  seriesMasterId: string | null
  isCancelled: boolean
  outlookCalendarBindingId: string | null
  outlookEventId: string | null
  outlookEtag: string | null
  outlookEventType: string | null
  /**
   * 工单 04：日程期间是否静音 —— true 静音 / false 不静音 / null 未指定
   * （生效值 = isMuted ?? 所属日历本的 defaultMuted）。
   */
  isMuted: boolean | null
}

/** Outlook 镜像事件（source 以 outlook 开头）走写回通道而非直接 PUT/DELETE */
export function isOutlookEvent(e: Pick<EventResponse, 'source'>): boolean {
  return e.source.startsWith('outlook')
}

/** 工单 04 E1.3：日历本静音策略批量设定的结果（PUT /calendar/calendars/{id}/mute-policy） */
export interface CalendarMutePolicyResult {
  calendarId: string
  defaultMuted: boolean
  /** 本次被写成显式值的既有日程数量 */
  affectedEventCount: number
}

/* ── 任务（TaskResponse） ─────────────────────────────────── */

export interface TaskResponse {
  id: string
  calendarId: string | null
  uid: string
  title: string
  description: string | null
  priority: number
  estimatedDuration: string | null
  minimumSegment: string | null
  dtStart: string | null
  due: string | null
  status: string
  isInbox: boolean
  sortOrder: number
  subTasks: TaskResponse[]
  plannedEnd: string | null
  taskBookId: string | null
  percentComplete: number
  /** 前端可选声明；后端 TaskResponse 不返回（05/calendar.md 备注） */
  checklistItems?: TaskChecklistItem[]
}

/**
 * 任务状态取值（工单 03 D1）：与 pim-api `TaskStatuses` 及契约 enum 同源。
 * 集合外的值服务端会以 400（02057）明确拒绝 —— 前端不要凭自由字符串写入。
 */
export const TASK_STATUS = {
  needsAction: 'NEEDS-ACTION',
  inProcess: 'IN-PROCESS',
  completed: 'COMPLETED',
  cancelled: 'CANCELLED',
} as const

export type TaskStatus = (typeof TASK_STATUS)[keyof typeof TASK_STATUS]

/** 取值顺序即契约 enum 的顺序（NEEDS-ACTION / IN-PROCESS / COMPLETED / CANCELLED） */
export const TASK_STATUS_VALUES = [
  TASK_STATUS.needsAction,
  TASK_STATUS.inProcess,
  TASK_STATUS.completed,
  TASK_STATUS.cancelled,
] as const

/** 状态选择项 */
export const TASK_STATUS_OPTIONS: ReadonlyArray<{ value: TaskStatus; label: string }> = [
  { value: TASK_STATUS.needsAction, label: '待办' },
  { value: TASK_STATUS.inProcess, label: '进行中' },
  { value: TASK_STATUS.completed, label: '已完成' },
  { value: TASK_STATUS.cancelled, label: '已取消' },
]

export const TASK_STATUS_LABEL: Record<string, string> = Object.fromEntries(
  TASK_STATUS_OPTIONS.map((o) => [o.value, o.label]),
)

export function isKnownTaskStatus(status: string): boolean {
  return (TASK_STATUS_VALUES as readonly string[]).includes(status)
}

/**
 * 把任务状态归一到受控值域内。
 *
 * 四个合法值（含 `IN-PROCESS`）原样返回；只有**值域外的历史值** —— 即服务端收紧之前
 * 由 MCP/脚本写进库的自由字符串（工单 03 D1.3 明确不做数据迁移）—— 才回落到「待办」。
 * 调用方必须用 {@link isKnownTaskStatus} 先判断并显式提示用户，不要静默改写。
 */
export function normalizeTaskStatus(status: string): TaskStatus {
  return isKnownTaskStatus(status) ? (status as TaskStatus) : TASK_STATUS.needsAction
}

/**
 * 执行段取值（工单 03 D1.4/D2.1）：与 pim-api `TaskSegmentStatuses` 同源（全小写）。
 * `active` 表示该段正在计时（服务端 `ends_at IS NULL`，同一任务同时只允许一条）。
 */
export const TASK_SEGMENT_STATUS = {
  planned: 'planned',
  active: 'active',
  paused: 'paused',
  completed: 'completed',
  cancelled: 'cancelled',
} as const

export const TASK_SEGMENT_STATUS_LABEL: Record<string, string> = {
  planned: '计划',
  active: '计时中',
  paused: '已暂停',
  completed: '已结束',
  cancelled: '已取消',
}

/* ── 图层（五层合一） ─────────────────────────────────────── */

export type LayerName = 'events' | 'task-segments' | 'habits' | 'availability' | 'ai-placeholders'

export interface LayerItem {
  id: string
  layer: LayerName
  objectType: string
  objectId: string
  title: string
  startsAt: string
  endsAt: string
  source: string
  status: string
  color: string
  requiresConfirmation: boolean
}

/* ── 任务清单 / 时间段 ────────────────────────────────────── */

export interface TaskChecklistItem {
  id: string
  taskId: string
  title: string
  isDone: boolean
  sortOrder: number
}

export interface TaskExecutionSegment {
  id: string
  taskId: string
  taskTitle: string
  startsAt: string
  /** `null` = 该段正在计时（工单 03 D2.1：服务端开放段） */
  endsAt: string | null
  status: string
  source: string
  planningReason: string | null
  confirmationId: string | null
}

/** 任务计时状态 + 累计实际时长（`GET /calendar/tasks/{id}/timer`，工单 03 D2.1） */
export interface TaskTimerState {
  taskId: string
  taskStatus: string
  /** 正在计时的段 id；没有在计时时为 null */
  runningSegmentId: string | null
  runningSince: string | null
  /**
   * 累计实际执行秒数（正在计时的段按查询时刻计入）。
   * 口径：只有 `planned`（尚未执行）不计；`cancelled` 是**真实发生过的执行**，计入
   * —— 否则「跑一段再取消」会把时长抹掉，与「先暂停再取消」不一致（与服务端 `CountsAsExecuted` 一致）。
   */
  accumulatedSeconds: number
  segmentCount: number
}

/* ── 操作结果 / 删除预览 ─────────────────────────────────── */

export interface CalendarOperationSample {
  id: string
  type: string
  title: string
  start: string | null
  end: string | null
  bookName: string | null
}

export interface CalendarOperationResult {
  operation: string
  operationId: string
  affectedCount: number
  affectedIds: string[]
  samples: CalendarOperationSample[]
  message: string
}

export interface CalendarDeletePreview {
  targetType: string
  targetId: string
  title: string
  operationKind: string
  affectedCount: number
  samples: CalendarOperationSample[]
  summary: string
  requiresStrictConfirmation: boolean
}

/* ── 提醒（05/calendar.md §提醒） ─────────────────────────── */

export interface ReminderResponse {
  id: string
  relatedObjectType: string
  relatedObjectId: string
  title: string
  body: string
  triggerReason: string
  riskLevel: number | string
  channels: string[]
  doNotDisturbStart: string | null
  doNotDisturbEnd: string | null
  scheduledAt: string
  status: 'Open' | 'Snoozed' | 'Dismissed'
}

export interface ReminderDelivery {
  id: string
  reminderId: string
  channel: string
  status: string
  payloadJson: string
  createdAt: string
  respondedAt: string | null
}

export interface ReminderActionResponse {
  kind: 'Executed' | 'OpenDetailRequired'
  status: string
  detailUrl: string | null
}

/* ── 报告 ─────────────────────────────────────────────────── */

export interface ReportArtifact {
  id: string
  kind: 'Daily' | 'Weekly' | 'Monthly' | 'Project'
  projectId: string | null
  riskLevel: string
  contentMarkdown: string
  metricsJson: string
  generatedAt: string
  status: 'Active' | 'Archived'
}

/* ── 习惯 ─────────────────────────────────────────────────── */

export interface HabitRoutine {
  id: string
  title: string
  /** 后端按数字序列化，经 habitsApi.list 归一为字符串（见 lib/enums.ts） */
  cadence: HabitCadence
  source: string
  status: string
  /** 习惯描述（PR #356 新增，末位字段） */
  description: string | null
}

/** PUT /calendar/habits/{id} 请求体：部分更新——null/缺省=保持原值 */
export interface UpdateHabitRequest {
  title?: string | null
  description?: string | null
  cadence?: string | null
  status?: string | null
  ruleJson?: string | null
}

/* ── AI 排程占位（工作台） ─────────────────────────────────── */

export interface AiPlanPlaceholder {
  id: string
  title: string
  startsAt: string
  endsAt: string
  reason: string
  status: 'Suggested' | 'PendingConfirmation' | 'Dismissed'
  source: string
  confirmationId: string | null
}

export interface GenerateAiPlanResponse {
  source: 'ai' | 'rule-engine' | 'none'
  placeholders: AiPlanPlaceholder[]
}

/* ── Outlook 状态（工作台状态卡） ──────────────────────────── */

export interface OutlookSettings {
  provider: string
  clientId: string | null
  status: string
  tokenHealth: string
  lastSyncedAt: string | null
  lastError: string | null
  uiStatus: string
}

export interface OutlookSyncBatch {
  id: string
  provider: string
  status: string
  readCount: number
  createdCount: number
  updatedCount: number
  conflictCount: number
  confirmationCount: number
  failureCount: number
  errorSummary: string | null
  startedAt: string
  finishedAt: string | null
  /** 同步模式；写回批次为 writeback */
  mode: string | null
  cancelRequested: boolean
  perCalendarJson: string | null
}

/* ── 数据中心（跨对象治理） ───────────────────────────────── */

export type DataCenterObjectType =
  | 'event'
  | 'task'
  | 'task-segment'
  | 'habit'
  | 'habit-occurrence'
  | 'availability'
  | 'reminder'
  | 'report'
  | 'sync-batch'
  | 'sync-conflict'
  | 'audit-version'

export interface DataCenterItem {
  objectType: DataCenterObjectType
  objectId: string
  title: string
  source: string
  status: string
  startsAt: string | null
  endsAt: string | null
  summary: string
}

export interface DataCenterQueryResponse {
  items: DataCenterItem[]
  page: number
  pageSize: number
  totalCount: number
}

export interface RestorePreviewResponse {
  objectType: string
  objectId: string
  summary: string
  requiresConfirmation: boolean
  changedFields: string[]
  beforeJson: string | null
  afterJson: string | null
}

/* ── Outlook 写回（409/412 冲突语义在 data 内） ───────────── */

export interface OutlookWriteResult {
  status: 'created' | 'updated' | 'deleted' | 'conflict' | 'reauth-required' | 'error'
  event: EventResponse | null
  latestEvent: EventResponse | null
  latestEtag: string | null
  errorCode: string | null
  errorMessage: string | null
}

/* ── 查询参数 ─────────────────────────────────────────────── */

export interface EventsRangeParams {
  start: string
  end: string
}

export interface TasksPagedParams {
  inbox?: boolean
  search?: string
  calendarId?: string
  status?: string
  priority?: number
  plannedFrom?: string
  plannedTo?: string
  dueFrom?: string
  dueTo?: string
  page?: number
  pageSize?: number
}

export type { PagedResult }


/**
 * 任务 → 请求体的全量字段适配（兼容导出；api#465 后服务端更新为三态语义：
 * 缺失字段=保留原值，因此完整替换编辑弹窗之外的调用方无需再用它补齐字段，
 * 直接发送真正要改的字段即可）。旧「必须补齐否则清空」的说法已不成立。
 */
export function taskToMutationData(task: TaskResponse): components['schemas']['UpdateTaskRequest'] {
  return {
    title: task.title,
    description: task.description,
    priority: task.priority,
    due: task.due,
    dtStart: task.dtStart,
    plannedEnd: task.plannedEnd,
    estimatedDuration: task.estimatedDuration,
    minimumSegment: task.minimumSegment,
    // 契约把 status 收紧成四值 union：值域外的历史值不能原样回写（服务端会 400），
    // 归一成「待办」——调用方需在 UI 上显式提示（见 task-editor-dialog 的 legacyStatus）。
    status: normalizeTaskStatus(task.status),
    percentComplete: task.percentComplete,
    taskBookId: task.taskBookId,
    calendarId: task.calendarId,
  }
}
