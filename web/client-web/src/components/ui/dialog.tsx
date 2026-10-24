import * as DialogPrimitive from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { useState, type HTMLAttributes, type ReactNode } from 'react'
import { useAnimationEndFallback } from './exit-guard'
import { cn } from '@/lib/utils'

export const Dialog = DialogPrimitive.Root
export const DialogTrigger = DialogPrimitive.Trigger
export const DialogClose = DialogPrimitive.Close

export function DialogContent({
  className,
  children,
  showClose = true,
}: {
  className?: string
  children: ReactNode
  showClose?: boolean
}) {
  /* 退场兜底：动画时钟冻结（后台标签/WebView）时 Radix 等不到 animationend，需手动补发 */
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
          'pim-dialog-content fixed top-1/2 left-1/2 z-50 flex max-h-[calc(100dvh-48px)] w-[calc(100dvw-32px)] max-w-[560px] -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-modal border border-border bg-bg shadow-modal outline-none animate-[pim-pop-in_200ms_cubic-bezier(.32,.72,.24,1)]',
          className,
        )}
      >
        {children}
        {showClose && (
          <DialogPrimitive.Close
            aria-label="关闭"
            className="absolute top-3.5 right-3.5 rounded-ctl p-1.5 text-text-3 transition-colors hover:bg-surface hover:text-text-1 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring"
          >
            <X className="size-4" aria-hidden />
          </DialogPrimitive.Close>
        )}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

export function DialogHeader({
  title,
  description,
  className,
}: {
  title: ReactNode
  description?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('shrink-0 border-b border-divider px-5 py-4 pr-12', className)}>
      <DialogPrimitive.Title className="text-base font-semibold text-text-1">
        {title}
      </DialogPrimitive.Title>
      {description != null && (
        <DialogPrimitive.Description className="mt-0.5 text-[13px] text-text-3">
          {description}
        </DialogPrimitive.Description>
      )}
    </div>
  )
}

export function DialogBody({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex-1 overflow-y-auto px-5 py-4', className)} {...props} />
}

export function DialogFooter({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex shrink-0 items-center justify-end gap-2 border-t border-divider px-5 py-3.5', className)}
      {...props}
    />
  )
}
