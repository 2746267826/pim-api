import { formatTime } from '@/lib/datetime'
import { cn } from '@/lib/utils'
import { dueUrgency, dueUrgencyView, parseDueMs, useDueNow } from '../due-urgency'

export interface DueTextProps {
  due: string | null | undefined
  /** 已完成/已取消：不再强调紧迫度 */
  inactive?: boolean
  prefix?: string
  className?: string
}

/** 四处任务入口共用的截止时间呈现：颜色与文字同源，不只靠颜色区分 */
export function DueText({ due, inactive = false, prefix = '截止', className }: DueTextProps) {
  const now = useDueNow()
  const urgency = dueUrgency(due, now, inactive)
  const view = dueUrgencyView(urgency)
  const dueMs = parseDueMs(due)

  return (
    <span className={cn('tnum', view.className, className)}>
      {dueMs == null ? `${prefix} 时间无效` : `${prefix} ${formatTime(due!, new Date(now))}${view.suffix}`}
    </span>
  )
}
