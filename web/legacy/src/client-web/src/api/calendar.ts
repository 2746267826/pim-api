import { apiGet, apiPost, apiPut, apiDelete, authedFetch } from './client';
import type {
  ApiResponse,
  CalendarDeletePreviewResponse,
  CalendarLayerQueryRequest,
  CalendarLayerResponse,
  CalendarOperationResult,
  CalendarRecycleBinItem,
  CalendarResponse,
  CalendarRestorePreviewResponse,
  AiPlanPlaceholderViewDto,
  GenerateAiPlanRequest,
  GenerateAiPlanResponse,
  AddTaskChecklistItemRequest,
  AuditExportResponse,
  CreateTaskExecutionSegmentRequest,
  CreateDomainProjectRequest,
  CreateHabitRequest,
  CreateReminderRequest,
  CreateTaskBookRequest,
  DataCenterBatchExecutionResponse,
  DataCenterBatchOperationRequest,
  DataCenterBatchPreviewResponse,
  DataCenterQueryRequest,
  DataCenterQueryResponse,
  DomainProject,
  EventResponse,
  GenerateReportRequest,
  HabitRoutine,
  ImportReport,
  OperationConfirmation,
  OutlookAuthorizationSessionResponse,
  OutlookCalendarBindingResponse,
  OutlookSyncBatchPage,
  OutlookSettingsResponse,
  OutlookSyncBatchResponse,
  OutlookSyncRequest,
  OutlookLocalDataPreview,
  OutlookWriteRequest,
  OutlookWriteResult,
  PagedResult,
  ReminderActionResponse,
  ReminderDelivery,
  ReminderSummary,
  ReportArtifact,
  RestorePreviewResponse,
  TaskBook,
  TaskChecklistItem,
  TaskExecutionSegmentResponse,
  TaskResponse,
  UnifiedEventDraft,
  UpdateHabitRequest,
  UpdateOutlookSettingsRequest,
} from '../types';

export type TaskMutationData = {
  calendarId?: string;
  taskBookId?: string;
  title: string;
  description?: string;
  priority: number;
  estimatedDuration?: string;
  minimumSegment?: string;
  percentComplete?: number;
  dtStart?: string;
  plannedEnd?: string;
  due?: string;
  status?: string;
};

export type RecycleBinParams = {
  type?: string;
  search?: string;
  page?: number;
  pageSize?: number;
};

export interface GetTasksParams {
  inbox?: boolean;
  search?: string;
  calendarId?: string;
  status?: string;
  priority?: number;
  plannedFrom?: string;
  plannedTo?: string;
  dueFrom?: string;
  dueTo?: string;
  page?: number;
  pageSize?: number;
}

function appendQuery(path: string, params: Record<string, string | number | boolean | undefined>) {
  const searchParams = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) searchParams.set(key, String(value));
  }

  const qs = searchParams.toString();
  return qs ? `${path}?${qs}` : path;
}

export const calendarApiPaths = {
  recycleBin(params: RecycleBinParams = {}) {
    return appendQuery('/calendar/recycle-bin', {
      type: params.type,
      search: params.search,
      page: params.page,
      pageSize: params.pageSize,
    });
  },
  recycleRestorePreview(type: string, id: string) {
    return `/calendar/recycle-bin/${encodeURIComponent(type)}/${encodeURIComponent(id)}/restore-preview`;
  },
  recycleRestore(type: string, id: string) {
    return `/calendar/recycle-bin/${encodeURIComponent(type)}/${encodeURIComponent(id)}/restore`;
  },
  calendarDeletePreview(id: string) {
    return `/calendar/calendars/${encodeURIComponent(id)}/delete-preview`;
  },
  eventBatchDelete() {
    return '/calendar/events/batch-delete';
  },
  taskPlan(id: string) {
    return `/calendar/tasks/${encodeURIComponent(id)}/plan`;
  },
  taskSegments(id: string) {
    return `/calendar/tasks/${encodeURIComponent(id)}/segments`;
  },
  taskSegment(taskId: string, segmentId: string) {
    return `/calendar/tasks/${encodeURIComponent(taskId)}/segments/${encodeURIComponent(segmentId)}`;
  },
  aiPlaceholders(status?: string) {
    return appendQuery('/calendar/ai-placeholders', { status });
  },
  aiPlaceholdersGenerate() {
    return '/calendar/ai-placeholders/generate';
  },
  aiPlaceholderConfirm(id: string) {
    return `/calendar/ai-placeholders/${encodeURIComponent(id)}/confirm`;
  },
  aiPlaceholderDismiss(id: string) {
    return `/calendar/ai-placeholders/${encodeURIComponent(id)}/dismiss`;
  },
  calendarLayers(params: CalendarLayerQueryRequest) {
    return appendQuery('/calendar/layers', {
      start: params.start,
      end: params.end,
      layers: params.layers?.join(','),
      outlookOnly: params.outlookOnly,
    });
  },
  dataCenterQuery() {
    return '/calendar/data-center/query';
  },
  dataCenterBatchPreview() {
    return '/calendar/data-center/batch/preview';
  },
  dataCenterBatchRequestConfirmation() {
    return '/calendar/data-center/batch/request-confirmation';
  },
  dataCenterBatchExecute() {
    return '/calendar/data-center/batch/execute';
  },
  dataCenterAuditExport() {
    return '/calendar/data-center/audit/export';
  },
  dataCenterRestorePreview() {
    return '/calendar/data-center/restore/preview';
  },
  dataCenterRestoreRequestConfirmation() {
    return '/calendar/data-center/restore/request-confirmation';
  },
  projects() {
    return '/calendar/projects';
  },
  taskBooks() {
    return '/calendar/task-books';
  },
  taskChecklist(id: string) {
    return `/calendar/tasks/${encodeURIComponent(id)}/checklist`;
  },
  taskChecklistItem(taskId: string, itemId: string) {
    return `/calendar/tasks/${encodeURIComponent(taskId)}/checklist/${encodeURIComponent(itemId)}`;
  },
  habits() {
    return '/calendar/habits';
  },
  habit(id: string) {
    return `/calendar/habits/${encodeURIComponent(id)}`;
  },
  habitArchive(id: string) {
    return `/calendar/habits/${encodeURIComponent(id)}/archive`;
  },
  reminders() {
    return '/calendar/reminders';
  },
  reminderSnooze(id: string) {
    return `/calendar/reminders/${encodeURIComponent(id)}/snooze`;
  },
  reminderDismiss(id: string) {
    return `/calendar/reminders/${encodeURIComponent(id)}/dismiss`;
  },
  reminderAction(id: string, action: string) {
    return `/calendar/reminders/${encodeURIComponent(id)}/actions/${encodeURIComponent(action)}`;
  },
  reminderDeliveryLog() {
    return '/calendar/reminders/delivery-log';
  },
  reports() {
    return '/calendar/reports';
  },
  generateReport() {
    return '/calendar/reports/generate';
  },
  report(id: string) {
    return `/calendar/reports/${encodeURIComponent(id)}`;
  },
  archiveReport(id: string) {
    return `/calendar/reports/${encodeURIComponent(id)}/archive`;
  },
  requestReportSuggestionAction(id: string) {
    return `/calendar/reports/suggestions/${encodeURIComponent(id)}/request-action`;
  },
  outlookSettings() {
    return '/calendar/outlook/settings';
  },
  outlookDeviceCode() {
    return '/calendar/outlook/device-code';
  },
  outlookDeviceCodePoll() {
    return '/calendar/outlook/device-code/poll';
  },
  outlookDeviceCodeCancel(sessionId: string) {
    return `/calendar/outlook/device-code/${encodeURIComponent(sessionId)}/cancel`;
  },
  outlookCheck() {
    return '/calendar/outlook/check';
  },
  outlookDiscover() {
    return '/calendar/outlook/calendars/discover';
  },
  outlookCalendars() {
    return '/calendar/outlook/calendars';
  },
  outlookSelection() {
    return '/calendar/outlook/calendars/selection';
  },
  outlookWriteback() {
    return '/calendar/outlook/events/writeback';
  },
  outlookLocalDataPreview() {
    return '/calendar/outlook/local-data/preview';
  },
  outlookLocalData() {
    return '/calendar/outlook/local-data';
  },
  outlookDisconnect() {
    return '/calendar/outlook/disconnect';
  },
  outlookSyncBatches() {
    return '/calendar/outlook/sync/batches';
  },
  outlookSync() {
    return '/calendar/outlook/sync';
  },
  outlookSyncCancel(id: string) {
    return `/calendar/outlook/sync/${encodeURIComponent(id)}/cancel`;
  },
  outlookSyncBatchesPaged(params: { page?: number; pageSize?: number } = {}) {
    return appendQuery('/calendar/outlook/sync/batches', {
      page: params.page,
      pageSize: params.pageSize,
    });
  },
  taskBatchUpdate() {
    return '/calendar/tasks/batch-update';
  },
  taskBatchDelete() {
    return '/calendar/tasks/batch-delete';
  },
};

export async function getCalendars(kind?: string) {
  const qs = kind ? `?kind=${encodeURIComponent(kind)}` : '';
  const r = await apiGet<ApiResponse<CalendarResponse[]>>(`/calendar/calendars${qs}`);
  return r.data;
}

export async function createCalendar(data: { name: string; color?: string; kind?: string }) {
  const r = await apiPost<ApiResponse<CalendarResponse>>('/calendar/calendars', data);
  return r.data;
}

export async function updateCalendar(id: string, data: { name?: string; color?: string }) {
  const r = await apiPut<ApiResponse<CalendarResponse>>(`/calendar/calendars/${id}`, data);
  return r.data;
}

export async function deleteCalendar(id: string) {
  await apiDelete(`/calendar/calendars/${id}`);
}

export async function getEvents(start: string, end: string) {
  // PIM-024: /calendar/events now always returns PagedResult<EventResponse> (commit 775539e).
  // Keep legacy getEvents() returning EventResponse[] for callers like CalendarPage
  // by unwrapping PagedResult.items and falling back to legacy array shape.
  // Use pageSize=100 (backend max) to minimize truncation vs old unbounded List.
  const r = await apiGet<ApiResponse<PagedResult<EventResponse> | EventResponse[]>>(
    `/calendar/events?start=${start}&end=${end}&page=1&pageSize=100`
  );
  const data = r.data as unknown;
  if (Array.isArray(data)) return data as EventResponse[];
  if (data && typeof data === 'object' && Array.isArray((data as PagedResult<EventResponse>).items)) {
    const paged = data as PagedResult<EventResponse>;
    if (typeof paged.totalCount === 'number' && paged.items.length < paged.totalCount) {
      console.warn(`[calendar] getEvents: truncated ${paged.items.length}/${paged.totalCount}, first page only`);
    }
    return paged.items;
  }
  if (data !== null && data !== undefined) {
    console.warn('[calendar] getEvents: unexpected response shape, fallback to []', data);
  }
  return [];
}

export async function createEvent(data: Partial<UnifiedEventDraft>) {
  const r = await apiPost<ApiResponse<EventResponse>>('/calendar/events', data);
  return r.data;
}

export async function updateEvent(id: string, data: Partial<UnifiedEventDraft>, opts?: { scope?: string; recurrenceId?: string; originalEventId?: string }) {
  const params: Record<string, string | undefined> = {};
  if (opts?.scope) params.scope = opts.scope;
  if (opts?.recurrenceId) params.recurrenceId = opts.recurrenceId;
  if (opts?.originalEventId) params.originalEventId = opts.originalEventId;
  const qs = new URLSearchParams(params as Record<string, string>).toString();
  const path = qs ? `/calendar/events/${id}?${qs}` : `/calendar/events/${id}`;
  const r = await apiPut<ApiResponse<EventResponse>>(path, data);
  return r.data;
}

export async function deleteEvent(id: string, opts?: { scope?: string; recurrenceId?: string; originalEventId?: string }) {
  const params: Record<string, string | undefined> = {};
  if (opts?.scope) params.scope = opts.scope;
  if (opts?.recurrenceId) params.recurrenceId = opts.recurrenceId;
  if (opts?.originalEventId) params.originalEventId = opts.originalEventId;
  const qs = new URLSearchParams(params as Record<string, string>).toString();
  const path = qs ? `/calendar/events/${id}?${qs}` : `/calendar/events/${id}`;
  await apiDelete(path);
}

export async function batchDeleteEvents(ids: string[]): Promise<CalendarOperationResult> {
  const r = await apiPost<ApiResponse<CalendarOperationResult>>(calendarApiPaths.eventBatchDelete(), { ids });
  return r.data;
}

export function buildTasksPath(inboxOnly?: boolean) {
  return inboxOnly ? '/calendar/tasks?inbox=true' : '/calendar/tasks';
}

export async function getTasks(inboxOnly?: boolean) {
  const r = await apiGet<ApiResponse<TaskResponse[]>>(
    buildTasksPath(inboxOnly)
  );
  return r.data;
}

export async function getTasksPaged(params: GetTasksParams = {}) {
  const searchParams = new URLSearchParams();
  if (params.inbox !== undefined) searchParams.set('inbox', String(params.inbox));
  if (params.search) searchParams.set('search', params.search);
  if (params.calendarId) searchParams.set('calendarId', params.calendarId);
  if (params.status) searchParams.set('status', params.status);
  if (params.priority !== undefined) searchParams.set('priority', String(params.priority));
  if (params.plannedFrom) searchParams.set('plannedFrom', params.plannedFrom);
  if (params.plannedTo) searchParams.set('plannedTo', params.plannedTo);
  if (params.dueFrom) searchParams.set('dueFrom', params.dueFrom);
  if (params.dueTo) searchParams.set('dueTo', params.dueTo);
  searchParams.set('page', String(params.page ?? 1));
  searchParams.set('pageSize', String(params.pageSize ?? 50));

  const qs = searchParams.toString();
  const r = await apiGet<ApiResponse<PagedResult<TaskResponse>>>(
    qs ? `/calendar/tasks?${qs}` : '/calendar/tasks'
  );
  return r.data;
}

export async function createTask(data: Partial<TaskMutationData>) {
  const r = await apiPost<ApiResponse<TaskResponse>>('/calendar/tasks', data);
  return r.data;
}

export async function updateTask(id: string, data: TaskMutationData) {
  const r = await apiPut<ApiResponse<TaskResponse>>(`/calendar/tasks/${id}`, data);
  return r.data;
}

export async function moveTask(id: string, data: { scheduledStart?: string; duration?: string; newSortOrder?: number }) {
  await apiPost<ApiResponse<string>>(`/calendar/tasks/${id}/move`, data);
}

export function taskToMutationData(task: TaskResponse, overrides: Partial<TaskMutationData> = {}): TaskMutationData {
  return {
    calendarId: task.calendarId,
    taskBookId: task.taskBookId,
    title: task.title,
    description: task.description,
    priority: task.priority,
    estimatedDuration: task.estimatedDuration,
    minimumSegment: task.minimumSegment,
    percentComplete: task.percentComplete,
    dtStart: task.dtStart,
    plannedEnd: task.plannedEnd,
    due: task.due,
    status: task.status,
    ...overrides,
  };
}

export async function deleteTask(id: string) {
  await apiDelete(`/calendar/tasks/${id}`);
}

export async function getRecycleBin(params: RecycleBinParams = {}) {
  const r = await apiGet<ApiResponse<PagedResult<CalendarRecycleBinItem>>>(
    calendarApiPaths.recycleBin(params)
  );
  return r.data;
}

export async function previewRecycleRestore(type: string, id: string) {
  const r = await apiPost<ApiResponse<CalendarRestorePreviewResponse>>(
    calendarApiPaths.recycleRestorePreview(type, id)
  );
  return r.data;
}

export async function restoreRecycleItem(type: string, id: string, restoreAsCopy = false) {
  const r = await apiPost<ApiResponse<CalendarOperationResult>>(
    calendarApiPaths.recycleRestore(type, id),
    { restoreAsCopy }
  );
  return r.data;
}

export async function previewCalendarDelete(id: string) {
  const r = await apiPost<ApiResponse<CalendarDeletePreviewResponse>>(
    calendarApiPaths.calendarDeletePreview(id),
    {}
  );
  return r.data;
}

export async function planTask(
  id: string,
  data: { plannedStart: string; plannedEnd?: string; estimatedDuration?: string }
) {
  const r = await apiPost<ApiResponse<TaskResponse>>(calendarApiPaths.taskPlan(id), data);
  return r.data;
}

export async function listTaskExecutionSegments(taskId: string) {
  const r = await apiGet<ApiResponse<TaskExecutionSegmentResponse[]>>(
    calendarApiPaths.taskSegments(taskId)
  );
  return r.data;
}

export async function createTaskExecutionSegment(
  taskId: string,
  data: CreateTaskExecutionSegmentRequest
) {
  const r = await apiPost<ApiResponse<TaskExecutionSegmentResponse>>(
    calendarApiPaths.taskSegments(taskId),
    data
  );
  return r.data;
}

export async function deleteTaskExecutionSegment(taskId: string, segmentId: string) {
  await apiDelete<ApiResponse<string>>(calendarApiPaths.taskSegment(taskId, segmentId));
}

export async function getCalendarLayers(params: CalendarLayerQueryRequest) {
  const r = await apiGet<ApiResponse<CalendarLayerResponse>>(
    calendarApiPaths.calendarLayers(params)
  );
  return r.data;
}

export async function queryDataCenter(data: DataCenterQueryRequest) {
  const r = await apiPost<ApiResponse<DataCenterQueryResponse>>(
    calendarApiPaths.dataCenterQuery(),
    data
  );
  return r.data;
}

export async function previewDataCenterBatch(data: DataCenterBatchOperationRequest) {
  const r = await apiPost<ApiResponse<DataCenterBatchPreviewResponse>>(
    calendarApiPaths.dataCenterBatchPreview(),
    data
  );
  return r.data;
}

export async function requestDataCenterBatchConfirmation(data: DataCenterBatchOperationRequest) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    calendarApiPaths.dataCenterBatchRequestConfirmation(),
    data
  );
  return r.data;
}

export async function executeDataCenterBatch(confirmationId: string) {
  const r = await apiPost<ApiResponse<DataCenterBatchExecutionResponse>>(
    calendarApiPaths.dataCenterBatchExecute(),
    { confirmationId }
  );
  return r.data;
}

export async function getAuditExport() {
  const r = await apiGet<ApiResponse<AuditExportResponse>>(
    calendarApiPaths.dataCenterAuditExport()
  );
  return r.data;
}

export async function previewDataCenterRestore(auditVersionId: string, reason?: string | null) {
  const r = await apiPost<ApiResponse<RestorePreviewResponse>>(
    calendarApiPaths.dataCenterRestorePreview(),
    { auditVersionId, reason }
  );
  return r.data;
}

export async function requestDataCenterRestoreConfirmation(auditVersionId: string, reason?: string | null) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    calendarApiPaths.dataCenterRestoreRequestConfirmation(),
    { auditVersionId, reason }
  );
  return r.data;
}

export async function getProjects() {
  const r = await apiGet<ApiResponse<DomainProject[]>>(calendarApiPaths.projects());
  return r.data;
}

export async function createProject(data: CreateDomainProjectRequest) {
  const r = await apiPost<ApiResponse<DomainProject>>(calendarApiPaths.projects(), data);
  return r.data;
}

export async function getTaskBooks() {
  const r = await apiGet<ApiResponse<TaskBook[]>>(calendarApiPaths.taskBooks());
  return r.data;
}

export async function createTaskBook(data: CreateTaskBookRequest) {
  const r = await apiPost<ApiResponse<TaskBook>>(calendarApiPaths.taskBooks(), data);
  return r.data;
}

export async function addTaskChecklistItem(taskId: string, data: AddTaskChecklistItemRequest) {
  const r = await apiPost<ApiResponse<TaskChecklistItem>>(
    calendarApiPaths.taskChecklist(taskId),
    data
  );
  return r.data;
}

export async function deleteTaskChecklistItem(taskId: string, itemId: string) {
  await apiDelete(calendarApiPaths.taskChecklistItem(taskId, itemId));
}

export async function updateTaskChecklistItem(
  taskId: string,
  itemId: string,
  data: { title?: string; isDone?: boolean }
) {
  const r = await apiPut<ApiResponse<TaskChecklistItem>>(
    calendarApiPaths.taskChecklistItem(taskId, itemId),
    data
  );
  return r.data;
}

export async function getHabits() {
  const r = await apiGet<ApiResponse<HabitRoutine[]>>(calendarApiPaths.habits());
  return r.data;
}

export async function createHabit(data: CreateHabitRequest) {
  const r = await apiPost<ApiResponse<HabitRoutine>>(calendarApiPaths.habits(), data);
  return r.data;
}

/** #351：编辑习惯。只传需要修改的字段（后端按部分更新语义处理）。 */
export async function updateHabit(id: string, data: UpdateHabitRequest) {
  const r = await apiPut<ApiResponse<HabitRoutine>>(calendarApiPaths.habit(id), data);
  return r.data;
}

/** #351：归档习惯（从活跃列表与日历图层消失，进入「归档」页签）。 */
export async function archiveHabit(id: string) {
  const r = await apiPost<ApiResponse<HabitRoutine>>(calendarApiPaths.habitArchive(id), {});
  return r.data;
}

/** #351：删除习惯（软删除；其历史 occurrence 一并隐藏）。 */
export async function deleteHabit(id: string) {
  const r = await apiDelete<ApiResponse<{ id: string }>>(calendarApiPaths.habit(id));
  return r.data;
}

export async function getReminders() {
  const r = await apiGet<ApiResponse<ReminderSummary[]>>(calendarApiPaths.reminders());
  return r.data;
}

export async function createReminder(data: CreateReminderRequest) {
  const r = await apiPost<ApiResponse<ReminderSummary>>(calendarApiPaths.reminders(), data);
  return r.data;
}

export async function snoozeReminder(id: string, scheduledAt?: string) {
  const suffix = scheduledAt ? `?scheduledAt=${encodeURIComponent(scheduledAt)}` : '';
  const r = await apiPost<ApiResponse<ReminderSummary>>(
    `${calendarApiPaths.reminderSnooze(id)}${suffix}`,
    {}
  );
  return r.data;
}

export async function dismissReminder(id: string) {
  const r = await apiPost<ApiResponse<ReminderSummary>>(calendarApiPaths.reminderDismiss(id), {});
  return r.data;
}

export async function handleReminderAction(id: string, action: string) {
  const r = await apiPost<ApiResponse<ReminderActionResponse>>(
    calendarApiPaths.reminderAction(id, action),
    {}
  );
  return r.data;
}

export async function getReminderDeliveryLog() {
  const r = await apiGet<ApiResponse<ReminderDelivery[]>>(calendarApiPaths.reminderDeliveryLog());
  return r.data;
}

export async function getReports() {
  const r = await apiGet<ApiResponse<ReportArtifact[]>>(calendarApiPaths.reports());
  return r.data;
}

export async function getReport(id: string) {
  const r = await apiGet<ApiResponse<ReportArtifact>>(calendarApiPaths.report(id));
  return r.data;
}

export async function generateReport(data: GenerateReportRequest) {
  const r = await apiPost<ApiResponse<ReportArtifact>>(calendarApiPaths.generateReport(), data);
  return r.data;
}

export async function archiveReport(id: string) {
  const r = await apiPost<ApiResponse<ReportArtifact>>(calendarApiPaths.archiveReport(id), {});
  return r.data;
}

export async function requestReportSuggestionAction(id: string) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    calendarApiPaths.requestReportSuggestionAction(id),
    {}
  );
  return r.data;
}

export async function getOutlookSettings() {
  const r = await apiGet<ApiResponse<OutlookSettingsResponse>>(
    calendarApiPaths.outlookSettings()
  );
  return r.data;
}

export async function updateOutlookSettings(data: UpdateOutlookSettingsRequest) {
  const r = await apiPut<ApiResponse<OutlookSettingsResponse>>(
    calendarApiPaths.outlookSettings(),
    data
  );
  return r.data;
}

export async function createOutlookDeviceCode() {
  const r = await apiPost<ApiResponse<OutlookAuthorizationSessionResponse>>(
    calendarApiPaths.outlookDeviceCode(),
    {}
  );
  return r.data;
}

export async function pollOutlookDeviceCode(sessionId: string) {
  const r = await apiPost<ApiResponse<OutlookAuthorizationSessionResponse>>(
    calendarApiPaths.outlookDeviceCodePoll(),
    { sessionId }
  );
  return r.data;
}

export async function cancelOutlookDeviceCode(sessionId: string) {
  const r = await apiPost<ApiResponse<string>>(
    calendarApiPaths.outlookDeviceCodeCancel(sessionId)
  );
  return r.data;
}

export async function checkOutlookConnection() {
  const r = await apiPost<ApiResponse<OutlookSettingsResponse>>(
    calendarApiPaths.outlookCheck()
  );
  return r.data;
}

export async function runOutlookSync(request: OutlookSyncRequest = { mode: 'normal' }) {
  const r = await apiPost<ApiResponse<OutlookSyncBatchResponse>>(
    calendarApiPaths.outlookSync(),
    request
  );
  return r.data;
}

export async function getOutlookSyncBatchesPaged(params: { page?: number; pageSize?: number } = {}) {
  const r = await apiGet<ApiResponse<OutlookSyncBatchPage>>(
    calendarApiPaths.outlookSyncBatchesPaged(params)
  );
  return r.data;
}

export async function getOutlookSyncBatches() {
  const page = await getOutlookSyncBatchesPaged();
  return page.items;
}

export async function cancelOutlookSync(batchId: string) {
  const r = await apiPost<ApiResponse<string>>(
    calendarApiPaths.outlookSyncCancel(batchId)
  );
  return r.data;
}

export async function outlookDiscover() {
  const r = await apiPost<ApiResponse<OutlookCalendarBindingResponse[]>>(
    calendarApiPaths.outlookDiscover()
  );
  return r.data;
}

export async function outlookBindings() {
  const r = await apiGet<ApiResponse<OutlookCalendarBindingResponse[]>>(
    calendarApiPaths.outlookCalendars()
  );
  return r.data;
}

export async function outlookSelection(selectedBindingIds: string[]) {
  const r = await apiPut<ApiResponse<OutlookCalendarBindingResponse[]>>(
    calendarApiPaths.outlookSelection(),
    { selectedBindingIds }
  );
  return r.data;
}

export async function writeOutlookEvent(request: OutlookWriteRequest) {
  const r = await authedFetch<ApiResponse<OutlookWriteResult>>(
    calendarApiPaths.outlookWriteback(),
    { method: 'POST', body: JSON.stringify(request) },
    [409, 412],
  );
  return r.data;
}

export async function outlookLocalDataPreview() {
  const r = await apiGet<ApiResponse<OutlookLocalDataPreview>>(
    calendarApiPaths.outlookLocalDataPreview()
  );
  return r.data;
}

export async function outlookLocalDataDelete() {
  const r = await apiDelete<ApiResponse<string>>(
    calendarApiPaths.outlookLocalData()
  );
  return r.data;
}

export async function outlookDisconnect() {
  const r = await apiPost<ApiResponse<string>>(
    calendarApiPaths.outlookDisconnect()
  );
  return r.data;
}

export async function batchDeleteTasks(ids: string[]) {
  const r = await apiPost<ApiResponse<CalendarOperationResult>>(
    calendarApiPaths.taskBatchDelete(),
    { ids }
  );
  return r.data;
}

export async function batchUpdateTasks(data: {
  ids: string[];
  status?: string;
  priority?: number;
  calendarId?: string;
}) {
  const r = await apiPost<ApiResponse<CalendarOperationResult>>(
    calendarApiPaths.taskBatchUpdate(),
    data
  );
  return r.data;
}

interface GetEventsParams {
  search?: string;
  calendarId?: string;
  start?: string;
  end?: string;
  page?: number;
  pageSize?: number;
}

export async function getEventsPaged(params: GetEventsParams = {}) {
  const searchParams = new URLSearchParams();
  if (params.search) searchParams.set('search', params.search);
  if (params.calendarId) searchParams.set('calendarId', params.calendarId);
  if (params.start) searchParams.set('start', params.start);
  if (params.end) searchParams.set('end', params.end);
  if (params.page) searchParams.set('page', String(params.page));
  if (params.pageSize) searchParams.set('pageSize', String(params.pageSize));

  const qs = searchParams.toString();
  const r = await apiGet<ApiResponse<PagedResult<EventResponse>>>(
    `/calendar/events?${qs}`
  );
  return r.data;
}

export async function exportIcs(ids?: string[], start?: string, end?: string) {
  const params = new URLSearchParams();
  if (ids?.length) params.set('ids', ids.join(','));
  if (start) params.set('start', start);
  if (end) params.set('end', end);

  const resp = await fetch(`/api/v1/calendar/export-ics?${params.toString()}`, {
    headers: { Authorization: `Bearer ${localStorage.getItem('accessToken')}` }
  });
  if (!resp.ok) throw new Error(`导出失败：${resp.status}`);
  const blob = await resp.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'pim-events.ics';
  a.click();
  URL.revokeObjectURL(url);
}

export async function importIcs(file: File, calendarId?: string) {
  const formData = new FormData();
  formData.append('file', file);
  if (calendarId) formData.append('calendarId', calendarId);

  const resp = await fetch('/api/v1/calendar/import-ics', {
    method: 'POST',
    headers: { Authorization: `Bearer ${localStorage.getItem('accessToken')}` },
    body: formData
  });
  if (!resp.ok) throw new Error(`导入失败：${resp.status}`);
  const json = await resp.json() as ApiResponse<ImportReport>;
  return json.data;
}


export async function getAiPlaceholders(status?: string): Promise<AiPlanPlaceholderViewDto[]> {
  const path = calendarApiPaths.aiPlaceholders(status);
  const r = await apiGet<ApiResponse<AiPlanPlaceholderViewDto[]>>(path);
  return r.data;
}

export async function generateAiPlan(request: GenerateAiPlanRequest = {}): Promise<GenerateAiPlanResponse> {
  const r = await apiPost<ApiResponse<GenerateAiPlanResponse>>(calendarApiPaths.aiPlaceholdersGenerate(), request);
  return r.data;
}

export async function confirmAiPlaceholder(id: string): Promise<OperationConfirmation> {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(calendarApiPaths.aiPlaceholderConfirm(id), {});
  return r.data;
}

export async function dismissAiPlaceholder(id: string): Promise<AiPlanPlaceholderViewDto> {
  const r = await apiPost<ApiResponse<AiPlanPlaceholderViewDto>>(calendarApiPaths.aiPlaceholderDismiss(id), {});
  return r.data;
}
