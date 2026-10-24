import type { ComponentType, ReactNode, SVGProps } from 'react'
import { Inbox } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface EmptyStateProps {
  /** 未传时使用默认收件箱图标——保证全站空态观感统一 */
  icon?: ComponentType<SVGProps<SVGSVGElement>>
  title: ReactNode
  description?: ReactNode
  action?: ReactNode
  size?: 'sm' | 'md'
  className?: string
}

/** 统一空状态（图标位 + 一句话 + 可选操作） */
export function EmptyState({
  icon: Icon = Inbox,
  title,
  description,
  action,
  size = 'md',
  className,
}: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center gap-1.5 text-center',
        size === 'md' ? 'py-12' : 'py-6',
        className,
      )}
    >
      {Icon && (
        <Icon
          className={cn('mb-1 text-text-4', size === 'md' ? 'size-6' : 'size-5')}
          strokeWidth={1.75}
          aria-hidden
        />
      )}
      <div className={cn('font-medium text-text-2', size === 'md' ? 'text-sm' : 'text-[13px]')}>
        {title}
      </div>
      {description != null && (
        <div className="max-w-[320px] text-[13px] text-text-3">{description}</div>
      )}
      {action != null && <div className="mt-2.5">{action}</div>}
    </div>
  )
}
