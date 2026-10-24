import { useMemo, useState } from 'react'
import { CalendarPlus, SquareCheckBig, Wand2 } from 'lucide-react'
import { scheduleApi, tasksApi } from '../api'
import { useInboxTasks, usePlanTask } from '../queries'
import { TASK_STATUS } from '../types'
import type { TaskResponse } from '../types'
import { formatDurationC, formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, EmptyState, Spinner } from '@/components/ui'
import { cn } from '@/lib/utils'

/** 收件箱/未排期判定（规格：客户端过滤） */
export function isInboxOrUnplanned(t: TaskResponse): boolean {
  return t.status !== TASK_STATUS.completed && (t.isInbox || !t.dtStart)
}

const PRIORITY_COLOR: Record<number, string> = {
  9: 'bg-crit',
  5: 'bg-warn',
  1: 'bg-neutral',
}

export interface InboxPanelProps {
  onNewTask: () => void
  onNewEvent: () => void
  className?: string
}

/**
 * 收件箱侧板（规格 01 §5）：未排期任务卡片列表；
 * 列表项为拖拽源——拖到日历时间槽触发排期（自动按时长估算）。
 */
export function InboxPanel({ onNewTask, onNewEvent, className }: InboxPanelProps) {
  const { data, isLoading } = useInboxTasks()
  const plan = usePlanTask()
  const [rescheduling, setRescheduling] = useState(false)

  const items = useMemo(() => (data ?? []).filter(isInboxOrUnplanned), [data])

  async function rescheduleAll() {
    const candidates = items.filter((t) => t.estimatedDuration)
    if (candidates.length === 0) {
      notifyError('没有可排期的任务：收件箱任务需要先设置预计时长')
      return
    }
    setRescheduling(true)
    try {
      const solutions = await scheduleApi.run(candidates.map((t) => t.id))
      // 响应为多算法方案数组：取首个含槽位的方案（greedy 优先）
      const solution = solutions.find((sol) => sol.slots.length > 0) ?? solutions[0]
      const slots = solution?.slots ?? []
      if (slots.length === 0) {
        notifyError('排程引擎未给出可用时间槽')
        return
      }
      for (const slot of slots) {
        await tasksApi.plan(slot.taskId, { plannedStart: slot.start, plannedEnd: slot.end })
      }
      notifySuccess(`已重排 ${slots.length} 个任务（${solution.algorithmName}）`)
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '一键重排失败')
    } finally {
      setRescheduling(false)
    }
  }

  return (
    <div className={cn('flex h-full flex-col', className)}>
      <div className="flex shrink-0 items-center justify-between gap-2 border-b border-divider px-4 py-3">
        <div className="text-sm font-semibold text-text-1">
          收件箱 <span className="tnum text-text-3">({items.length})</span>
        </div>
        <div className="flex items-center gap-1.5">
          <Button
            variant="ghost"
            size="sm"
            title="一键重排：按预计时长自动分配时间槽"
            loading={rescheduling}
            onClick={() => void rescheduleAll()}
          >
            <Wand2 className="size-4" aria-hidden /> 重排
          </Button>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto p-2">
        {isLoading ? (
          <div className="grid place-items-center py-10">
            <Spinner className="size-5" />
          </div>
        ) : items.length === 0 ? (
          <EmptyState
            size="sm"
            title="收件箱是空的"
            description="没有未排期的任务。拖动任务卡片到日历即可排期。"
          />
        ) : (
          <ul className="space-y-1">
            {items.map((task) => (
              <li
                key={task.id}
                data-task-id={task.id}
                data-task-title={task.title}
                data-task-duration={task.estimatedDuration ?? '01:00:00'}
                title="拖到日历时间槽排期"
                className="fc-external-drag cursor-grab rounded-ctl border border-transparent px-2 py-1.5 transition-colors select-none hover:border-border hover:bg-surface active:cursor-grabbing"
              >
                <div className="flex items-center gap-2">
                  <span
                    className={cn('size-1.5 shrink-0 rounded-full', PRIORITY_COLOR[task.priority] ?? 'bg-neutral')}
                    aria-hidden
                  />
                  <span className="min-w-0 flex-1 truncate text-[13px] text-text-1">{task.title}</span>
                  {task.due && (
                    <span className="tnum shrink-0 text-[11px] text-warn">≤ {formatTime(task.due)}</span>
                  )}
                </div>
                <div className="mt-0.5 pl-3.5 text-[11px] text-text-4">
                  {task.estimatedDuration
                    ? `预计 ${formatDurationC(task.estimatedDuration)}`
                    : '未设预计时长'}
                  {task.isInbox ? ' · 收件箱' : ''}
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="grid shrink-0 grid-cols-2 gap-2 border-t border-divider p-2">
        <Button variant="secondary" size="sm" onClick={onNewTask}>
          <SquareCheckBig className="size-4" aria-hidden /> 新建任务
        </Button>
        <Button variant="secondary" size="sm" onClick={onNewEvent}>
          <CalendarPlus className="size-4" aria-hidden /> 新建日程
        </Button>
      </div>

      {/* 拖放排期失败的静默重试交给用户；plan mutation 的错误经全局 toast 上浮 */}
      {plan.isError && (
        <div className="shrink-0 px-3 py-2 text-xs text-crit">排期失败，请重试。</div>
      )}
    </div>
  )
}
