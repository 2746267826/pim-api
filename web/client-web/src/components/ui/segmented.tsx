import * as ToggleGroupPrimitive from '@radix-ui/react-toggle-group'
import { motion } from 'motion/react'
import { useId } from 'react'
import { cn } from '@/lib/utils'

export interface SegmentedOption<T extends string> {
  value: T
  label: React.ReactNode
  title?: string
  disabled?: boolean
}

export interface SegmentedProps<T extends string> {
  value: T
  onValueChange: (value: T) => void
  options: SegmentedOption<T>[]
  size?: 'sm' | 'md'
  className?: string
}

/**
 * 分段控件（规格"胶囊型单选切换"）：容器 #F1F5F9，选中白底+主色字+卡片阴影。
 * 选中胶囊用 motion layoutId 平滑滑动（layout 动画走 transform，reduced-motion 自动禁用）；
 * layoutId 按实例隔离（useId），同页多个 Segmented 互不串场。
 */
export function Segmented<T extends string>({
  value,
  onValueChange,
  options,
  size = 'md',
  className,
}: SegmentedProps<T>) {
  const pillId = useId()
  return (
    <ToggleGroupPrimitive.Root
      type="single"
      value={value}
      onValueChange={(v) => {
        if (v) onValueChange(v as T)
      }}
      className={cn('inline-flex items-center rounded-full bg-surface-2 p-[3px]', className)}
      role="tablist"
    >
      {options.map((o) => {
        const active = value === o.value
        return (
          <ToggleGroupPrimitive.Item
            key={o.value}
            value={o.value}
            title={o.title}
            disabled={o.disabled}
            className={cn(
              'relative inline-flex items-center justify-center whitespace-nowrap rounded-full px-3 font-medium outline-none transition-colors duration-150 hover:text-text-1 disabled:opacity-50',
              active ? 'text-primary' : 'text-text-3',
              size === 'md' ? 'h-7 text-[13px]' : 'h-6 px-2.5 text-xs',
            )}
          >
            {active && (
              <motion.span
                layoutId={`pim-seg-pill-${pillId}`}
                className="absolute inset-0 rounded-full bg-bg shadow-card"
                transition={{ type: 'spring', duration: 0.32, bounce: 0.18 }}
              />
            )}
            <span className="relative z-10">{o.label}</span>
          </ToggleGroupPrimitive.Item>
        )
      })}
    </ToggleGroupPrimitive.Root>
  )
}
