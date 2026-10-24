import { createContext, lazy, Suspense, useCallback, useContext, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import type { QuickNoteDialogTarget } from '@/features/quick-notes/components/quick-note-dialog'
import { useCalendars, useTaskBooks } from '@/features/calendar/queries'
import type { QuickNoteDetail } from '@/features/quick-notes/types'
import type { EventResponse, TaskResponse } from '@/features/calendar/types'

/*
 * 全局编辑器启动器（01 §5 FAB）：
 * FAB 的「写闪念 / 建任务 / 排日程」直接在当前页打开对应编辑弹窗，而不是跳转页面。
 * 三个弹窗实例挂在 AppShell，任何页面都可调用。
 */

interface GlobalEditorsValue {
  /** 新建闪念：打开空白草稿（保存时才在后端创建，空内容关闭不留记录） */
  openNewQuickNote: (prefill?: string) => void
  /** 打开已有闪念 */
  openQuickNote: (note: QuickNoteDetail) => void
  /** 新建/编辑任务 */
  openTask: (task?: TaskResponse | null) => void
  /** 新建/编辑日程 */
  openEvent: (event?: EventResponse | null, initial?: { start: Date; end: Date } | null) => void
}

/*
 * 三个全局弹窗懒加载：它们只在打开时渲染，但编辑器本体（TipTap 等）很重，
 * 同步引入会把入口 chunk 拖大数百 KB。Suspense fallback=null：未加载时视为未打开。
 */
const QuickNoteDialog = lazy(() => import('@/features/quick-notes/components/quick-note-dialog').then((m) => ({ default: m.QuickNoteDialog })))
const TaskEditorDialog = lazy(() => import('@/features/calendar/components/task-editor-dialog').then((m) => ({ default: m.TaskEditorDialog })))
const EventEditorDialog = lazy(() => import('@/features/calendar/components/event-editor-dialog').then((m) => ({ default: m.EventEditorDialog })))

const GlobalEditorsContext = createContext<GlobalEditorsValue | null>(null)

export function GlobalEditorsProvider({ children }: { children: ReactNode }) {
  const { data: calendars = [] } = useCalendars()
  const { data: taskBooks = [] } = useTaskBooks()

  const [noteTarget, setNoteTarget] = useState<QuickNoteDialogTarget | null>(null)
  const [taskOpen, setTaskOpen] = useState(false)
  const [task, setTask] = useState<TaskResponse | null>(null)
  const [eventState, setEventState] = useState<
    { open: false } | { open: true; event: EventResponse | null; initial: { start: Date; end: Date } | null }
  >({ open: false })

  const openNewQuickNote = useCallback((prefill?: string) => {
    setNoteTarget({ mode: 'draft', initialMarkdown: prefill ?? '', source: 'web-floating' })
  }, [])

  const value = useMemo<GlobalEditorsValue>(
    () => ({
      openNewQuickNote,
      openQuickNote: (n) => setNoteTarget({ mode: 'edit', note: n }),
      openTask: (t) => {
        setTask(t ?? null)
        setTaskOpen(true)
      },
      openEvent: (e, initial) => setEventState({ open: true, event: e ?? null, initial: initial ?? null }),
    }),
    [openNewQuickNote],
  )

  return (
    <GlobalEditorsContext.Provider value={value}>
      {children}

      <Suspense fallback={null}>
        <QuickNoteDialog target={noteTarget} onClose={() => setNoteTarget(null)} />

        <TaskEditorDialog open={taskOpen} onOpenChange={setTaskOpen} taskBooks={taskBooks} task={task} />

        <EventEditorDialog
          open={eventState.open}
          onOpenChange={(o) => !o && setEventState({ open: false })}
          calendars={calendars}
          event={eventState.open ? eventState.event : null}
          initial={eventState.open ? eventState.initial : null}
        />
      </Suspense>
    </GlobalEditorsContext.Provider>
  )
}

export function useGlobalEditors(): GlobalEditorsValue {
  const ctx = useContext(GlobalEditorsContext)
  if (!ctx) throw new Error('useGlobalEditors 必须在 GlobalEditorsProvider 内使用')
  return ctx
}
