import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/*
 * 图表统一悬浮明细（深色圆角卡片）：
 * - position:fixed 依锚点矩形定位，脱离文档流，绝不撑大页面；
 * - 左右收敛到视口内，下方放不下时翻到锚点上方；
 * - 内容尺寸实测后定位（首帧隐藏避免闪跳）。
 *
 * 使用方式：在触发元素上记录 anchor=getBoundingClientRect()，
 * 把本组件挂在不受 overflow 裁剪的位置（通常是组件根）。
 */

export interface TooltipAnchor {
  left: number
  top: number
  bottom: number
  width: number
}

/**
 * 位置计算（纯函数）：以锚点水平中心为准，整体收敛在视口内；
 * 下方空间不足时改为贴锚点上方。
 */
export function placeTooltip(
  anchor: TooltipAnchor,
  box: { width: number; height: number },
  viewport: { width: number; height: number },
  margin = 8,
): { left: number; top: number | null; bottom: number | null } {
  const centerX = anchor.left + anchor.width / 2
  const maxLeft = Math.max(margin, viewport.width - box.width - margin)
  const left = Math.min(Math.max(centerX - box.width / 2, margin), maxLeft)

  const below = anchor.bottom + 6
  const flip = below + box.height + margin > viewport.height
  return flip
    ? { left, top: null, bottom: Math.max(margin, viewport.height - anchor.top + 6) }
    : { left, top: below, bottom: null }
}

export interface FloatingTooltipProps {
  /** 触发元素的 getBoundingClientRect() */
  anchor: TooltipAnchor
  children: ReactNode
  /** 内容最大宽度（默认 320；超过部分由调用方自行截断） */
  maxWidth?: number
  className?: string
}

export function FloatingTooltip({ anchor, children, maxWidth = 320, className }: FloatingTooltipProps) {
  const ref = useRef<HTMLDivElement>(null)
  const [box, setBox] = useState<{ w: number; h: number } | null>(null)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const r = el.getBoundingClientRect()
    setBox((prev) => (prev && prev.w === r.width && prev.h === r.height ? prev : { w: r.width, h: r.height }))
  }, [children])

  const margin = 8
  const vw = typeof window === 'undefined' ? 1280 : window.innerWidth
  const vh = typeof window === 'undefined' ? 720 : window.innerHeight
  const w = box?.w ?? Math.min(maxWidth, vw - margin * 2)
  const h = box?.h ?? 36
  const pos = placeTooltip(anchor, { width: w, height: h }, { width: vw, height: vh })

  return (
    <div
      ref={ref}
      role="tooltip"
      className={cn(
        'pointer-events-none fixed z-50 rounded-ctl px-2.5 py-1.5 text-[11px] leading-4 text-white shadow-lg',
        className,
      )}
      style={{
        left: pos.left,
        top: pos.top ?? undefined,
        bottom: pos.bottom ?? undefined,
        maxWidth,
        backgroundColor: 'rgba(15,23,42,.94)',
        // 首帧未测量前先隐藏，避免从估算位置闪到实测位置
        visibility: box ? 'visible' : 'hidden',
      }}
    >
      {children}
    </div>
  )
}
