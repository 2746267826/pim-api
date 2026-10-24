import type { ButtonHTMLAttributes } from 'react'
import { cn } from '@/lib/utils'

export interface ChipProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  active?: boolean
}

/** chips 过滤条（可切换短标签）：未选白底描边，选中主色 soft 底 */
export function Chip({ active = false, className, type = 'button', ...props }: ChipProps) {
  return (
    <button
      type={type}
      aria-pressed={active}
      className={cn(
        'inline-flex h-7 shrink-0 items-center gap-1 whitespace-nowrap rounded-full border px-3 text-[13px] font-medium transition-colors duration-150 outline-none disabled:opacity-50',
        active
          ? 'border-primary bg-primary-soft text-primary hover:bg-primary-soft/80'
          : 'border-border bg-bg text-text-2 hover:border-border-strong hover:text-text-1',
        className,
      )}
      {...props}
    />
  )
}
