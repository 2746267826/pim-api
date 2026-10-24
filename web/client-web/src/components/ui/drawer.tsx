import * as DialogPrimitive from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { useAnimationEndFallback } from './exit-guard'
import { cn } from '@/lib/utils'

export const Drawer = DialogPrimitive.Root
export const DrawerClose = DialogPrimitive.Close

const sideClasses = {
  right:
    'pim-drawer-right inset-y-0 right-0 w-[420px] max-w-[calc(100dvw-24px)] animate-[pim-slide-in-right_240ms_cubic-bezier(.32,.72,.24,1)]',
  left: 'pim-drawer-left inset-y-0 left-0 w-[280px] max-w-[calc(100dvw-56px)] animate-[pim-slide-in-left_240ms_cubic-bezier(.32,.72,.24,1)]',
}

export function DrawerContent({
  side = 'right',
  className,
  children,
}: {
  side?: keyof typeof sideClasses
  className?: string
  children: ReactNode
}) {
  /* 退场兜底：同 dialog（动画时钟冻结时 Radix 等不到 animationend） */
  const [overlayNode, setOverlayNode] = useState<HTMLElement | null>(null)
  const [contentNode, setContentNode] = useState<HTMLElement | null>(null)
  useAnimationEndFallback(overlayNode)
  useAnimationEndFallback(contentNode)

  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay
        ref={setOverlayNode}
        className="pim-dialog-overlay fixed inset-0 z-40 bg-text-1/30 animate-[pim-fade-in_200ms_ease-out]"
      />
      <DialogPrimitive.Content
        ref={setContentNode}
        className={cn(
          'fixed z-50 flex flex-col bg-bg shadow-modal outline-none',
          sideClasses[side],
          className,
        )}
      >
        {children}
        <DialogPrimitive.Close
          aria-label="关闭"
          className="absolute top-3.5 right-3.5 rounded-ctl p-1.5 text-text-3 transition-colors hover:bg-surface hover:text-text-1 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring"
        >
          <X className="size-4" aria-hidden />
        </DialogPrimitive.Close>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

/** 编辑抽屉标准结构：头（标题+说明）/ 滚动体 / 底部右对齐操作 */
export function DrawerHeader({
  title,
  description,
  className,
}: {
  title: ReactNode
  description?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('shrink-0 border-b border-divider px-4 py-3.5 pr-12', className)}>
      <DialogPrimitive.Title className="text-base font-semibold text-text-1">
        {title}
      </DialogPrimitive.Title>
      {description != null && (
        <DialogPrimitive.Description className="mt-0.5 text-xs text-text-3">
          {description}
        </DialogPrimitive.Description>
      )}
    </div>
  )
}

export function DrawerBody({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex-1 overflow-y-auto px-4 py-4', className)} {...props} />
}

export function DrawerFooter({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex shrink-0 items-center justify-end gap-2 border-t border-divider px-4 py-3', className)}
      {...props}
    />
  )
}
