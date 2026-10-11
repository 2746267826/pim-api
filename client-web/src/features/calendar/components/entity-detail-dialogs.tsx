import { type ReactNode } from 'react'
import { CalendarClock, Pencil } from 'lucide-react'
import { Button, Dialog, DialogBody, DialogContent, DialogHeader, StatusBadge } from '@/components/ui'
import { formatRange, formatTime, durationToMinutes, formatDuration } from '@/lib/datetime'
import { htmlToPlainText } from '@/lib/text'
import { cn } from '@/lib/utils'
import type { EventResponse, TaskResponse } from '../types'
import { readEventRrule } from '../rrule'

/*
 * 日程 / 任务详情弹窗（先看后改）：
 * 点击卡片不再直接进入编辑，而是先展示全部字段的只读视图，用户确认后再点「编辑」。
 * 字段清单对齐编辑表单（规格 02 §calendar：事件卡徽标位含闲忙/地点/来源/描述/重复/重要/提醒）。
 */

/* ── 通用行 ─────────────────────────────────────────────────── */

function Row({ label, children }: { label: string; children: ReactNode }) {
  if (children == null || children === '' || children === false) return null
  return (
    <div className="flex gap-3 border-b border-divider py-2 last:border-b-0">
      <span className="w-20 shrink-0 pt-0.5 text-xs text-text-3">{label}</span>
      <span className="min-w-0 flex-1 text-[13px] break-words text-text-1">{children}</span>
    </div>
  )
}

/** 空值占位（用于「必须可见但可能为空」的字段） */
function Empty() {
  return <span className="text-text-4">—</span>
}

/* ── 常量映射 ───────────────────────────────────────────────── */

const SHOW_AS_LABEL: Record<string, string> = {
  free: '空闲',
  tentative: '暂定',
  busy: '忙碌',
  oof: '外出',
  workingElsewhere: '异地工作',
  unknown: '未知',
}

const IMPORTANCE_LABEL: Record<string, string> = {
  low: '低',
  normal: '普通',
  high: '高',
}

const SENSITIVITY_LABEL: Record<string, string> = {
  normal: '普通',
  personal: '个人',
  private: '私密',
  confidential: '机密',
}

const STATUS_LABEL: Record<string, string> = {
  CONFIRMED: '已确认',
  TENTATIVE: '暂定',
  CANCELLED: '已取消',
}

const TASK_STATUS_LABEL: Record<string, string> = {
  'NEEDS-ACTION': '待办',
  'IN-PROCESS': '进行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消',
}

const PRIORITY_LABEL: Record<number, string> = {
  1: '最高',
  3: '高',
  5: '普通',
  7: '低',
  9: '最低',
}

/** 重复规则的可读摘要（仅常见 FREQ/INTERVAL/COUNT/UNTIL 组合） */
export function formatRrule(rrule: string | null | undefined): string | null {
  if (!rrule) return null
  const freq = /FREQ=([A-Z]+)/.exec(rrule)?.[1]
  const interval = Number(/INTERVAL=(\d+)/.exec(rrule)?.[1] ?? 1)
  const count = /COUNT=(\d+)/.exec(rrule)?.[1]
  const until = /UNTIL=([0-9TZ]+)/.exec(rrule)?.[1]
  const freqLabel: Record<string, string> = {
    DAILY: '天',
    WEEKLY: '周',
    MONTHLY: '月',
    YEARLY: '年',
  }
  const byDay = /BYDAY=([A-Z,]+)/.exec(rrule)?.[1]
  const DAY_LABEL: Record<string, string> = {
    MO: '一', TU: '二', WE: '三', TH: '四', FR: '五', SA: '六', SU: '日',
  }
  if (!freq || !freqLabel[freq]) return rrule
  let text = interval > 1 ? `每 ${interval} ${freqLabel[freq]}` : `每${freqLabel[freq]}`
  if (byDay) {
    text += ` 周${byDay.split(',').map((d) => DAY_LABEL[d] ?? d).join('、')}`
  }
  if (count) text += `，共 ${count} 次`
  else if (until) text += `，至 ${until.slice(0, 10)}`
  return text
}

/* ── 日程详情 ───────────────────────────────────────────────── */

export function EventDetailDialog({
  event,
  calendarName,
  onClose,
  onEdit,
}: {
  event: EventResponse | null
  calendarName?: string
  onClose: () => void
  onEdit: (event: EventResponse) => void
}) {
  if (!event) return null
  const durationMin =
    event.dtStart && event.dtEnd
      ? Math.max(0, Math.round((new Date(event.dtEnd).getTime() - new Date(event.dtStart).getTime()) / 60_000))
      : null
  const attendees = event.attendees ?? []

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[600px]">
        <DialogHeader title="日程详情" description={calendarName} />
        <DialogBody className="max-h-[62dvh] overflow-y-auto">
          {/* 标题与状态 */}
          <div className="mb-1 flex flex-wrap items-center gap-2">
            <h3 className={cn('text-[15px] font-semibold', event.isCancelled ? 'text-text-4 line-through' : 'text-text-1')}>
              {event.title}
            </h3>
            <StatusBadge tone={event.isCancelled ? 'neutral' : event.status === 'CONFIRMED' ? 'ok' : 'warn'} dot={false}>
              {STATUS_LABEL[event.status] ?? event.status}
            </StatusBadge>
            {event.isSeriesMaster && <StatusBadge tone="info" dot={false}>重复系列</StatusBadge>}
            {event.isException && <StatusBadge tone="warn" dot={false}>例外</StatusBadge>}
            {event.source === 'outlook' && <StatusBadge tone="info" dot={false}>Outlook</StatusBadge>}
          </div>

          <div className="mt-2">
            <Row label="时间">
              {event.isAllDay
                ? `${event.dtStart.slice(0, 10)}（全天）`
                : formatRange(event.dtStart, event.dtEnd)}
            </Row>
            <Row label="时长">
              {durationMin != null ? formatDuration(durationMin * 60) : <Empty />}
            </Row>
            <Row label="日历本">
              {calendarName ?? <Empty />}
            </Row>
            <Row label="地点">{event.location || <Empty />}</Row>
            <Row label="闲忙">{SHOW_AS_LABEL[event.showAs ?? ''] ?? event.showAs ?? <Empty />}</Row>
            <Row label="重要度">{IMPORTANCE_LABEL[event.importance ?? ''] ?? event.importance ?? <Empty />}</Row>
            <Row label="敏感度">{SENSITIVITY_LABEL[event.sensitivity ?? ''] ?? event.sensitivity ?? <Empty />}</Row>
            <Row label="重复">{formatRrule(readEventRrule(event)) ?? <Empty />}</Row>
            <Row label="提醒">
              {event.isReminderOn ? (
                <span className="flex items-center gap-1.5">
                  <CalendarClock className="size-3.5 text-text-3" aria-hidden />
                  {event.reminderMinutesBeforeStart != null ? `提前 ${event.reminderMinutesBeforeStart} 分钟` : '已开启'}
                </span>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="分类">
              {event.categories && event.categories.length > 0 ? (
                <span className="flex flex-wrap gap-1">
                  {event.categories.map((c) => (
                    <span key={c} className="rounded-badge bg-surface-2 px-1.5 py-0.5 text-[11px] text-text-2">{c}</span>
                  ))}
                </span>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="组织者">
              {event.organizer ? [event.organizer.name, event.organizer.email].filter(Boolean).join(' · ') : <Empty />}
            </Row>
            <Row label="参会人">
              {attendees.length > 0 ? (
                <ul className="space-y-0.5">
                  {attendees.map((a, i) => (
                    <li key={i} className="truncate">
                      {[a.name, a.email].filter(Boolean).join(' · ')}
                      {a.type === 'optional' && <span className="ml-1 text-text-4">（可选）</span>}
                    </li>
                  ))}
                </ul>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="在线会议">
              {event.isOnlineMeeting ? (
                event.onlineMeetingUrl ? (
                  <a href={event.onlineMeetingUrl} target="_blank" rel="noreferrer" className="text-primary hover:underline">
                    {event.onlineMeetingProvider ?? '加入会议'}
                  </a>
                ) : (
                  event.onlineMeetingProvider ?? '是'
                )
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="外部链接">
              {event.externalLink ? (
                <a href={event.externalLink} target="_blank" rel="noreferrer" className="break-all text-primary hover:underline">
                  {event.externalLink}
                </a>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="描述">
              {event.description ? (
                <span className="whitespace-pre-wrap">{htmlToPlainText(event.description)}</span>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="来源">{event.source === 'outlook' ? 'Outlook 同步' : '本地创建'}</Row>
            <Row label="UID">
              <span className="mono text-[11px] break-all text-text-3">{event.uid}</span>
            </Row>
          </div>
        </DialogBody>

        <div className="flex items-center gap-2 border-t border-divider px-4 py-3">
          <Button variant="ghost" size="sm" onClick={onClose}>关闭</Button>
          <Button
            variant="secondary"
            size="sm"
            className="ml-auto"
            onClick={() => onEdit(event)}
          >
            <Pencil className="size-3.5" aria-hidden /> 编辑
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/* ── 任务详情 ───────────────────────────────────────────────── */

export function TaskDetailDialog({
  task,
  taskBookName,
  onClose,
  onEdit,
}: {
  task: TaskResponse | null
  taskBookName?: string
  onClose: () => void
  onEdit: (task: TaskResponse) => void
}) {
  if (!task) return null
  const checklist = task.checklistItems ?? []
  const doneCount = checklist.filter((c) => c.isDone).length

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[600px]">
        <DialogHeader title="任务详情" description={taskBookName} />
        <DialogBody className="max-h-[62dvh] overflow-y-auto">
          <div className="mb-1 flex flex-wrap items-center gap-2">
            <h3 className={cn('text-[15px] font-semibold', task.status === 'COMPLETED' ? 'text-text-4 line-through' : 'text-text-1')}>
              {task.title}
            </h3>
            <StatusBadge tone={task.status === 'COMPLETED' ? 'ok' : task.status === 'CANCELLED' ? 'neutral' : 'info'} dot={false}>
              {TASK_STATUS_LABEL[task.status] ?? task.status}
            </StatusBadge>
            {task.isInbox && <StatusBadge tone="warn" dot={false}>收集箱</StatusBadge>}
          </div>

          <div className="mt-2">
            <Row label="状态">{TASK_STATUS_LABEL[task.status] ?? task.status}</Row>
            <Row label="优先级">{PRIORITY_LABEL[task.priority] ?? String(task.priority)}</Row>
            <Row label="完成度">{task.percentComplete}%</Row>
            <Row label="任务本">{taskBookName ?? <Empty />}</Row>
            <Row label="截止">{task.due ? formatTime(task.due) : <Empty />}</Row>
            <Row label="计划开始">{task.dtStart ? formatTime(task.dtStart) : <Empty />}</Row>
            <Row label="计划结束">{task.plannedEnd ? formatTime(task.plannedEnd) : <Empty />}</Row>
            <Row label="预计时长">
              {task.estimatedDuration ? formatDuration((durationToMinutes(task.estimatedDuration) ?? 0) * 60) : <Empty />}
            </Row>
            <Row label="最小时段">
              {task.minimumSegment ? formatDuration((durationToMinutes(task.minimumSegment) ?? 0) * 60) : <Empty />}
            </Row>
            <Row label="清单">
              {checklist.length > 0 ? (
                <ul className="space-y-0.5">
                  {checklist.map((c) => (
                    <li key={c.id} className={cn('flex items-start gap-1.5', c.isDone && 'text-text-4 line-through')}>
                      <span aria-hidden>{c.isDone ? '✓' : '○'}</span>
                      <span className="min-w-0 flex-1">{c.title}</span>
                    </li>
                  ))}
                  <li className="pt-0.5 text-[11px] text-text-4">已完成 {doneCount}/{checklist.length}</li>
                </ul>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="描述">
              {task.description ? (
                <span className="whitespace-pre-wrap">{htmlToPlainText(task.description)}</span>
              ) : (
                <Empty />
              )}
            </Row>
            <Row label="UID">
              <span className="mono text-[11px] break-all text-text-3">{task.uid}</span>
            </Row>
          </div>
        </DialogBody>

        <div className="flex items-center gap-2 border-t border-divider px-4 py-3">
          <Button variant="ghost" size="sm" onClick={onClose}>关闭</Button>
          <Button variant="secondary" size="sm" className="ml-auto" onClick={() => onEdit(task)}>
            <Pencil className="size-3.5" aria-hidden /> 编辑
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
