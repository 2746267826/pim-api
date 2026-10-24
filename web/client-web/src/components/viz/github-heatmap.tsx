import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import { cn } from '@/lib/utils'
import { FloatingTooltip } from './floating-tooltip'

/*
 * GitHub 贡献图风格热力图（全局统一）：
 * - 小圆角方块，5 档色阶（0 档 = 无记录），顶部月份标签、左侧星期标签、右下「少 → 多」图例；
 * - 色阶按「非零值的四分位」分档（与 GitHub 一致），而不是按最大值归一——
 *   否则个别极高的日子会把其余天全压成最浅档，看上去像大量空白；
 * - 格子尺寸随容器宽度自适应（短范围自动放大），放不下时横向滚动；
 * - 悬浮明细用统一的 FloatingTooltip（不再使用浏览器原生 title）。

 * 两种数据形态：按天序列（GitHub 原版）与「周 × 星期」矩阵（PC 网格接口返回）。
 */

export interface HeatDay {
  /** yyyy-MM-dd（本地口径） */
  date: string
  value: number
  /** 悬浮补充说明 */
  detail?: string
}

/** 色阶：GitHub 风格绿色；可传入自定义 5 档 */
export const HEAT_RAMP_BLUE = ['#EBEDF0', '#DBEAFE', '#93C5FD', '#3B82F6', '#1D4ED8']
export const HEAT_RAMP_GREEN = ['#EBEDF0', '#9BE9A8', '#40C463', '#30A14E', '#216E39']
export const HEAT_RAMP_ORANGE = ['#EBEDF0', '#FED7AA', '#FDBA74', '#F97316', '#C2410C']

const WEEKDAY_LABELS = ['一', '二', '三', '四', '五', '六', '日']

/** ISO 周一为一周起点（0=周一 … 6=周日） */
function mondayIndex(d: Date): number {
  return (d.getDay() + 6) % 7
}

function parseLocalDate(s: string): Date {
  const [y, m, d] = s.split('-').map(Number)
  return new Date(y, (m ?? 1) - 1, d ?? 1)
}

function dateKey(d: Date): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

/**
 * 色阶阈值：非零值的四分位（GitHub 的做法）。
 * 返回 [q25, q50, q75]；无非零值返回 null。
 */
export function quantileThresholds(values: readonly number[]): [number, number, number] | null {
  const nz = values.filter((v) => v > 0).sort((a, b) => a - b)
  if (nz.length === 0) return null
  const at = (p: number) => nz[Math.min(nz.length - 1, Math.floor(p * nz.length))]
  return [at(0.25), at(0.5), at(0.75)]
}

/** 值 → 档位：0 档 = 无记录；非零按四分位落 1–4 档（无阈值时非零至少落 1 档，避免有值却不可见） */
export function levelFor(value: number, th: readonly [number, number, number] | null): number {
  if (value <= 0) return 0
  if (!th) return 1
  if (value <= th[0]) return 1
  if (value <= th[1]) return 2
  if (value <= th[2]) return 3
  return 4
}

export interface GitHubHeatmapProps {
  /** 数据点（任意顺序，内部按 date 索引） */
  days: HeatDay[]
  /** 单元尺寸下限（px），默认 11；容器足够宽时自动放大 */
  cellSize?: number
  /** 单元尺寸上限（px），默认 26（短范围靠放大填满容器） */
  maxCellSize?: number
  /** 单元间距（px），默认 3 */
  gap?: number
  /** 色阶，默认蓝色系 */
  ramp?: string[]
  /** 数值单位后缀（悬浮提示用，如 "分钟"） */
  unit?: string
  /** 数值格式化（悬浮提示用） */
  formatValue?: (v: number) => string
  /** 零值日的悬浮文案 */
  emptyLabel?: string
  className?: string
  /** 显示的周数上限（超出则只保留最近 N 周），默认 53（约一年） */
  maxWeeks?: number
  /**
   * 布局方向：
   * - 'vertical'（默认，GitHub 原版）：列 = 周、行 = 星期，适合长范围（≥ 数周）；
   * - 'horizontal'：行 = 周、列 = 星期，适合短范围（1–2 周）——
   *   纵向布局在只有 1–2 列时会缩成一条，横向铺开才看得清。
   * 'auto' 按周数自动选择（≤ 3 周用横向）。
   */
  layout?: 'vertical' | 'horizontal' | 'auto'
}

/** 每格默认 11px；容器宽时放大（上限见 maxCellSize） */
const MIN_CELL = 11
/** 纵向格子上限：两张热力并排（各约 490px）放 13 周时需 ~32px 才不显得空 */
const DEFAULT_MAX_CELL = 32
const LABEL_W = 22 // 星期标签列宽 + 间距

/**
 * 按天序列 → GitHub 贡献图。
 * 自动补全区间内的空缺日（空值渲染为最浅色）。
 * vertical：列=周、行=星期；horizontal：行=周、列=星期（短范围用）。
 */
export function GitHubHeatmap({
  days,
  cellSize,
  maxCellSize = DEFAULT_MAX_CELL,
  gap = 3,
  ramp = HEAT_RAMP_BLUE,
  unit = '',
  formatValue,
  emptyLabel = '无记录',
  className,
  maxWeeks = 53,
  layout = 'auto',
}: GitHubHeatmapProps) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const [availW, setAvailW] = useState(0)
  /* 悬浮锚点：挂在组件根（容器有 overflow-x-auto 时行内定位会被裁） */
  const [anchor, setAnchor] = useState<{ rect: DOMRect; text: string; color: string } | null>(null)

  /* 容器宽度 → 自适应格径 */
  useLayoutEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const update = () => setAvailW(el.clientWidth)
    update()
    const ro = new ResizeObserver(update)
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const model = useMemo(() => {
    const byDate = new Map<string, HeatDay>()
    for (const d of days) byDate.set(d.date, d)
    const thresholds = quantileThresholds(days.map((d) => d.value))

    if (days.length === 0) {
      return { weeks: [] as (HeatDay | null)[][], monthMarks: [] as { col: number; label: string }[], thresholds, total: 0 }
    }

    const sorted = days.map((d) => d.date).sort()
    const first = parseLocalDate(sorted[0])
    const last = parseLocalDate(sorted[sorted.length - 1])

    // 起点回退到所在周的周一
    const start = new Date(first)
    start.setDate(start.getDate() - mondayIndex(start))

    const cells: (HeatDay | null)[] = []
    const cursor = new Date(start)
    while (cursor <= last) {
      const key = dateKey(cursor)
      const hit = byDate.get(key)
      cells.push(hit ?? (cursor >= first ? { date: key, value: 0 } : null))
      cursor.setDate(cursor.getDate() + 1)
    }

    // 切成周列
    const weeks: (HeatDay | null)[][] = []
    for (let i = 0; i < cells.length; i += 7) weeks.push(cells.slice(i, i + 7))
    const trimmed = weeks.length > maxWeeks ? weeks.slice(-maxWeeks) : weeks

    // 月份标签：某列首日进入新月份即标注
    const monthMarks: { col: number; label: string }[] = []
    let lastMonth = -1
    trimmed.forEach((week, col) => {
      const firstDay = week.find(Boolean)
      if (!firstDay) return
      const m = parseLocalDate(firstDay.date).getMonth()
      if (m !== lastMonth) {
        // 同月标签至少间隔 3 列，避免拥挤
        const prev = monthMarks[monthMarks.length - 1]
        if (!prev || col - prev.col >= 3) monthMarks.push({ col, label: `${m + 1}月` })
        lastMonth = m
      }
    })

    return { weeks: trimmed, monthMarks, thresholds, total: days.reduce((a, d) => a + d.value, 0) }
  }, [days, maxWeeks])

  /*
   * 布局选择：周数少（≤ 6 周，约 1–1.5 个月）时改用横向——
   * 纵向布局在这种长度下只有 1–6 列，会缩成一条竖线，横向铺开才看得清。
   */
  const resolved = layout === 'auto' ? (model.weeks.length <= 6 ? 'horizontal' : 'vertical') : layout
  const weeks = model.weeks.length

  /*
   * 格径自适应：把可用宽度尽量用满。
   * 纵向列多（周数）时上限 26px，避免格子过大反而不像贡献图；
   * 横向列固定 7，可放大到接近日历格（含日数标签）。
   */
  const cols = resolved === 'horizontal' ? 7 : weeks
  const labelW = resolved === 'horizontal' ? 34 : LABEL_W
  const cap = resolved === 'horizontal' ? Math.max(maxCellSize, 56) : maxCellSize
  const fitted = availW > 0 ? Math.floor((availW - labelW - Math.max(0, cols - 1) * gap) / Math.max(1, cols)) : MIN_CELL
  const size = Math.max(cellSize ?? MIN_CELL, Math.min(fitted, cap))
  const totalW = cols * (size + gap) - gap
  /* 格子足够大时直接把日数写进格内（短范围可读性大幅提升） */
  const showDayNumber = size >= 34

  const fmt = formatValue ?? ((v: number) => v.toLocaleString())

  const cellTitle = (cell: HeatDay): string => {
    if (cell.value > 0) {
      const d = parseLocalDate(cell.date)
      return `${cell.date} 周${WEEKDAY_LABELS[mondayIndex(d)]} · ${fmt(cell.value)}${unit}${cell.detail ? ` · ${cell.detail}` : ''}`
    }
    return `${cell.date} · ${emptyLabel}`
  }

  const renderCell = (cell: HeatDay | null, key: number) => {
    if (!cell) return <div key={key} style={{ width: size, height: size }} aria-hidden />
    const level = levelFor(cell.value, model.thresholds)
    const day = parseLocalDate(cell.date).getDate()
    return (
      <button
        key={key}
        type="button"
        className={cn(
          'relative flex items-center justify-center rounded-[2px] transition-[transform,box-shadow] duration-100',
          'hover:z-10 hover:scale-110 hover:shadow-md focus:z-10 focus:outline-none focus-visible:outline-2 focus-visible:outline-primary-ring',
        )}
        style={{ width: size, height: size, backgroundColor: ramp[level] }}
        onMouseEnter={(e) => setAnchor({ rect: e.currentTarget.getBoundingClientRect(), text: cellTitle(cell), color: ramp[level] })}
        onFocus={(e) => setAnchor({ rect: e.currentTarget.getBoundingClientRect(), text: cellTitle(cell), color: ramp[level] })}
        onBlur={() => setAnchor(null)}
        aria-label={cellTitle(cell)}
      >
        {showDayNumber && (
          <span className={cn('tnum text-[11px] leading-none', level >= 3 ? 'text-white' : 'text-text-2')} aria-hidden>
            {day}
          </span>
        )}
      </button>
    )
  }

  if (model.weeks.length === 0) {
    return <p className={cn('py-4 text-center text-[13px] text-text-4', className)}>暂无数据</p>
  }

  return (
    <div
      ref={wrapRef}
      className={cn('w-full', totalW > availW ? 'overflow-x-auto' : 'overflow-x-hidden', className)}
      onMouseLeave={() => setAnchor(null)}
    >
      {/* 放得下时居中，避免单侧大片留白 */}
      <div className={cn('inline-block min-w-full', totalW <= availW && 'flex justify-center')}>
        <div className="inline-block">
          {resolved === 'horizontal' ? (
            <>
              {/* 列头：星期 */}
              <div className="flex" style={{ gap, marginLeft: labelW }}>
                {WEEKDAY_LABELS.map((w) => (
                  <span key={w} className="text-center text-[10px] text-text-4" style={{ width: size }}>
                    {w}
                  </span>
                ))}
              </div>
              {/* 行 = 周 */}
              <div className="mt-1">
                {model.weeks.map((week, wi) => {
                  const firstDay = week.find(Boolean)
                  const label = firstDay ? firstDay.date.slice(5).replace('-', '/') : ''
                  return (
                    <div key={wi} className="flex items-center" style={{ gap, marginBottom: gap }}>
                      <span className="tnum shrink-0 pr-1 text-right text-[10px] text-text-4" style={{ width: labelW - 4 }}>
                        {label}
                      </span>
                      {Array.from({ length: 7 }, (_, di) => renderCell(week[di] ?? null, di))}
                    </div>
                  )
                })}
              </div>
            </>
          ) : (
            <>
              {/* 月份标签 */}
              <div className="relative mb-1 h-3.5" style={{ width: totalW, marginLeft: LABEL_W }}>
                {model.monthMarks.map((mk) => (
                  <span
                    key={`${mk.col}-${mk.label}`}
                    className="absolute text-[10px] text-text-4"
                    style={{ left: mk.col * (size + gap) }}
                  >
                    {mk.label}
                  </span>
                ))}
              </div>

              <div className="flex gap-1.5">
                {/* 星期标签（只标周一/三/五，与 GitHub 一致） */}
                <div className="flex shrink-0 flex-col" style={{ gap }}>
                  {WEEKDAY_LABELS.map((w, i) => (
                    <span
                      key={w}
                      className="text-[9px] leading-none text-text-4"
                      style={{ height: size, width: 16, lineHeight: `${size}px` }}
                    >
                      {i % 2 === 0 ? w : ''}
                    </span>
                  ))}
                </div>

                {/* 周列 */}
                <div className="flex" style={{ gap }}>
                  {model.weeks.map((week, col) => (
                    <div key={col} className="flex flex-col" style={{ gap }}>
                      {Array.from({ length: 7 }, (_, row) => renderCell(week[row] ?? null, row))}
                    </div>
                  ))}
                </div>
              </div>
            </>
          )}

          {/* 图例 */}
          <div className="mt-2 flex items-center justify-end gap-1 text-[10px] text-text-4">
            <span>少</span>
            {ramp.map((c) => (
              <span key={c} className="rounded-[2px]" style={{ width: 10, height: 10, backgroundColor: c }} />
            ))}
            <span>多</span>
          </div>
        </div>
      </div>

      {anchor && (
        <FloatingTooltip anchor={anchor.rect} maxWidth={360}>
          <span className="flex items-center gap-1.5">
            <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: anchor.color }} aria-hidden />
            <span className="font-medium">{anchor.text}</span>
          </span>
        </FloatingTooltip>
      )}
    </div>
  )
}

export interface HeatMatrixProps {
  /** 矩阵：rows × cols，每格为数值 */
  values: number[][]
  rowLabels: string[]
  colLabels: string[]
  cellSize?: number
  gap?: number
  ramp?: string[]
  unit?: string
  /** 悬浮提示：行/列/值 → 文本 */
  formatCell?: (row: number, col: number, value: number) => string
  className?: string
}

/**
 * 「行 × 列」矩阵热力（PC 网格接口的 day/month/year 维度返回的就是这种形态）。
 * 同样采用 GitHub 的方块 + 5 档色阶视觉，避免 ECharts heatmap 的默认观感。
 */
export function HeatMatrix({
  values,
  rowLabels,
  colLabels,
  cellSize = 11,
  gap = 3,
  ramp = HEAT_RAMP_BLUE,
  unit = '',
  formatCell,
  className,
}: HeatMatrixProps) {
  const thresholds = useMemo(() => quantileThresholds(values.flat()), [values])
  if (values.length === 0) {
    return <p className={cn('py-4 text-center text-[13px] text-text-4', className)}>暂无数据</p>
  }
  return (
    <div className={cn('w-full overflow-x-auto', className)}>
      <div className="inline-block">
        {/* 列标签 */}
        <div className="mb-1 flex" style={{ gap, marginLeft: 30 }}>
          {colLabels.map((c) => (
            <span key={c} className="text-center text-[9px] text-text-4" style={{ width: cellSize }}>
              {c}
            </span>
          ))}
        </div>
        {values.map((row, ri) => (
          <div key={ri} className="flex items-center" style={{ gap, marginBottom: gap }}>
            <span className="shrink-0 pr-1 text-right text-[9px] text-text-4" style={{ width: 26 }}>
              {rowLabels[ri] ?? ''}
            </span>
            {row.map((v, ci) => {
              const level = levelFor(v, thresholds)
              return (
                <div
                  key={ci}
                  title={formatCell ? formatCell(ri, ci, v) : `${rowLabels[ri] ?? ''} ${colLabels[ci] ?? ''}：${v.toLocaleString()}${unit}`}
                  className="rounded-[2px] transition-[transform,box-shadow] duration-100 hover:scale-125 hover:shadow-sm"
                  style={{ width: cellSize, height: cellSize, backgroundColor: ramp[level] }}
                />
              )
            })}
          </div>
        ))}
        <div className="mt-2 flex items-center justify-end gap-1 text-[10px] text-text-4">
          <span>少</span>
          {ramp.map((c) => (
            <span key={c} className="rounded-[2px]" style={{ width: 10, height: 10, backgroundColor: c }} />
          ))}
          <span>多</span>
        </div>
      </div>
    </div>
  )
}
