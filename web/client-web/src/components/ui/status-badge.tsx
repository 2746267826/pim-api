import type { HTMLAttributes } from 'react'
import { cn } from '@/lib/utils'

export type Tone = 'ok' | 'warn' | 'crit' | 'info' | 'neutral'

const toneDot: Record<Tone, string> = {
  ok: 'bg-ok',
  warn: 'bg-warn',
  crit: 'bg-crit',
  info: 'bg-info',
  neutral: 'bg-neutral',
}

const toneSoft: Record<Tone, string> = {
  ok: 'border-ok-border bg-ok-soft text-ok',
  warn: 'border-warn-border bg-warn-soft text-warn',
  crit: 'border-crit-border bg-crit-soft text-crit',
  info: 'border-info-border bg-info-soft text-info',
  neutral: 'border-border bg-surface text-text-3',
}

export interface StatusBadgeProps extends HTMLAttributes<HTMLSpanElement> {
  tone?: Tone
  dot?: boolean
  /** sm（默认 22px）｜xs（18px：表格行/紧凑列表行用，避免撑高行距） */
  size?: 'sm' | 'xs'
}

/** 状态徽标（语义短标签：健康/告警/离线/同步中等） */
export function StatusBadge({
  tone = 'neutral',
  dot = true,
  size = 'sm',
  className,
  children,
  ...props
}: StatusBadgeProps) {
  const xs = size === 'xs'
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-badge border font-medium whitespace-nowrap',
        xs ? 'h-[18px] gap-1 px-1.5 text-[10px]' : 'h-[22px] gap-1.5 px-2 text-xs',
        toneSoft[tone],
        className,
      )}
      {...props}
    >
      {dot && <span className={cn('shrink-0 rounded-full', xs ? 'size-1' : 'size-1.5', toneDot[tone])} aria-hidden />}
      {children}
    </span>
  )
}
