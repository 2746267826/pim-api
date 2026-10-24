import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface PageHeaderProps {
  title: ReactNode
  subtitle?: ReactNode
  actions?: ReactNode
  className?: string
}

/** 页面页头：标题 + 副标题 + 右侧操作区（移动端由壳的顶部条补充返回/操作） */
export function PageHeader({ title, subtitle, actions, className }: PageHeaderProps) {
  return (
    <div className={cn('mb-5 flex flex-wrap items-start justify-between gap-3', className)}>
      <div className="min-w-0">
        <h1 className="truncate text-2xl leading-8 font-semibold text-text-1">{title}</h1>
        {subtitle != null && <p className="mt-1 text-[13px] text-text-3">{subtitle}</p>}
      </div>
      {actions != null && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  )
}
