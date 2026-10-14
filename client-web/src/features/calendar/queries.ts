import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deferredIntervalMs, standardIntervalMs } from '@/lib/polling'
import { calendarsApi, checklistApi, eventsApi, layersApi, segmentsApi, taskBooksApi, taskTimerApi, tasksApi } from './api'
import type { components } from '@/api/generated'
import type { TaskResponse, TasksPagedParams } from './types'
import { taskToMutationData } from './types'

/* ── queryKey 工厂（CONVENTIONS §4：禁止散写字符串数组） ───── */

export const calendarKeys = {
  calendars: (kind?: string) => ['calendar', 'calendars', kind ?? 'all'] as const,
  taskBooks: ['calendar', 'task-books'] as const,
  events: (start: string, end: string) => ['calendar', 'events', start, end] as const,
  tasks: (params: TasksPagedParams) => ['calendar', 'tasks', 'paged', params] as const,
  tasksAll: ['calendar', 'tasks', 'all'] as const,
  layers: (start: string, end: string, layers: string, outlookOnly: boolean) =>
    ['calendar', 'layers', start, end, layers, outlookOnly] as const,
  segments: (taskId: string) => ['calendar', 'task-segments', taskId] as const,
  timer: (taskId: string) => ['calendar', 'task-timer', taskId] as const,
}

/** 今日页依赖日历数据：变更后一并失效 */
const DEPENDENT_PREFIXES = [['today']] as const

function invalidateCalendar(qc: ReturnType<typeof useQueryClient>, extra: readonly (readonly string[])[] = []) {
  for (const key of [
    calendarKeys.calendars(),
    calendarKeys.taskBooks,
    calendarKeys.tasksAll,
    ['calendar', 'events'],
    ['calendar', 'tasks', 'paged'],
    ['calendar', 'layers'],
    ['calendar', 'task-segments'],
    ['calendar', 'task-timer'],
    ...DEPENDENT_PREFIXES,
    ...extra,
  ]) {
    void qc.invalidateQueries({ queryKey: [...key] })
  }
}

/* ── 查询 hooks ───────────────────────────────────────────── */

export function useCalendars(kind?: 'calendar' | 'task') {
  return useQuery({
    queryKey: calendarKeys.calendars(kind),
    queryFn: () => calendarsApi.list(kind),
  })
}

export function useTaskBooks() {
  return useQuery({
    queryKey: calendarKeys.taskBooks,
    queryFn: taskBooksApi.list,
    staleTime: 60_000,
  })
}

/** 日历网格事件（范围+分页分支；延迟轮询） */
export function useEvents(start: string, end: string, enabled = true) {
  return useQuery({
    queryKey: calendarKeys.events(start, end),
    queryFn: () => eventsApi.listPaged({ start, end, page: 1, pageSize: 100 }),
    enabled,
    refetchInterval: () => deferredIntervalMs(),
  })
}

/*
 * 列表视图（自定义无限下滑）：固定大窗口 [start, end]（约两年）内按页装填，
 * totalCount 到顶即 hasMore=false——「确实没有更多」由 totalCount 证明，
 * 不需要「连续空窗口」这类启发式。注意后端不保证按开始时间排序，由
 * event-list-model 统一排序分组。
 */
export function useEventsInfinite(start: string, end: string, enabled = true) {
  return useInfiniteQuery({
    queryKey: [...calendarKeys.events(start, end), 'infinite'],
    queryFn: ({ pageParam }) => eventsApi.listPaged({ start, end, page: pageParam, pageSize: 100 }),
    initialPageParam: 1,
    getNextPageParam: (last, _all, lastPage) =>
      last.totalCount > lastPage * last.pageSize ? lastPage + 1 : undefined,
    enabled,
  })
}

/** 收件箱侧板（旧版全量分支 + 客户端过滤；标准轮询） */
export function useInboxTasks() {
  return useQuery({
    queryKey: calendarKeys.tasksAll,
    queryFn: () => tasksApi.listAll(),
    refetchInterval: () => standardIntervalMs(),
  })
}

export function useLayers(
  start: string,
  end: string,
  layers: string,
  outlookOnly: boolean,
  enabled = true,
) {
  return useQuery({
    queryKey: calendarKeys.layers(start, end, layers, outlookOnly),
    queryFn: () => layersApi.list({ start, end, layers, outlookOnly }),
    enabled,
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useTaskSegments(taskId: string | null) {
  return useQuery({
    queryKey: calendarKeys.segments(taskId ?? ''),
    queryFn: () => segmentsApi.list(taskId!),
    enabled: taskId != null,
  })
}

/**
 * 任务计时状态（工单 03 D2.1）。正在计时时按秒轮询，让「当前累计时长」自己走；
 * 没有在跑的段时停掉轮询（数据只在动作后变化）。
 */
export function useTaskTimer(taskId: string | null, running: boolean) {
  return useQuery({
    queryKey: calendarKeys.timer(taskId ?? ''),
    queryFn: () => taskTimerApi.state(taskId!),
    enabled: taskId != null,
    refetchInterval: running ? 1_000 : false,
  })
}

export function useTasksPaged(params: TasksPagedParams) {
  return useQuery({
    queryKey: calendarKeys.tasks(params),
    queryFn: () => tasksApi.listPaged(params),
  })
}

/* ── 变更 hooks ───────────────────────────────────────────── */

export function useCreateCalendar() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: calendarsApi.create,
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useRenameCalendar() {
  const qc = useQueryClient()
  return useMutation({
    /* 契约 CreateCalendarRequest.name 必填（minLength 1）；调用方（日历本重命名）已保证非空 */
    mutationFn: ({ id, body }: { id: string; body: components['schemas']['CreateCalendarRequest'] }) =>
      calendarsApi.rename(id, body),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useDeleteCalendar() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id }: { id: string }) => calendarsApi.remove(id),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useSaveEvent() {
  const qc = useQueryClient()
  return useMutation({
    /* 请求体用契约类型而非 Record<string, unknown>：编译器才能校验必填集
     * （Create/Update 的 isAllDay 可空性不同，Create 可赋给 Update，故取 Create）。 */
    mutationFn: async (input: {
      id?: string
      body: components['schemas']['CreateEventRequest']
      scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string }
    }) => (input.id ? eventsApi.update(input.id, input.body, input.scope) : eventsApi.create(input.body)),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useDeleteEvent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      id: string
      scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string }
    }) => eventsApi.remove(input.id, input.scope),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useSaveTask() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (input: {
      id?: string
      body: components['schemas']['CreateTaskRequest'] | components['schemas']['UpdateTaskRequest']
    }) => (input.id ? tasksApi.update(input.id, input.body) : tasksApi.create(input.body)),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useDeleteTask() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => tasksApi.remove(id),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useBatchDeleteTasks() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (ids: string[]) => tasksApi.batchDelete(ids),
    onSuccess: () => invalidateCalendar(qc),
  })
}

/** 拖放排期：成功后任务与图层全部失效 */
export function usePlanTask() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({
      id,
      body,
    }: {
      id: string
      body: { plannedStart: string; plannedEnd?: string; estimatedDuration?: string }
    }) => tasksApi.plan(id, body),
    onSuccess: () => invalidateCalendar(qc),
  })
}

/**
 * 完成状态切换：PUT /calendar/tasks/{id} 是**整体替换式**语义
 * （title/description/priority/due/时长/dtStart 均按请求写入），
 * 因此必须先经 taskToMutationData 把当前值补齐再提交，否则字段会被清空。
 */
export function useToggleTaskComplete() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ task, completed }: { task: TaskResponse; completed: boolean }) =>
      tasksApi.update(task.id, {
        ...taskToMutationData(task),
        status: completed ? 'COMPLETED' : 'NEEDS-ACTION',
        percentComplete: completed ? 100 : 0,
      }),
    onSuccess: () => invalidateCalendar(qc),
  })
}

export function useChecklistMutations() {
  const qc = useQueryClient()
  const invalidate = (taskId: string) => void qc.invalidateQueries({ queryKey: calendarKeys.segments(taskId) })
  return {
    add: useMutation({
      mutationFn: ({ taskId, body }: { taskId: string; body: { title: string; sortOrder?: number } }) =>
        checklistApi.add(taskId, body),
      onSuccess: (_d, v) => invalidate(v.taskId),
    }),
    update: useMutation({
      mutationFn: ({ taskId, itemId, body }: { taskId: string; itemId: string; body: { title?: string; isDone?: boolean } }) =>
        checklistApi.update(taskId, itemId, body),
      onSuccess: (_d, v) => invalidate(v.taskId),
    }),
    remove: useMutation({
      mutationFn: ({ taskId, itemId }: { taskId: string; itemId: string }) =>
        checklistApi.remove(taskId, itemId),
      onSuccess: (_d, v) => invalidate(v.taskId),
    }),
  }
}

export function useAddSegment() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({
      taskId,
      body,
    }: {
      taskId: string
      body: { startsAt: string; endsAt: string; status: string; source: string }
    }) => segmentsApi.add(taskId, body),
    onSuccess: () => invalidateCalendar(qc),
  })
}

/** 删除手工计划的时间段（工单 03 D2.2 补齐的 DELETE）。正在计时的段服务端会拒绝删除。 */
export function useDeleteSegment() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ taskId, segmentId }: { taskId: string; segmentId: string }) =>
      segmentsApi.remove(taskId, segmentId),
    onSuccess: () => invalidateCalendar(qc),
  })
}

/**
 * 计时动作（开始 / 暂停 / 结束）。
 * 计时段落落库后任务状态会跟着变成 IN-PROCESS，所以三个动作都做整片日历失效，
 * 让任务行上的状态徽标与列表同步刷新。
 */
export function useTaskTimerActions() {
  const qc = useQueryClient()
  const invalidate = () => invalidateCalendar(qc)
  return {
    start: useMutation({ mutationFn: (taskId: string) => taskTimerApi.start(taskId), onSuccess: invalidate }),
    pause: useMutation({ mutationFn: (taskId: string) => taskTimerApi.pause(taskId), onSuccess: invalidate }),
    stop: useMutation({ mutationFn: (taskId: string) => taskTimerApi.stop(taskId), onSuccess: invalidate }),
  }
}

/* ── P2：提醒 / 报告 / 习惯 / AI 建议 / Outlook / 数据中心 ── */

export const calendarExtraKeys = {
  reminders: ['calendar', 'reminders'] as const,
  deliveryLog: ['calendar', 'reminders', 'delivery-log'] as const,
  reports: ['calendar', 'reports'] as const,
  habits: ['calendar', 'habits'] as const,
  aiSuggestions: ['calendar', 'ai-placeholders', 'suggested'] as const,
  outlookSettings: ['calendar', 'outlook', 'settings'] as const,
  outlookBatches: ['calendar', 'outlook', 'batches'] as const,
  dataCenter: (params: Record<string, unknown>) => ['calendar', 'data-center', params] as const,
}

export function useReminders() {
  return useQuery({
    queryKey: calendarExtraKeys.reminders,
    queryFn: remindersApiList,
    refetchInterval: () => standardIntervalMs(),
  })
}

function remindersApiList() {
  return import('./api').then((m) => m.remindersApi.list())
}

export function useDeliveryLog(enabled: boolean) {
  return useQuery({
    queryKey: calendarExtraKeys.deliveryLog,
    queryFn: () => import('./api').then((m) => m.remindersApi.deliveryLog()),
    enabled,
  })
}

export function useReports() {
  return useQuery({ queryKey: calendarExtraKeys.reports, queryFn: () => import('./api').then((m) => m.reportsApi.list()) })
}

export function useHabits() {
  return useQuery({ queryKey: calendarExtraKeys.habits, queryFn: () => import('./api').then((m) => m.habitsApi.list()) })
}

export function useAiSuggestions() {
  return useQuery({
    queryKey: calendarExtraKeys.aiSuggestions,
    queryFn: () => import('./api').then((m) => m.aiPlaceholdersApi.list()),
    refetchInterval: () => standardIntervalMs(),
  })
}

export function useOutlookSettings() {
  return useQuery({
    queryKey: calendarExtraKeys.outlookSettings,
    queryFn: () => import('./api').then((m) => m.outlookApi.settings()),
    refetchInterval: () => standardIntervalMs(),
    meta: { silent: true },
  })
}

export function useAiActions() {
  const qc = useQueryClient()
  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ['calendar', 'ai-placeholders'] })
    void qc.invalidateQueries({ queryKey: ['today'] })
    void qc.invalidateQueries({ queryKey: ['operations', 'confirmations'] })
  }
  return {
    generate: useMutation({
      mutationFn: (horizonDays: number) => import('./api').then((m) => m.aiPlaceholdersApi.generate(horizonDays)),
      onSuccess: invalidate,
    }),
    confirm: useMutation({
      mutationFn: (id: string) => import('./api').then((m) => m.aiPlaceholdersApi.confirm(id)),
      onSuccess: invalidate,
    }),
    dismiss: useMutation({
      mutationFn: (id: string) => import('./api').then((m) => m.aiPlaceholdersApi.dismiss(id)),
      onSuccess: invalidate,
    }),
  }
}

export function useReminderActions() {
  const qc = useQueryClient()
  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ['calendar', 'reminders'] })
    void qc.invalidateQueries({ queryKey: ['today'] })
  }
  return {
    snooze: useMutation({
      mutationFn: ({ id, at }: { id: string; at?: string }) => import('./api').then((m) => m.remindersApi.snooze(id, at)),
      onSuccess: invalidate,
    }),
    dismiss: useMutation({
      mutationFn: (id: string) => import('./api').then((m) => m.remindersApi.dismiss(id)),
      onSuccess: invalidate,
    }),
    action: useMutation({
      mutationFn: ({ id, action }: { id: string; action: string }) =>
        import('./api').then((m) => m.remindersApi.action(id, action)),
      onSuccess: invalidate,
    }),
  }
}

export function useCreateHabit() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { title: string; cadence?: string }) => import('./api').then((m) => m.habitsApi.create(body)),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['calendar', 'habits'] }),
  })
}

/*
 * 习惯管理（PR #356）：编辑 / 归档 / 删除。
 * 归档与删除都会让该习惯的 occurrence 从日历 habits 图层与今日分区消失，
 * 因此除 habits 列表外一并失效图层与今日聚合。
 */
export function useUpdateHabit() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: import('./types').UpdateHabitRequest }) =>
      import('./api').then((m) => m.habitsApi.update(id, body)),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['calendar', 'habits'] })
      void qc.invalidateQueries({ queryKey: ['calendar', 'layers'] })
      void qc.invalidateQueries({ queryKey: ['today'] })
    },
  })
}

export function useArchiveHabit() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => import('./api').then((m) => m.habitsApi.archive(id)),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['calendar', 'habits'] })
      void qc.invalidateQueries({ queryKey: ['calendar', 'layers'] })
      void qc.invalidateQueries({ queryKey: ['today'] })
    },
  })
}

export function useDeleteHabit() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => import('./api').then((m) => m.habitsApi.remove(id)),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['calendar', 'habits'] })
      void qc.invalidateQueries({ queryKey: ['calendar', 'layers'] })
      void qc.invalidateQueries({ queryKey: ['today'] })
    },
  })
}

export function useGenerateReport() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { kind: string; date: string }) => import('./api').then((m) => m.reportsApi.generate(body)),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['calendar', 'reports'] }),
  })
}

export function useDataCenterQuery(params: components['schemas']['DataCenterQueryRequest']) {
  return useQuery({
    queryKey: calendarExtraKeys.dataCenter(params),
    queryFn: () => import('./api').then((m) => m.dataCenterApi.query(params)),
    placeholderData: (prev) => prev,
  })
}
