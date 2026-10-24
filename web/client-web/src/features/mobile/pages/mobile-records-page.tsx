import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { ChevronRight, RefreshCw, Smartphone } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import {
  useLiveness,
  useMobileCharts,
  useMobileHeatmap,
  useMobileOverview,
  useTimelineBlocks,
} from '../queries'
import { mobileApi } from '../api'
import { todayBusinessDay, businessDayShift, businessDayRange } from '@/lib/businessDay'
import { LIFE_CATEGORY_COLOR } from '@/lib/enums'
import { formatDuration, formatTime } from '@/lib/datetime'
import { EChartsBox, asTooltipItem, chartTooltip, isTooltipList, resolveCssColors } from '@/components/viz/echarts-box'
import { GitHubHeatmap, HEAT_RAMP_GREEN } from '@/components/viz/github-heatmap'
import { Button, Card, CardTitle, Chip, EmptyState, Input, MetricCard, PageHeader, Segmented, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

/**
 * 时间范围：预设天数或自定义起止（业务日口径，转 UTC 窗口参数）。
 * custom 为 null 时按 days 计算；否则用自定义的 start/end 业务日。
 */
function useRangeParams(days: number, custom: { start: string; end: string } | null): Record<string, string> {
  return useMemo(() => {
    const end = custom?.end ?? todayBusinessDay()
    const start = custom?.start ?? businessDayShift(end, -(days - 1))
    return {
      rangeStartUtc: businessDayRange(start).startUtc,
      rangeEndUtc: businessDayRange(end).endUtc,
    }
  }, [days, custom])
}

type View = 'usage' | 'liveness'
type RangeKey = 1 | 7 | 30

function lifeColor(cat: string | null): string {
  const raw = LIFE_CATEGORY_COLOR[cat as keyof typeof LIFE_CATEGORY_COLOR] ?? 'var(--color-cat-other)'
  // ECharts canvas 不解析 var()，取实际色值
  return resolveCssColors({ c: raw }).c
}

const RANGE_OPTIONS: { value: RangeKey; label: string }[] = [
  { value: 1, label: '今天' },
  { value: 7, label: '7 天' },
  { value: 30, label: '30 天' },
]

/** 时间范围选择条：预设天数 + 自定义起止（自定义优先） */
function RangePicker({
  days,
  setDays,
  custom,
  setCustom,
  onAfterChange,
}: {
  days: RangeKey
  setDays: (d: RangeKey) => void
  custom: { start: string; end: string } | null
  setCustom: (c: { start: string; end: string } | null) => void
  onAfterChange?: () => void
}) {
  const today = todayBusinessDay()
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {RANGE_OPTIONS.map((r) => (
        <Chip key={r.value} active={custom == null && days === r.value} onClick={() => { setDays(r.value); onAfterChange?.() }}>
          {r.label}
        </Chip>
      ))}
      <span className="mx-1 text-text-4">|</span>
      <Input
        type="date"
        aria-label="起始业务日"
        className="h-7 w-[138px] text-[13px]"
        value={custom?.start ?? businessDayShift(today, -(days - 1))}
        max={custom?.end ?? today}
        onChange={(e) => {
          if (!e.target.value) return
          setCustom({ start: e.target.value, end: custom?.end ?? today })
          onAfterChange?.()
        }}
      />
      <span className="text-text-3">→</span>
      <Input
        type="date"
        aria-label="结束业务日"
        className="h-7 w-[138px] text-[13px]"
        value={custom?.end ?? today}
        min={custom?.start ?? businessDayShift(today, -(days - 1))}
        max={today}
        onChange={(e) => {
          if (!e.target.value) return
          setCustom({ start: custom?.start ?? businessDayShift(today, -(days - 1)), end: e.target.value })
          onAfterChange?.()
        }}
      />
      {custom != null && (
        <Chip onClick={() => { setCustom(null); onAfterChange?.() }}>清除自定义</Chip>
      )}
    </div>
  )
}

/** 手机记录（02 §mobile-records：使用记录 / 设备存活双子视图，?view=liveness） */
export function MobileRecordsPage() {
  const [params, setParams] = useSearchParams()
  const view: View = params.get('view') === 'liveness' ? 'liveness' : 'usage'
  const [days, setDays] = useState<RangeKey>(7)
  /* 自定义范围：设置后优先于预设天数（null = 用预设） */
  const [custom, setCustom] = useState<{ start: string; end: string } | null>(null)
  const range = useRangeParams(days, custom)

  function setView(v: View) {
    setParams(v === 'liveness' ? { view: 'liveness' } : {})
  }

  return (
    <div>
      <PageHeader
        title="手机记录"
        subtitle="Android 使用分析（业务日 04:00 起算）"
        actions={<Segmented value={view} onValueChange={(v) => setView(v as View)} options={[
          { value: 'usage', label: '使用记录' },
          { value: 'liveness', label: '设备存活' },
        ]} />}
      />

      {view === 'usage' ? (
        <UsageView
          days={days}
          setDays={(d) => { setDays(d); setCustom(null) }}
          custom={custom}
          setCustom={setCustom}
          range={range}
        />
      ) : (
        <LivenessView custom={custom} setCustom={setCustom} range={range} days={days} setDays={(d) => { setDays(d); setCustom(null) }} />
      )}
    </div>
  )
}

/* ── 使用记录 ─────────────────────────────────────────────── */

function UsageView({
  days,
  setDays,
  custom,
  setCustom,
  range,
}: {
  days: RangeKey
  setDays: (d: RangeKey) => void
  custom: { start: string; end: string } | null
  setCustom: (c: { start: string; end: string } | null) => void
  range: Record<string, string>
}) {
  const qc = useQueryClient()
  const [force, setForce] = useState(false)
  const [page, setPage] = useState(1)
  const [expanded, setExpanded] = useState<string | null>(null)

  const forceParam = force ? { force: true } : {}
  const overview = useMobileOverview({ ...range, ...forceParam })
  const heatmap = useMobileHeatmap({ ...range, granularity: 'hour', ...forceParam })
  const charts = useMobileCharts({ ...range, ...forceParam })
  const blocks = useTimelineBlocks({ ...range, page, pageSize: 20, ...forceParam })

  const o = overview.data
  const [refreshing, setRefreshing] = useState(false)

  async function forceRefresh() {
    setRefreshing(true)
    setForce(true)
    await qc.invalidateQueries({ queryKey: ['mobile'] })
    setTimeout(() => setForce(false), 800)
    setRefreshing(false)
  }

  /*
   * 热力图：GitHub 贡献图风格（按天聚合的方块日历）。
   * 原实现是「小时 × 日」矩阵，格数随时长线性增长且观感差；
   * 现按天求和展示，小时级分布交由下方服务端小时分布图承担。
   */
  const heatDays = useMemo(() => {
    const buckets = heatmap.data ?? []
    const byDate = new Map<string, number>()
    for (const b of buckets) {
      byDate.set(b.localDate, (byDate.get(b.localDate) ?? 0) + b.foregroundSeconds / 60)
    }
    return [...byDate.entries()]
      .map(([date, v]) => ({ date, value: Math.round(v) }))
      .sort((a, b) => a.date.localeCompare(b.date))
  }, [heatmap.data])

  /*
   * 图表网格（服务端 8 张预设，按 chartType 分发）。
   * 服务端时长类图表 unit 一律是 "seconds"，原始秒数上轴不可读——
   * 统一换算为分钟（等比缩放不改变饼图角度），tooltip 再给中文时长。
   */
  const chartCards = useMemo(() => {
    return (charts.data ?? []).map((c0) => {
      const toMinutes = c0.unit === 'seconds'
      const c = toMinutes
        ? { ...c0, unit: '分钟', points: c0.points.map((pt) => ({ ...pt, value: Math.round((pt.value / 60) * 10) / 10 })) }
        : c0
      return { c, option: chartToOption(c) }
    })
  }, [charts.data])

  return (
    <div className="space-y-5">
      {/* 筛选行：预设天数 + 自定义范围 */}
      <div className="flex flex-wrap items-center gap-2">
        <RangePicker
          days={days}
          setDays={setDays}
          custom={custom}
          setCustom={setCustom}
          onAfterChange={() => setPage(1)}
        />
        <Chip onClick={() => void forceRefresh()}>
          <RefreshCw className={cn('mr-1 inline size-3', refreshing && 'animate-spin')} aria-hidden /> 强制刷新
        </Chip>
      </div>

      {/* 概览指标条 */}
      {overview.isLoading ? (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-20" />)}</div>
      ) : (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <MetricCard label="使用总时长" value={formatDuration(o?.totalForegroundSeconds ?? 0)} icon={Smartphone} />
          <MetricCard label="日均" value={formatDuration(o?.dailyAverageSeconds ?? 0)} />
          <MetricCard label="应用数" value={o?.appCount ?? 0} />
          <MetricCard
            label="完整度"
            value={`${Math.round((o?.completeness ?? 0) * 100)}%`}
            hint={(o?.quality.fallbackShare ?? 0) > 0.3 ? 'fallback 占比偏高' : undefined}
            hintTone="warn"
          />
        </div>
      )}

      {/* 热力图（GitHub 贡献图风格，按天汇总） */}
      <Card className="p-4">
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>使用热力图</CardTitle>
          <span className="text-xs text-text-4">按天汇总的前台使用分钟数</span>
        </div>
        <div className="mt-3">
          {heatDays.length === 0 ? (
            <EmptyState size="sm" title="暂无使用数据" />
          ) : (
            <GitHubHeatmap days={heatDays} ramp={HEAT_RAMP_GREEN} unit="分钟" />
          )}
        </div>
      </Card>

      {/* 图表网格（服务端定义） */}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        {chartCards.map(({ c, option }) => (
          <Card key={c.key} className="p-4">
            <CardTitle>{c.title}</CardTitle>
            <div className="mt-2">
              {c.points.length === 0 ? (
                <EmptyState size="sm" title="暂无数据" />
              ) : (
                <EChartsBox option={option} height={200} />
              )}
            </div>
          </Card>
        ))}
      </div>

      {/* 时间线下钻（块 → 会话） */}
      <Card className="p-4">
        <div className="flex items-center gap-2">
          <CardTitle>时间线块</CardTitle>
          <div className="ml-auto flex items-center gap-2 text-xs text-text-3">
            <span className="tnum">共 {blocks.data?.totalCount ?? 0} 块</span>
            <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
            <span className="tnum">{page}</span>
            <Button variant="secondary" size="sm" disabled={!blocks.data?.hasMore} onClick={() => setPage((p) => p + 1)}>下一页</Button>
          </div>
        </div>
        <div className="mt-3 space-y-1.5">
          {(blocks.data?.items ?? []).map((b) => (
            <div key={b.id} className="rounded-ctl border border-border">
              <button
                type="button"
                onClick={() => setExpanded(expanded === b.id ? null : b.id)}
                className="flex w-full items-center gap-3 px-3 py-2 text-left outline-none"
              >
                <span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: lifeColor(b.lifeCategory) }} aria-hidden />
                <span className="tnum text-xs text-text-3">{b.localStart}–{b.localEnd}</span>
                <span className="truncate text-[13px] font-medium text-text-1">{b.topApps[0]?.displayName ?? b.lifeCategory}{b.appCount > 1 ? ` 等 ${b.appCount} 个应用` : ''}</span>
                <span className="tnum ml-auto shrink-0 text-xs text-text-3">{formatDuration(b.foregroundSeconds)}</span>
              </button>
              {expanded === b.id && <BlockSessions blockId={b.id} range={range} />}
            </div>
          ))}
          {(blocks.data?.items?.length ?? 0) === 0 && <EmptyState size="sm" title="暂无时间线块" />}
        </div>
      </Card>

      {/* 异常与建议 */}
      {(o?.anomalies?.length ?? 0) > 0 || (o?.suggestions?.length ?? 0) > 0 ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Card className="p-4">
            <CardTitle>异常</CardTitle>
            <div className="mt-2 space-y-1.5">
              {(o?.anomalies ?? []).map((a) => (
                <div key={a.code} className="rounded-ctl bg-warn-soft px-3 py-2 text-[13px]">
                  <b className="text-warn">{a.title}</b>
                  <span className="ml-2 text-text-2">{a.evidence}</span>
                </div>
              ))}
              {(o?.anomalies?.length ?? 0) === 0 && <p className="text-[13px] text-text-4">无异常</p>}
            </div>
          </Card>
          <Card className="p-4">
            <CardTitle>建议</CardTitle>
            <div className="mt-2 space-y-1.5">
              {(o?.suggestions ?? []).map((s) => (
                <div key={s.code} className="rounded-ctl bg-surface px-3 py-2 text-[13px] text-text-2">{s.text}</div>
              ))}
              {(o?.suggestions?.length ?? 0) === 0 && <p className="text-[13px] text-text-4">无建议</p>}
            </div>
          </Card>
        </div>
      ) : null}
    </div>
  )
}

/** 块内会话列表（二级下钻） */
function BlockSessions({ blockId, range }: { blockId: string; range: Record<string, string> }) {
  const [loaded, setLoaded] = useState(false)
  const [sessions, setSessions] = useState<{ id: string; displayName: string; startUtc: string; endUtc: string | null; durationSeconds: number }[] | null>(null)
  /* 三级下钻：块 → 会话 → 原始事件 */
  const [eventSession, setEventSession] = useState<string | null>(null)

  if (!loaded) {
    setLoaded(true)
    void mobileApi
      .blockSessions(blockId, range)
      .then(setSessions)
      .catch(() => setSessions([]))
  }

  return (
    <div className="border-t border-divider bg-surface px-3 py-2">
      {sessions == null ? (
        <Skeleton className="h-8" />
      ) : sessions.length === 0 ? (
        <p className="py-1 text-xs text-text-4">无会话明细</p>
      ) : (
        <ul className="space-y-1">
          {sessions.map((s) => (
            <li key={s.id}>
              <button
                type="button"
                onClick={() => setEventSession(s.id === eventSession ? null : s.id)}
                className="flex w-full items-center gap-2 rounded-ctl px-1 py-0.5 text-left text-xs outline-none hover:bg-surface-2"
                title="点击查看会话原始事件"
              >
                <span className="min-w-0 flex-1 truncate text-text-2">{s.displayName}</span>
                <span className="tnum text-text-4">{formatTime(s.startUtc)}</span>
                <span className="tnum text-text-3">{formatDuration(s.durationSeconds)}</span>
                <ChevronRight className={cn('size-3 shrink-0 text-text-4 transition-transform', s.id === eventSession && 'rotate-90')} aria-hidden />
              </button>
              {s.id === eventSession && <SessionEvents sessionId={s.id} />}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** 分类色序列（服务端未给 lifeCategory 时按序取用，保证多色可辨） */
const CATEGORY_FALLBACK_PALETTE = ['#2563EB', '#16A34A', '#F59E0B', '#8B5CF6', '#EC4899', '#14B8A6', '#F97316', '#64748B']

/** 会话原始事件（三级下钻最内层，规格 mobile.md:915） */
function SessionEvents({ sessionId }: { sessionId: string }) {
  const [events, setEvents] = useState<{ id: string; eventType: string; eventTimeUtc: string; className: string | null }[] | null>(null)
  useEffect(() => {
    let cancelled = false
    void mobileApi
      .sessionEvents(sessionId)
      .then((res) => !cancelled && setEvents(res))
      .catch(() => !cancelled && setEvents([]))
    return () => {
      cancelled = true
    }
  }, [sessionId])

  if (events == null) return <div className="px-2 py-1"><Skeleton className="h-6" /></div>
  if (events.length === 0) return <p className="px-2 py-1 text-[11px] text-text-4">无事件明细</p>
  return (
    <ul className="ml-3 space-y-0.5 border-l border-divider pl-2">
      {events.map((e) => (
        <li key={e.id} className="flex items-center gap-2 text-[11px]">
          <span className="mono text-text-3">{e.eventType}</span>
          <span className="tnum ml-auto text-text-4">{formatTime(e.eventTimeUtc)}</span>
        </li>
      ))}
    </ul>
  )
}

/** 服务端图表 DTO → ECharts option */
/** 服务端预设图表 → ECharts option（统一带 tooltip） */
function chartToOption(c: { chartType: string; unit?: string; points: { label: string; value: number; lifeCategory?: string | null; localDate?: string | null; localHour?: number | null; packageName?: string | null }[] }) {
  const unit = c.unit ?? ''
  if (c.chartType === 'pie' || c.chartType === 'category-share') {
    const hasCategories = c.points.some((p) => p.lifeCategory)
    return {
      tooltip: chartTooltip({
        trigger: 'item',
        formatter: (p) => {
          const it = asTooltipItem(p)
          return `${it.name}<br/><b>${Math.round(Number(it.value))}</b>${unit}${unit === '分钟' ? `（${formatDuration(Number(it.value) * 60)}）` : ''} · ${it.percent ?? 0}%`
        },
      }),
      legend: { bottom: 0, itemWidth: 10, itemHeight: 10, textStyle: { fontSize: 10, color: '#64748B' } },
      series: [{
        type: 'pie',
        radius: ['45%', '70%'],
        center: ['50%', '44%'],
        label: { show: false },
        data: c.points.map((p, i) => ({
          name: p.label,
          value: Math.round(p.value),
          itemStyle: {
            color: hasCategories
              ? lifeColor(p.lifeCategory ?? null)
              : CATEGORY_FALLBACK_PALETTE[i % CATEGORY_FALLBACK_PALETTE.length],
          },
        })),
      }],
    }
  }
  if (c.chartType === 'category-trend') {
    // 「日期 × 生活分类」点集 → 每个分类一条线、x 轴为日期的多系列趋势。
    // 旧实现把分类名当 x 轴类别，17 个点挤在 3-4 个格子里且互相覆盖（视觉上大量 0/缺位）。
    const withDate = c.points.filter((pt) => pt.localDate)
    const dates = [...new Set(withDate.map((pt) => pt.localDate as string))].sort()
    const cats = [...new Set(withDate.map((pt) => pt.lifeCategory ?? pt.label))]
    const byCatDate = new Map<string, number>()
    for (const pt of withDate) {
      byCatDate.set(`${pt.lifeCategory ?? pt.label}|${pt.localDate}`, Math.round(pt.value * 100) / 100)
    }
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        formatter: (p) => {
          const arr = isTooltipList(p) ? p : [p]
          const head = arr[0]?.axisValueLabel ?? ''
          const lines = arr
            .filter((x) => Number(x.value) > 0)
            .map((x) => `${x.marker ?? ''}${x.seriesName}：${formatDuration(Number(x.value) * 60)}`)
          return [head, ...lines].join('<br/>')
        },
      }),
      legend: { bottom: 0, itemWidth: 10, itemHeight: 10, textStyle: { fontSize: 10, color: '#64748B' } },
      grid: { left: 46, right: 12, top: 10, bottom: 44 },
      xAxis: { type: 'category', data: dates.map((d) => d.slice(5)), axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' }, name: unit, nameTextStyle: { fontSize: 9, color: '#94A3B8' } },
      series: cats.map((cat) => ({
        name: cat,
        type: 'line' as const,
        smooth: true,
        symbolSize: 5,
        itemStyle: { color: lifeColor(cat) },
        emphasis: { focus: 'series' as const },
        data: dates.map((d) => byCatDate.get(`${cat}|${d}`) ?? 0),
      })),
    }
  }
  if (c.chartType === 'daily-total' || c.chartType === 'switch-trend') {
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        formatter: (p) => {
          const it = asTooltipItem(p)
          const v = Math.round(Number(it.value) * 100) / 100
          return `${it.axisValueLabel ?? it.name}<br/><b>${v}</b>${unit}${unit === '分钟' ? `（${formatDuration(v * 60)}）` : ''}`
        },
      }),
      grid: { left: 46, right: 12, top: 10, bottom: 24 },
      xAxis: { type: 'category', data: c.points.map((p) => p.label.slice(5)), axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' }, name: unit, nameTextStyle: { fontSize: 9, color: '#94A3B8' } },
      series: [{ type: 'line', smooth: true, symbolSize: 5, itemStyle: { color: '#2563EB' }, areaStyle: { color: 'rgba(37,99,235,.08)' }, data: c.points.map((p) => Math.round(p.value * 100) / 100) }],
    }
  }
  if (c.chartType === 'hour-distribution') {
    // 竖向柱状：小时在横轴（标签斜排）、分钟在纵轴——24 点横排一屏放下，
    // 替代旧横向条形（曾被 top-10 截断、加高后又有等高空白）
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (p) => {
          const arr = isTooltipList(p) ? p : [p]
          const it = asTooltipItem(p)
          const v = Math.round(Number(it.value) * 100) / 100
          return `${arr[0]?.axisValueLabel ?? it.name}<br/><b>${v}</b>${unit}（${formatDuration(v * 60)}）`
        },
      }),
      grid: { left: 46, right: 12, top: 10, bottom: 44 },
      xAxis: {
        type: 'category',
        data: c.points.map((pt) => pt.label),
        axisLabel: { fontSize: 10, color: '#94A3B8', rotate: 45, interval: 0 },
      },
      yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' }, name: unit, nameTextStyle: { fontSize: 9, color: '#94A3B8' } },
      series: [{ type: 'bar', barWidth: 10, itemStyle: { color: '#3B82F6', borderRadius: [3, 3, 0, 0] }, data: c.points.map((pt) => Math.round(pt.value * 100) / 100) }],
    }
  }
  // bar 类（top-apps 等排行图）：取前 10
  const items = c.points.slice(0, 10).slice().reverse()
  return {
    tooltip: chartTooltip({
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (p) => {
        const it = asTooltipItem(p)
        const v = Math.round(Number(it.value) * 100) / 100
        return `${it.name}<br/><b>${v}</b>${unit}${unit === '分钟' ? `（${formatDuration(v * 60)}）` : ''}`
      },
    }),
    grid: { left: 96, right: 24, top: 6, bottom: 20 },
    xAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' } },
    yAxis: { type: 'category', data: items.map((p) => p.label), axisLabel: { fontSize: 11, color: '#64748B' } },
    series: [{ type: 'bar', barWidth: 12, itemStyle: { color: '#3B82F6', borderRadius: [0, 3, 3, 0] }, data: items.map((p) => Math.round(p.value * 100) / 100) }],
  }
}

/* ── 设备存活 ─────────────────────────────────────────────── */

function LivenessView({
  days,
  setDays,
  custom,
  setCustom,
  range,
}: {
  days: RangeKey
  setDays: (d: RangeKey) => void
  custom: { start: string; end: string } | null
  setCustom: (c: { start: string; end: string } | null) => void
  range: Record<string, string>
}) {
  const { data, isLoading } = useLiveness(range, true)

  const groups = [
    { label: '手机', items: data?.phones ?? [] },
    { label: '平板', items: data?.tablets ?? [] },
    { label: '未分类机型', items: data?.unclassified ?? [] },
  ]

  return (
    <div className="space-y-4">
      <RangePicker days={days} setDays={setDays} custom={custom} setCustom={setCustom} />

      {isLoading ? (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-36" />)}</div>
      ) : (
        groups.map((g) =>
          g.items.length === 0 ? null : (
            <section key={g.label}>
              <h3 className="mb-2 text-xs font-medium text-text-3">{g.label}（{g.items.length}）</h3>
              <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                {g.items.map((d) => (
                  <DeviceLivenessCard key={d.deviceId} device={d} />
                ))}
              </div>
            </section>
          ),
        )
      )}
    </div>
  )
}

function DeviceLivenessCard({ device: d }: { device: { deviceId: string; displayName: string; deviceKindLabel: string; hasData: boolean; conclusion: string; coverageByHour: number | null; longestSilenceMinutes: number; silences: { minutes: number; severityLabel: string }[]; causes: { label: string; count: number }[] } }) {
  return (
    <Card className="p-4">
      <div className="flex items-center gap-2">
        <CardTitle className="truncate">{d.displayName}</CardTitle>
        <StatusBadge tone={d.hasData ? 'ok' : 'neutral'} dot={false} className="ml-auto">{d.deviceKindLabel}</StatusBadge>
      </div>
      {!d.hasData ? (
        <p className="mt-2 text-[13px] text-text-4">无数据/未上报：该区间内没有任何存活证据。</p>
      ) : (
        <>
          <p className="mt-2 text-[13px] text-text-2">{d.conclusion}</p>
          <div className="mt-2 grid grid-cols-3 gap-2 text-center">
            <div className="rounded-ctl bg-surface py-1.5">
              <div className="tnum text-sm font-semibold text-text-1">{d.coverageByHour != null ? `${Math.round(d.coverageByHour * 100)}%` : '—'}</div>
              <div className="text-[10px] text-text-4">按小时覆盖</div>
            </div>
            <div className="rounded-ctl bg-surface py-1.5">
              <div className={cn('tnum text-sm font-semibold', d.longestSilenceMinutes >= 60 ? 'text-crit' : d.longestSilenceMinutes >= 30 ? 'text-warn' : 'text-text-1')}>
                {d.longestSilenceMinutes} 分
              </div>
              <div className="text-[10px] text-text-4">最长静默</div>
            </div>
            <div className="rounded-ctl bg-surface py-1.5">
              <div className="tnum text-sm font-semibold text-text-1">{d.silences.length}</div>
              <div className="text-[10px] text-text-4">静默段</div>
            </div>
          </div>
          {d.causes.length > 0 && (
            <p className="mt-2 text-[11px] text-text-4">
              死因：{d.causes.map((c) => `${c.label}×${c.count}`).join('、')}
            </p>
          )}
        </>
      )}
    </Card>
  )
}
