/*
 * 共享动效原语（motion/react 封装；全站动效统一入口）。
 *
 * 红线（DESIGN §动效）：
 * - 只动 transform / opacity（GPU 合成，不触发重排）；
 * - stagger 延迟封顶：第 11 项起不再叠加延迟（STAGGER_CAP），长列表不会越排越久；
 * - reduced motion：根组件用 <MotionConfig reducedMotion="user"> 全局兜底，
 *   命令式动画（CountUp）另用 useReducedMotion 显式短路。
 */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { animate, motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

/** 每项错开间隔（秒） */
const STAGGER_STEP = 0.035
/** 参与错开的最大项数：之后的项直接出现（0 延迟），避免长列表等待 */
const STAGGER_CAP = 10

const EASE_STANDARD = [0.32, 0.72, 0.24, 1] as const

export function Stagger({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <motion.div className={className} initial="hidden" animate="show">
      {children}
    </motion.div>
  )
}

/**
 * 入场淡入上移。index 决定错开延迟（超过 STAGGER_CAP 不再叠加）。
 * 变化轮询重渲染不会重播：variants 动画只在挂载时跑一次。
 */
export function StaggerItem({
  children,
  index = 0,
  className,
}: {
  children: ReactNode
  index?: number
  className?: string
}) {
  return (
    <motion.div
      className={className}
      variants={{
        hidden: { opacity: 0, y: 8 },
        show: (i: number) => ({
          opacity: 1,
          y: 0,
          transition: { duration: 0.2, ease: EASE_STANDARD, delay: Math.min(i, STAGGER_CAP) * STAGGER_STEP },
        }),
      }}
      custom={index}
    >
      {children}
    </motion.div>
  )
}

/**
 * 数字滚动：值变化时从旧值缓动到新值（仅挂载/变更时运行一次，
 * 轮询重渲染同值不触发）。reduced motion 或大数值（>10 万）直接直出。
 */
export function CountUp({
  value,
  duration = 0.45,
  className,
}: {
  value: number
  duration?: number
  className?: string
}) {
  const reduced = useReducedMotion()
  const [display, setDisplay] = useState(() => (reduced ? value : 0))
  const prev = useRef(value)

  useEffect(() => {
    if (reduced || Math.abs(value) > 100_000) {
      prev.current = value
      setDisplay(value)
      return
    }
    const controls = animate(prev.current, value, {
      duration,
      ease: 'easeOut',
      onUpdate: (v) => setDisplay(Math.round(v)),
    })
    prev.current = value
    return () => controls.stop()
  }, [value, duration, reduced])

  return <span className={cn('tnum', className)}>{display.toLocaleString()}</span>
}

/** 单元素入场（不参与 stagger 的独立块，如弹窗内容/横幅） */
export function FadeIn({
  children,
  className,
  y = 6,
}: {
  children: ReactNode
  className?: string
  y?: number
}) {
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, y }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, ease: EASE_STANDARD }}
    >
      {children}
    </motion.div>
  )
}
