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
}

/** Outlook 镜像事件（source 以 outlook 开头）走写回通道而非直接 PUT/DELETE */
export function isOutlookEvent(e: Pick<EventResponse, 'source'>): boolean {
  return e.source.startsWith('outlook')
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

export const TASK_STATUS = {
  needsAction: 'NEEDS-ACTION',
  completed: 'COMPLETED',
} as const

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
  endsAt: string
  status: string
  source: string
  planningReason: string | null
  confirmationId: string | null
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
 * 任务 → PUT/POST 请求体（整体替换式端点的必备适配，规格 calendar.md:379-391）。
 * PUT /calendar/tasks/{id} 会把 title/description/priority/due/时长/dtStart 全部按请求写入，
 * 任何部分更新（如仅切换完成状态）都必须先补齐当前值，否则字段被清空。
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
    status: task.status,
    percentComplete: task.percentComplete,
    taskBookId: task.taskBookId,
    calendarId: task.calendarId,
  }
}
