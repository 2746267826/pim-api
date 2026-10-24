import type { HTMLAttributes, ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Card } from './card'
import type { Tone } from './status-badge'

const hintToneText: Record<Tone, string> = {
  ok: 'text-ok',
  warn: 'text-warn',
  crit: 'text-crit',
  info: 'text-info',
  neutral: 'text-text-3',
}

export interface MetricCardProps extends HTMLAttributes<HTMLDivElement> {
  label: ReactNode
  value: ReactNode
  hint?: ReactNode
  hintTone?: Tone
  icon?: LucideIcon
}

/** 指标卡：标签 + 28px 等宽数字大数 + 可选语义提示行 */
export function MetricCard({
  label,
  value,
  hint,
  hintTone = 'neutral',
  icon: Icon,
  className,
  ...props
}: MetricCardProps) {
  return (
    <Card className={cn('flex flex-col justify-center gap-0.5 px-4 py-3', className)} {...props}>
      <div className="flex items-center gap-1.5 text-xs text-text-3">
        {Icon && <Icon className="size-3.5 shrink-0" strokeWidth={1.75} aria-hidden />}
        <span className="truncate">{label}</span>
      </div>
      <div className="tnum text-[26px] leading-9 font-semibold text-text-1">{value}</div>
      {hint != null && (
        <div className={cn('truncate text-xs', hintToneText[hintTone])}>{hint}</div>
      )}
    </Card>
  )
}
