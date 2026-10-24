import { useEffect, useRef } from 'react'
import * as echarts from 'echarts/core'
import {
  BarChart,
  FunnelChart,
  GaugeChart,
  HeatmapChart,
  LineChart,
  PieChart,
  ScatterChart,
} from 'echarts/charts'
import {
  CalendarComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { EChartsCoreOption } from 'echarts/core'
import { cn } from '@/lib/utils'

echarts.use([
  BarChart,
  FunnelChart,
  GaugeChart,
  HeatmapChart,
  LineChart,
  PieChart,
  ScatterChart,
  CalendarComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
  CanvasRenderer,
])

/** ECharts 通用主题：轴线/文字/网格线按设计系统着色（DESIGN.md §图表规范） */
export function themedOption(option: EChartsCoreOption): EChartsCoreOption {
  /*
   * 动画默认值：首绘错峰生长（柱/饼/仪表通用），更新 300ms 平滑过渡。
   * 放在展开前 = 调用方传了 animation 相关字段时以调用方为准。
   * reduced motion（系统设置）整体关掉——与全局动效红线一致。
   */
  const reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
  return {
    textStyle: { fontFamily: 'Inter Variable, Noto Sans SC Variable, sans-serif', color: '#64748B' },
    ...(reducedMotion
      ? { animation: false }
      : {
          animation: true,
          animationDuration: 500,
          animationDurationUpdate: 300,
          animationEasing: 'cubicOut',
          animationEasingUpdate: 'cubicOut',
          animationDelay: (idx: number) => Math.min(idx, 20) * 25,
        }),
    ...option,
  }
}

/*
 * 统一 tooltip 外观：ECharts 默认的白色圆角框在本项目的浅色紧凑风格里偏突兀。
 * 所有图表一律通过 chartTooltip() 生成 tooltip 配置，保证悬浮提示的样式与行为一致。
 */
export const TOOLTIP_BOX_STYLE = {
  backgroundColor: 'rgba(15, 23, 42, 0.92)',
  borderWidth: 0,
  padding: [6, 10] as [number, number],
  textStyle: { color: '#F8FAFC', fontSize: 12, lineHeight: 18 },
  extraCssText: 'border-radius:8px;box-shadow:0 6px 20px rgba(15,23,42,.24);backdrop-filter:blur(2px);',
}

export interface TooltipLike {
  name?: string
  seriesName?: string
  value?: unknown
  percent?: number
  marker?: string
  axisValueLabel?: string
  axisValue?: string | number
  dataIndex?: number
  color?: string
}

/** tooltipBoxStyle 别名（供需要直接展开的调用点使用） */
export const tooltipBoxStyle = TOOLTIP_BOX_STYLE

/**
 * 生成统一风格的 tooltip 配置。
 * formatter 收到 ECharts 的原始 params（item 触发为单个、axis 触发为数组），
 * 可用 isTooltipList(params) 收窄类型后取数组。
 */
export function chartTooltip(opts: {
  trigger?: 'item' | 'axis' | 'none'
  axisPointer?: Record<string, unknown>
  formatter?: (params: TooltipLike | TooltipLike[]) => string
  /** axis 触发时是否按值倒序（默认 true） */
  orderByValue?: boolean
} = {}): Record<string, unknown> {
  const { trigger = 'item', axisPointer, formatter, orderByValue = true } = opts
  return {
    trigger,
    confine: true,
    appendToBody: true,
    ...TOOLTIP_BOX_STYLE,
    ...(axisPointer ? { axisPointer } : {}),
    ...(trigger === 'axis' ? { order: orderByValue ? ('valueDesc' as const) : undefined } : {}),
    ...(formatter
      ? {
          formatter: (params: TooltipLike | TooltipLike[]) => {
            try {
              return formatter(params)
            } catch {
              return ''
            }
          },
        }
      : {}),
  }
}

/** 收窄：axis 触发的 params 是数组 */
export function isTooltipList(p: TooltipLike | TooltipLike[]): p is TooltipLike[] {
  return Array.isArray(p)
}

/** 收窄：item 触发的 params 是单点 */
export function asTooltipItem(p: TooltipLike | TooltipLike[]): TooltipLike {
  return Array.isArray(p) ? (p[0] ?? {}) : p
}

export interface EChartsBoxProps {
  option: EChartsCoreOption
  height?: number
  className?: string
  onClickData?: (params: { seriesName?: string; name?: string; value?: unknown }) => void
}

/** ECharts 按需封装：自动 init/resize/dispose */
export function EChartsBox({ option, height = 260, className, onClickData }: EChartsBoxProps) {
  const ref = useRef<HTMLDivElement>(null)
  const chartRef = useRef<echarts.ECharts | null>(null)

  useEffect(() => {
    if (!ref.current) return
    const chart = echarts.init(ref.current)
    chartRef.current = chart
    const observer = new ResizeObserver(() => chart.resize())
    observer.observe(ref.current)
    return () => {
      observer.disconnect()
      chart.dispose()
      chartRef.current = null
    }
  }, [])

  useEffect(() => {
    const chart = chartRef.current
    if (!chart) return
    chart.setOption(themedOption(option), true)
    if (onClickData) {
      chart.off('click')
      chart.on('click', onClickData as never)
    }
  }, [option, onClickData])

  return <div ref={ref} className={cn('w-full', className)} style={{ height }} />
}

/** CSS 变量 → 实际色值（ECharts canvas 不解析 var()，标签/系列色需在运行时求值） */
export function cssVar(name: string, fallback = '#64748B'): string {
  if (typeof window === 'undefined') return fallback
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  return v || fallback
}

/** 生命周期分类 → 实际色值（图表用；LIFE_CATEGORY_COLOR 存的是变量名） */
export function resolveCssColors(map: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [k, v] of Object.entries(map)) {
    out[k] = v.startsWith('var(') ? cssVar(v.slice(4, -1)) : v
  }
  return out
}
