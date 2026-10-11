import { apiDelete, apiGet, apiPost, apiPut } from '@/api/client'
import type { components } from '@/api/generated'
import { normalizeHabitCadence } from '@/lib/enums'
import type { PagedResult } from '@/api/types'
import type {
  CalendarBook,
  CalendarDeletePreview,
  CalendarOperationResult,
  EventResponse,
  LayerItem,
  TaskBook,
  TaskChecklistItem,
  TaskExecutionSegment,
  TaskResponse,
  TasksPagedParams,
} from './types'

/* ── 日历本 / 任务本 ─────────────────────────────────────── */

export const calendarsApi = {
  list: (kind?: 'calendar' | 'task') =>
    apiGet<CalendarBook[]>(`/api/v1/calendar/calendars${kind ? `?kind=${kind}` : ''}`),
  create: (body: components['schemas']['CreateCalendarRequest']) =>
    apiPost<CalendarBook>('/api/v1/calendar/calendars', body),
  rename: (id: string, body: components['schemas']['CreateCalendarRequest']) =>
    apiPut<CalendarBook>(`/api/v1/calendar/calendars/${id}`, body),
  deletePreview: (id: string) =>
    apiPost<CalendarDeletePreview>(`/api/v1/calendar/calendars/${id}/delete-preview`, {}),
  remove: (id: string) => apiDelete<CalendarOperationResult>(`/api/v1/calendar/calendars/${id}`),
}

export const taskBooksApi = {
  list: () => apiGet<TaskBook[]>('/api/v1/calendar/task-books'),
}

/* ── 日程事件 ─────────────────────────────────────────────── */
/* 双态端点：固定带 page/pageSize 命中 PagedResult 分支（02 §日历网格） */

export const eventsApi = {
  listPaged: (params: { start: string; end: string; page?: number; pageSize?: number }) => {
    const q = new URLSearchParams({
      start: params.start,
      end: params.end,
      page: String(params.page ?? 1),
      pageSize: String(params.pageSize ?? 100),
    })
    return apiGet<PagedResult<EventResponse>>(`/api/v1/calendar/events?${q}`)
  },
  create: (body: components['schemas']['CreateEventRequest']) =>
    apiPost<EventResponse>('/api/v1/calendar/events', body),
  update: (
    id: string,
    body: components['schemas']['UpdateEventRequest'],
    scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string },
  ) => {
    const q = new URLSearchParams()
    if (scope?.scope) q.set('scope', scope.scope)
    if (scope?.recurrenceId) q.set('recurrenceId', scope.recurrenceId)
    if (scope?.originalEventId) q.set('originalEventId', scope.originalEventId)
    const suffix = q.toString() ? `?${q}` : ''
    return apiPut<EventResponse>(`/api/v1/calendar/events/${id}${suffix}`, body)
  },
  remove: (
    id: string,
    scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string },
  ) => {
    const q = new URLSearchParams()
    if (scope?.scope) q.set('scope', scope.scope)
    if (scope?.recurrenceId) q.set('recurrenceId', scope.recurrenceId)
    if (scope?.originalEventId) q.set('originalEventId', scope.originalEventId)
    const suffix = q.toString() ? `?${q}` : ''
    return apiDelete<string>(`/api/v1/calendar/events/${id}${suffix}`)
  },
  batchDelete: (ids: string[]) =>
    apiPost<CalendarOperationResult>('/api/v1/calendar/events/batch-delete', { ids }),
}

/* ── 任务 ─────────────────────────────────────────────────── */
/* 双态端点：带筛选/分页参数 → PagedResult；全缺省 → 旧版全量数组 */

function buildTasksQuery(params: TasksPagedParams): string {
  const q = new URLSearchParams()
  if (params.inbox !== undefined) q.set('inbox', String(params.inbox))
  if (params.search) q.set('search', params.search)
  if (params.calendarId) q.set('calendarId', params.calendarId)
  if (params.status) q.set('status', params.status)
  if (params.priority !== undefined) q.set('priority', String(params.priority))
  if (params.plannedFrom) q.set('plannedFrom', params.plannedFrom)
  if (params.plannedTo) q.set('plannedTo', params.plannedTo)
  if (params.dueFrom) q.set('dueFrom', params.dueFrom)
  if (params.dueTo) q.set('dueTo', params.dueTo)
  if (params.page) q.set('page', String(params.page))
  if (params.pageSize) q.set('pageSize', String(params.pageSize))
  return q.toString()
}

export const tasksApi = {
  listPaged: (params: TasksPagedParams) => {
    const q = buildTasksQuery(params)
    return apiGet<PagedResult<TaskResponse>>(`/api/v1/calendar/tasks${q ? `?${q}` : ''}`)
  },
  /** 旧版全量（收件箱侧板用全量+客户端过滤） */
  listAll: (params: { inbox?: boolean } = {}) => {
    const q = params.inbox !== undefined ? `?inbox=${params.inbox}` : ''
    return apiGet<TaskResponse[]>(`/api/v1/calendar/tasks${q}`)
  },
  create: (body: components['schemas']['CreateTaskRequest']) =>
    apiPost<TaskResponse>('/api/v1/calendar/tasks', body),
  update: (id: string, body: components['schemas']['UpdateTaskRequest']) =>
    apiPut<TaskResponse>(`/api/v1/calendar/tasks/${id}`, body),
  remove: (id: string) =>
    apiDelete<CalendarOperationResult>(`/api/v1/calendar/tasks/${id}`),
  batchDelete: (ids: string[]) =>
    apiPost<CalendarOperationResult>('/api/v1/calendar/tasks/batch-delete', { ids }),
  /** 拖放排期（收件箱任务拖到日历时间槽） */
  plan: (id: string, body: components['schemas']['PlanTaskRequest']) =>
    apiPost<TaskResponse>(`/api/v1/calendar/tasks/${id}/plan`, body),
}

/* ── 任务清单 / 时间段 ────────────────────────────────────── */

export const checklistApi = {
  add: (taskId: string, body: components['schemas']['AddTaskChecklistItemRequest']) =>
    apiPost<TaskChecklistItem>(`/api/v1/calendar/tasks/${taskId}/checklist`, body),
  update: (taskId: string, itemId: string, body: components['schemas']['UpdateTaskChecklistItemRequest']) =>
    apiPut<TaskChecklistItem>(`/api/v1/calendar/tasks/${taskId}/checklist/${itemId}`, body),
  remove: (taskId: string, itemId: string) =>
    apiDelete<string>(`/api/v1/calendar/tasks/${taskId}/checklist/${itemId}`),
}

export const segmentsApi = {
  list: (taskId: string) =>
    apiGet<TaskExecutionSegment[]>(`/api/v1/calendar/tasks/${taskId}/segments`),
  add: (taskId: string, body: components['schemas']['CreateTaskExecutionSegmentRequest']) =>
    apiPost<TaskExecutionSegment>(`/api/v1/calendar/tasks/${taskId}/segments`, body),
}

/* ── 图层（工作台/日历统一查询） ──────────────────────────── */

export const layersApi = {
  list: (params: {
    start: string
    end: string
    layers?: string
    outlookOnly?: boolean
  }) => {
    const q = new URLSearchParams({ start: params.start, end: params.end })
    if (params.layers) q.set('layers', params.layers)
    if (params.outlookOnly) q.set('outlookOnly', 'true')
    return apiGet<{ start: string; end: string; items: LayerItem[] }>(
      `/api/v1/calendar/layers?${q}`,
    )
  },
}

/* ── 排程引擎（收件箱"一键重排"：纯内存计算，返回建议槽位） ── */

export interface ScheduleSlot {
  taskId: string
  title: string
  start: string
  end: string
}

export const scheduleApi = {
  /** 排程引擎：响应 data 是 ScheduleSolution[]（数组，非对象） */
  run: (taskIds: string[]) =>
    apiPost<{ algorithmName: string; slots: ScheduleSlot[]; metrics: Record<string, unknown> }[]>(
      '/api/v1/calendar/schedule',
      { taskIds },
    ),
}

/* ── 提醒 ─────────────────────────────────────────────────── */

export const remindersApi = {
  list: () => apiGet<import('./types').ReminderResponse[]>('/api/v1/calendar/reminders'),
  snooze: (id: string, scheduledAt?: string) =>
    apiPost<import('./types').ReminderResponse>(
      `/api/v1/calendar/reminders/${id}/snooze${scheduledAt ? `?scheduledAt=${encodeURIComponent(scheduledAt)}` : ''}`,
      {},
    ),
  dismiss: (id: string) =>
    apiPost<import('./types').ReminderResponse>(`/api/v1/calendar/reminders/${id}/dismiss`, {}),
  action: (id: string, action: string) =>
    apiPost<import('./types').ReminderActionResponse>(`/api/v1/calendar/reminders/${id}/actions/${action}`, {}),
  deliveryLog: () => apiGet<import('./types').ReminderDelivery[]>('/api/v1/calendar/reminders/delivery-log'),
}

/* ── 报告 ─────────────────────────────────────────────────── */

export const reportsApi = {
  list: () => apiGet<import('./types').ReportArtifact[]>('/api/v1/calendar/reports'),
  generate: (body: components['schemas']['GenerateReportRequest']) =>
    apiPost<import('./types').ReportArtifact>('/api/v1/calendar/reports/generate', body),
  requestSuggestionAction: (suggestionId: string) =>
    apiPost<unknown>(`/api/v1/calendar/reports/suggestions/${suggestionId}/request-action`, {}),
}

/* ── 习惯 ─────────────────────────────────────────────────── */

export const habitsApi = {
  list: async () => {
    const list = await apiGet<import('./types').HabitRoutine[]>('/api/v1/calendar/habits')
    // 后端 HabitCadence 按数字序列化（Daily=0/Weekly=1/Monthly=2/Custom=3），归一为字符串
    return list.map((h) => ({ ...h, cadence: normalizeHabitCadence(h.cadence) }))
  },
  get: async (id: string) => {
    const h = await apiGet<import('./types').HabitRoutine>(`/api/v1/calendar/habits/${id}`)
    return { ...h, cadence: normalizeHabitCadence(h.cadence) }
  },
  create: (body: components['schemas']['CreateHabitRequest']) =>
    apiPost<import('./types').HabitRoutine>('/api/v1/calendar/habits', body),
  /** 部分更新语义：null/缺省字段保持原值；description 空串=清空；ruleJson 空白=落 "{}" */
  update: (id: string, body: components['schemas']['UpdateHabitRequest']) =>
    apiPut<import('./types').HabitRoutine>(`/api/v1/calendar/habits/${id}`, body),
  /** 软删除（幂等）：历史 occurrence 一并软删，日历图层 habits 同步消失 */
  remove: (id: string) => apiDelete<{ id: string }>(`/api/v1/calendar/habits/${id}`),
  /** 归档：只改 status=Archived，历史 occurrence 保留可审计 */
  archive: (id: string) => apiPost<import('./types').HabitRoutine>(`/api/v1/calendar/habits/${id}/archive`, {}),
}

/* ── AI 排程建议（工作台） ─────────────────────────────────── */

export const aiPlaceholdersApi = {
  list: (status = 'Suggested') =>
    apiGet<import('./types').AiPlanPlaceholder[]>(`/api/v1/calendar/ai-placeholders?status=${status}`),
  generate: (horizonDays: number) =>
    apiPost<import('./types').GenerateAiPlanResponse>('/api/v1/calendar/ai-placeholders/generate', { horizonDays }),
  confirm: (id: string) => apiPost<unknown>(`/api/v1/calendar/ai-placeholders/${id}/confirm`, {}),
  dismiss: (id: string) =>
    apiPost<import('./types').AiPlanPlaceholder>(`/api/v1/calendar/ai-placeholders/${id}/dismiss`, {}),
}

/* ── Outlook 状态（工作台状态卡） ──────────────────────────── */

export const outlookApi = {
  settings: () => apiGet<import('./types').OutlookSettings>('/api/v1/calendar/outlook/settings'),
  batches: (page = 1, pageSize = 20) =>
    apiGet<{ items: import('./types').OutlookSyncBatch[]; total: number; page: number; pageSize: number }>(
      `/api/v1/calendar/outlook/sync/batches?page=${page}&pageSize=${pageSize}`,
    ),
}

/* ── 数据中心（跨对象治理） ───────────────────────────────── */

export const dataCenterApi = {
  query: (body: components['schemas']['DataCenterQueryRequest']) =>
    apiPost<import('./types').DataCenterQueryResponse>('/api/v1/calendar/data-center/query', body),
  auditExport: (params?: { start?: string; end?: string }) => {
    const q = new URLSearchParams()
    if (params?.start) q.set('start', params.start)
    if (params?.end) q.set('end', params.end)
    return apiGet<{ fileName: string; contentType: string; content: string }>(
      `/api/v1/calendar/data-center/audit/export${q.toString() ? `?${q}` : ''}`,
    )
  },
  restorePreview: (body: components['schemas']['DataCenterRestoreRequest']) =>
    apiPost<import('./types').RestorePreviewResponse>('/api/v1/calendar/data-center/restore/preview', body),
  batchPreview: (body: components['schemas']['DataCenterBatchOperationRequest']) =>
    apiPost<{
      riskLevel: string
      requiresStrictConfirmation: boolean
      summary: string
      affectedObjectTypes: string[]
      affectedCount: number
    }>('/api/v1/calendar/data-center/batch/preview', body),
}
