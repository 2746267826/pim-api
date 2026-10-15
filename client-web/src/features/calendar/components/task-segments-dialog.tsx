import { useEffect, useState } from 'react'
import { Pause, Play, Square, Trash2 } from 'lucide-react'
import { useAddSegment, useDeleteSegment, useTaskSegments, useTaskTimer, useTaskTimerActions } from '../queries'
import { TASK_SEGMENT_STATUS, TASK_SEGMENT_STATUS_LABEL, TASK_STATUS_LABEL } from '../types'
import { formatRange, fromLocalInputValue, toLocalInputValue } from '@/lib/datetime'
import { closedSegmentSeconds, currentRunSeconds, formatClock, runningSegment } from '../timer'
import { Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, EmptyState, InlineAlert, Input, Label, StatusBadge } from '@/components/ui'

export interface TaskSegmentsDialogProps {
  taskId: string | null
  taskTitle: string
  onClose: () => void
}

/**
 * 任务计时面板（工单 03 D2.2）。
 *
 * - 开始 / 暂停 / 结束 三个动作直接打到计时段落接口，落库的是真段；
 * - 「当前累计时长」取服务端 `GET /tasks/{id}/timer`（秒级刷新），与服务端口径一致；
 * - 手工添加的时间段仍然是 `planned`（计划块，不计入实际时长），但经共享常量写入，
 *   不再在计时路径上硬编码 `planned`（旧实现无论怎么点都只写 planned）。
 */
export function TaskSegmentsDialog({ taskId, taskTitle, onClose }: TaskSegmentsDialogProps) {
  const { data: segments = [], isLoading } = useTaskSegments(taskId)
  const running = runningSegment(segments)
  const { data: timer } = useTaskTimer(taskId, running != null)
  const actions = useTaskTimerActions()
  const add = useAddSegment()
  const remove = useDeleteSegment()
  const [startsAt, setStartsAt] = useState('')
  const [endsAt, setEndsAt] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setStartsAt('')
    setEndsAt('')
    setError(null)
  }, [taskId])

  async function act(run: () => Promise<unknown>) {
    setError(null)
    try {
      await run()
    } catch (err) {
      setError(err instanceof Error ? err.message : '操作失败')
    }
  }

  async function submit() {
    if (!taskId) return
    const s = fromLocalInputValue(startsAt)
    const e = fromLocalInputValue(endsAt)
    if (!s || !e || e <= s) {
      setError('请填写有效的时间段（结束须晚于开始）')
      return
    }
    setError(null)
    try {
      await add.mutateAsync({
        taskId,
        body: {
          startsAt: s,
          endsAt: e,
          // 手工添加的是「计划块」，不是执行记录：planned 不计入实际执行时长
          status: TASK_SEGMENT_STATUS.planned,
          source: 'manual',
        },
      })
      setStartsAt('')
      setEndsAt('')
    } catch (err) {
      setError(err instanceof Error ? err.message : '添加失败')
    }
  }

  const busy = actions.start.isPending || actions.pause.isPending || actions.stop.isPending
  // 时长一律以服务端累计值为准（正在跑的那一轮由「累计 − 已闭合段」反推），
  // 组件里不读本地时钟，避免界面与将来报告读到的数字分叉。
  const totalSeconds = timer?.accumulatedSeconds ?? 0
  const currentRun = currentRunSeconds(segments, totalSeconds)

  return (
    <Dialog open={taskId != null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[560px]">
        <DialogHeader title="任务计时" description={taskTitle} />
        <DialogBody className="space-y-4">
          {error && <InlineAlert tone="crit">{error}</InlineAlert>}

          {/* 计时器 */}
          <div className="rounded-ctl border border-border bg-surface p-3">
            <div className="flex items-center justify-between">
              <div>
                <div className="text-[11px] text-text-3">当前累计时长</div>
                <div className="tnum text-[26px] leading-tight font-semibold text-text-1">
                  {formatClock(totalSeconds)}
                </div>
              </div>
              <div className="flex items-center gap-2">
                {running ? (
                  <StatusBadge tone="ok" dot size="sm">计时中</StatusBadge>
                ) : (
                  <StatusBadge tone="neutral" dot={false} size="sm">
                    {timer ? (TASK_STATUS_LABEL[timer.taskStatus] ?? timer.taskStatus) : '未计时'}
                  </StatusBadge>
                )}
              </div>
            </div>

            <div className="mt-3 flex items-center gap-2">
              <Button
                variant="primary"
                size="sm"
                disabled={busy || running != null}
                loading={actions.start.isPending}
                onClick={() => taskId && void act(() => actions.start.mutateAsync(taskId))}
              >
                <Play className="size-3.5" aria-hidden /> {segments.length > 0 ? '继续计时' : '开始计时'}
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={busy || running == null}
                loading={actions.pause.isPending}
                onClick={() => taskId && void act(() => actions.pause.mutateAsync(taskId))}
              >
                <Pause className="size-3.5" aria-hidden /> 暂停
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={busy || running == null}
                loading={actions.stop.isPending}
                onClick={() => taskId && void act(() => actions.stop.mutateAsync(taskId))}
              >
                <Square className="size-3.5" aria-hidden /> 结束
              </Button>
            </div>
            <p className="mt-2 text-[11px] text-text-4">
              开始计时会把任务置为「进行中」；暂停/结束会闭合当前计时段，暂停期间不计入累计时长。
            </p>
          </div>

          {/* 时间段 */}
          {isLoading ? (
            <div className="py-6 text-center text-[13px] text-text-3">加载中…</div>
          ) : segments.length === 0 ? (
            <EmptyState size="sm" title="暂无执行时间段" description="点「开始计时」记录实际执行，或手工添加计划时间段。" />
          ) : (
            <ul className="space-y-1.5">
              {segments.map((seg) => (
                <li
                  key={seg.id}
                  className="flex items-center gap-3 rounded-ctl border border-border px-3 py-2 text-[13px]"
                >
                  <StatusBadge tone={seg.endsAt == null ? 'ok' : 'neutral'} dot={seg.endsAt == null} size="xs">
                    {TASK_SEGMENT_STATUS_LABEL[seg.status] ?? seg.status}
                  </StatusBadge>
                  <span className="tnum flex-1 text-text-1">
                    {seg.endsAt == null
                      ? `${toLocalInputValue(seg.startsAt)} ~ 进行中`
                      : formatRange(seg.startsAt, seg.endsAt)}
                  </span>
                  <span className="tnum text-text-3">
                    {formatClock(seg.endsAt == null ? currentRun : closedSegmentSeconds(seg))}
                  </span>
                  <button
                    type="button"
                    aria-label={`删除时间段 ${seg.id}`}
                    title={seg.endsAt == null ? '正在计时，请先暂停或结束' : '删除时间段'}
                    disabled={seg.endsAt == null || remove.isPending}
                    onClick={() =>
                      taskId &&
                      void act(() => remove.mutateAsync({ taskId, segmentId: seg.id }))
                    }
                    className="rounded-ctl p-1 text-text-4 transition-colors outline-none hover:bg-surface-2 hover:text-crit disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    <Trash2 className="size-3.5" aria-hidden />
                  </button>
                </li>
              ))}
            </ul>
          )}

          {/* 手工计划时间段（用 form 包裹：回车即可提交，不必非得点按钮） */}
          <form
            className="rounded-ctl border border-border bg-surface p-3"
            onSubmit={(e) => {
              e.preventDefault()
              void submit()
            }}
          >
            <Label htmlFor="segment-starts-at">添加计划时间段</Label>
            <div className="grid grid-cols-2 gap-2">
              <Input
                id="segment-starts-at"
                type="datetime-local"
                value={startsAt}
                onChange={(e) => setStartsAt(e.target.value)}
                aria-label="开始时间"
              />
              <Input
                type="datetime-local"
                value={endsAt}
                onChange={(e) => setEndsAt(e.target.value)}
                aria-label="结束时间"
              />
            </div>
            <Button type="submit" variant="secondary" size="sm" className="mt-2" loading={add.isPending}>
              添加时间段
            </Button>
          </form>
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={onClose}>关闭</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
