import { AlertTriangle, CheckCircle2, Info, XCircle } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'
import type { Tone } from './status-badge'

const toneStyles: Record<Tone, { box: string; icon: string; Icon: typeof Info }> = {
  ok: { box: 'border-ok-border bg-ok-soft text-ok', icon: 'text-ok', Icon: CheckCircle2 },
  warn: { box: 'border-warn-border bg-warn-soft text-warn', icon: 'text-warn', Icon: AlertTriangle },
  crit: { box: 'border-crit-border bg-crit-soft text-crit', icon: 'text-crit', Icon: XCircle },
  info: { box: 'border-info-border bg-info-soft text-info', icon: 'text-info', Icon: Info },
  neutral: { box: 'border-border bg-surface text-text-2', icon: 'text-text-3', Icon: Info },
}

export interface InlineAlertProps {
  tone?: Tone
  title?: ReactNode
  children?: ReactNode
  className?: string
}

/** 内联告警/结果横幅（操作结果横幅原语） */
export function InlineAlert({ tone = 'info', title, children, className }: InlineAlertProps) {
  const { box, icon, Icon } = toneStyles[tone]
  return (
    <div
      role={tone === 'crit' ? 'alert' : 'status'}
      className={cn('flex items-start gap-2.5 rounded-ctl border px-3 py-2.5 text-[13px]', box, className)}
    >
      <Icon className={cn('mt-0.5 size-4 shrink-0', icon)} aria-hidden />
      <div className="min-w-0">
        {title != null && <div className="font-medium">{title}</div>}
        {children != null && <div className={cn('text-text-2', title != null && 'mt-0.5')}>{children}</div>}
      </div>
    </div>
  )
}
