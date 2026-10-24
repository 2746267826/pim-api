import { useMemo, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { EChartsBox, asTooltipItem, chartTooltip, isTooltipList } from '@/components/viz/echarts-box'
import { MouseHeatmap } from '@/components/viz/mouse-heatmap'
import { GitHubHeatmap, HEAT_RAMP_BLUE, HEAT_RAMP_ORANGE } from '@/components/viz/github-heatmap'
import { KeyboardMatrix } from '@/components/viz/keyboard-matrix'
import { CountUp } from '@/components/motion/primitives'
import { CategoryTimeline } from '../components/category-timeline'
import {
  useActivityAnalysis,
  useAppUsage,
  useCategoryDictionary,
  useCategoryDistribution,
  useFocusBlocks,
  useHeatmapGrid,
  useLabelMutation,
  useLabelingQueue,
  useLateNight,
  usePcSummary,
  useProductivity,
  useSuggestionActions,
  useKeystatsRange,
  useContextSuggestions,
} from '../queries'
import { todayBusinessDay, businessDayShift } from '@/lib/businessDay'
import { buildHourlyHeatRows } from '../components/hourly-heat'

import { Button, Card, CardTitle, Chip, EmptyState, Input, PageHeader, Segmented, Skeleton, StatusBadge, Switch } from '@/components/ui'
import { cn } from '@/lib/utils'
import { notifyError, notifySuccess } from '@/lib/notify'

type Mode = 'day' | 'range'

/** 服务端强度档（0–5）→ 颜色：0 档为空底，1–5 由浅到深 */
const INTENSITY_RAMP = ['#E2E8F0', '#DBEAFE', '#93C5FD', '#60A5FA', '#3B82F6', '#1D4ED8']

/*
 * 页面结构（大统计与详细分离）：
 * - 单日 = 详细视图：时间线、逐小时热力、生产力等「天粒度明细」，全部走 date 类接口；
 * - 范围 = 大统计：活动热力、时长分布、深夜趋势等「跨天宏观」，全部走 start/end 聚合接口。
 * 不再做「时/日/月/年」维度切换：该选择器与范围语义重叠、且 hour 维度只返回起始日
 * 24 桶（会把热力图打坏），已移除。
 */

/** PC 记录（02 §pc-tracker：分析驾驶舱主页面） */
export function PcTrackerPage() {
  /* 时间范围：单日可选任意业务日；范围模式作用于支持 start/end 的聚合接口 */
  const [mode, setMode] = useState<Mode>('day')
  const [date, setDate] = useState(() => todayBusinessDay())
  const [range, setRange] = useState(() => ({
    start: businessDayShift(todayBusinessDay(), -6),
    end: todayBusinessDay(),
  }))
  /* 强制刷新：穿透服务端聚合缓存（force=true，规格 04 §7） */
  const [force, setForce] = useState(false)
  /* 时间块热力：是否显示全部 24 小时（默认仅显示有活跃的小时） */
  const [showAllHours, setShowAllHours] = useState(false)

  const scope = mode === 'day' ? { date } : { start: range.start, end: range.end }
  const summary = usePcSummary(date, force, mode === 'day')
  const activity = useActivityAnalysis(date, mode === 'day', force)
  const appUsage = useAppUsage(scope)
  const categories = useCategoryDistribution(scope)
  const productivity = useProductivity(date, mode === 'day', force)
  const focusBlocks = useFocusBlocks(scope)
  const lateNight = useLateNight(scope)
  const { data: suggestions = [] } = useContextSuggestions(date)
  /* 键鼠范围聚合（GAP-1 已提供）：范围模式下键鼠热力的数据源 */
  const keystatsRange = useKeystatsRange(range.start, range.end, mode === 'range', force)

  /* 活动热力（仅范围模式）：按天网格；dimension 固定 day（后端 hour 维度只返回起始日） */
  const heatGrid = useHeatmapGrid(range.start, range.end, 'day', mode === 'range', force)

  /*
   * 时间块热力的逐小时数据（合并逻辑见 hourly-heat.ts）：
   * 条形取 summary.heatmap（覆盖完整，输入口径），待分类/主要应用取 activity-analysis（仅它提供），
   * 时间线覆盖段拆自 summary.timeline（窗口事件口径），按 +08:00 本地小时对齐。
   * 时刻换算不使用浏览器本地时区。
   */
  const hourlyRows = useMemo(
    () => buildHourlyHeatRows(summary.data?.heatmap, activity.data?.blocks, summary.data?.timeline),
    [summary.data, activity.data],
  )

  /* 环形：分类占比 */
  const pieOption = useMemo(() => {
    const items = categories.data?.items ?? []
    return {
      tooltip: chartTooltip({
        trigger: 'item',
        formatter: (p) => {
          const it = asTooltipItem(p)
          return `${it.name}<br/>${Math.round(Number(it.value))} 分钟 · ${it.percent ?? 0}%`
        },
      }),
      series: [{
        type: 'pie',
        radius: ['55%', '80%'],
        label: { show: false },
        data: items.map((c) => ({ name: c.categoryName, value: Math.round(c.minutes), itemStyle: { color: c.color } })),
      }],
    }
  }, [categories.data])

  /* 条形：应用时长 Top */
  const barOption = useMemo(() => {
    const items = (appUsage.data?.items ?? []).slice().reverse()
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (p) => {
          const first = asTooltipItem(p)
          return `${first.name}<br/>${Math.round(Number(first.value))} 分钟`
        },
      }),
      grid: { left: 96, right: 24, top: 6, bottom: 20 },
      xAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: {
        type: 'category',
        data: items.map((i) => i.displayName ?? i.appName),
        axisLabel: { fontSize: 11, color: '#64748B' },
      },
      series: [{ type: 'bar', barWidth: 12, itemStyle: { color: '#3B82F6', borderRadius: [0, 3, 3, 0] }, data: items.map((i) => Math.round(i.totalMinutes)) }],
    }
  }, [appUsage.data])

  /* 生产力周趋势堆叠柱 */
  const trendOption = useMemo(() => {
    const trend = productivity.data?.weeklyTrend ?? []
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (p) => {
          const arr = isTooltipList(p) ? p : [p]
          const head = arr[0]?.axisValueLabel ?? ''
          const rows = arr.map((x) => `${x.marker ?? ''}${x.seriesName}：${Math.round(Number(x.value))} 分钟`).join('<br/>')
          const total = arr.reduce((a, x) => a + Number(x.value ?? 0), 0)
          return `${head}<br/>${rows}<br/><span style="opacity:.7">合计 ${Math.round(total)} 分钟</span>`
        },
      }),
      legend: { bottom: 0, itemWidth: 10, itemHeight: 10, textStyle: { fontSize: 10, color: '#64748B' } },
      grid: { left: 40, right: 12, top: 10, bottom: 30 },
      xAxis: { type: 'category', data: trend.map((d) => d.date.slice(5)), axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' }, name: '分钟', nameTextStyle: { fontSize: 9, color: '#94A3B8' } },
      series: [
        { name: '生产力', type: 'bar', stack: 'm', barWidth: 16, itemStyle: { color: '#16A34A' }, data: trend.map((d) => d.productiveMinutes) },
        { name: '中性', type: 'bar', stack: 'm', itemStyle: { color: '#94A3B8' }, data: trend.map((d) => d.neutralMinutes) },
        { name: '分心', type: 'bar', stack: 'm', itemStyle: { color: '#F97316' }, data: trend.map((d) => d.distractingMinutes) },
      ],
    }
  }, [productivity.data])

  /*
   * 活动热力数据：后端 grid 恒为「周行 × 星期(7)」按天形态（dimension=day）。
   * 数值 = keyPressCount（键盘原始按键计数，颜色深浅用；PC-3 修复后量纲已明确）。
   * 日期归属：day 桶按业务日窗口切分（本地 04:00 起，即 UTC 前一日 20:00），
   * 桶起点的 UTC 日期比业务日早一天，必须 +08:00 偏移后取本地日，否则整图偏移一天。
   */
  const heatCells = useMemo(() => {
    const grid = heatGrid.data?.grid ?? []
    const merged = new Map<string, { date: string; value: number }>()
    for (const row of grid) {
      for (const b of row ?? []) {
        // 优先读服务端 businessDay：业务日 00:00–03:59 的 hour 桶 start 落在次日 UTC，
        // 按 +08:00 推算会归错天（PR #370 后该字段四维度必返）
        const day = 'businessDay' in b && b.businessDay ? b.businessDay : new Date(Date.parse(b.start) + 8 * 3600_000).toISOString().slice(0, 10)
        if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) continue
        const prev = merged.get(day)
        if (prev) prev.value += Math.round(b.keyPressCount)
        else merged.set(day, { date: day, value: Math.round(b.keyPressCount) })
      }
    }
    return [...merged.values()].sort((a, b) => a.date.localeCompare(b.date))
  }, [heatGrid.data])

  /* 有效数据区间：首个/末个按键 >0 的日子（用于解释长范围前段的空白） */
  const heatDataRange = useMemo(() => {
    const active = heatCells.filter((c) => c.value > 0)
    if (active.length === 0) return null
    const first = active[0].date
    const last = active[active.length - 1].date
    return first === last ? first : `${first.slice(5)} ~ ${last.slice(5)}`
  }, [heatCells])

  const m = summary.data?.metrics
  /* 键鼠数据源：单日取 summary.keystats；范围取键鼠范围聚合（字段同构，GAP-1 已提供） */
  const ks = mode === 'day' ? summary.data?.keystats : keystatsRange.data
  /*
   * 活跃时长：按后端口径说明统一引用 summary.heatmap（activeMinutes 含「仅有键盘输入、
   * 没有窗口事件」的分钟，9/27 由 569 → 722.8）；metrics.activeInputDuration 的旧口径
   * 会出现「有效输入 > 记录时长」的矛盾展示，不再使用。
   */
  const activeMinutesTotal = useMemo(
    () => Math.round((summary.data?.heatmap ?? []).reduce((a, b) => a + (b.activeMinutes ?? 0), 0)),
    [summary.data],
  )
  const fmtHm = (min: number) => (min < 60 ? `${min}m` : `${Math.floor(min / 60)}h ${String(min % 60).padStart(2, '0')}m`)

  return (
    <div className="space-y-5">
      <PageHeader
        title="电脑记录"
        subtitle={
          mode === 'day'
            ? `业务日 ${date}（04:00 起算）`
            : `范围 ${range.start} ~ ${range.end}（聚合接口按范围统计）`
        }
        actions={
          <>
            <Button
              variant="secondary"
              size="sm"
              loading={force}
              onClick={() => {
                setForce(true)
                setTimeout(() => setForce(false), 1200)
              }}
              title="穿透服务端聚合缓存重新计算"
            >
              <RefreshCw className="size-4" aria-hidden /> 强制刷新
            </Button>
          </>
        }
      />

      {/* 时间范围控制：单日可任意选日；范围模式可自定义起止 */}
      <Card className="flex flex-wrap items-center gap-2 p-3">
        <Segmented
          size="sm"
          value={mode}
          onValueChange={(v) => setMode(v)}
          options={[
            { value: 'day', label: '单日' },
            { value: 'range', label: '范围' },
          ]}
        />
        {mode === 'day' ? (
          <div className="flex items-center gap-1.5">
            <Button variant="ghost" size="sm" aria-label="前一天" onClick={() => setDate(businessDayShift(date, -1))}>←</Button>
            <Input
              type="date"
              aria-label="业务日"
              className="h-7 w-[136px] text-[13px]"
              value={date}
              max={todayBusinessDay()}
              onChange={(e) => e.target.value && setDate(e.target.value)}
            />
            <Button variant="ghost" size="sm" aria-label="后一天" disabled={date >= todayBusinessDay()} onClick={() => setDate(businessDayShift(date, 1))}>→</Button>
            {date !== todayBusinessDay() && (
              <Chip onClick={() => setDate(todayBusinessDay())}>回到今天</Chip>
            )}
          </div>
        ) : (
          <div className="flex flex-wrap items-center gap-1.5">
            {([['近 7 天', -6], ['近 30 天', -29], ['近 90 天', -89]] as const).map(([label, offset]) => {
              const active = range.start === businessDayShift(todayBusinessDay(), offset) && range.end === todayBusinessDay()
              return (
                <Chip
                  key={label}
                  active={active}
                  onClick={() => setRange({ start: businessDayShift(todayBusinessDay(), offset), end: todayBusinessDay() })}
                >
                  {label}
                </Chip>
              )
            })}
            <Input
              type="date"
              aria-label="范围起始"
              className="h-7 w-[136px] text-[13px]"
              value={range.start}
              max={range.end}
              onChange={(e) => e.target.value && setRange((r) => ({ ...r, start: e.target.value }))}
            />
            <span className="text-text-3">→</span>
            <Input
              type="date"
              aria-label="范围结束"
              className="h-7 w-[136px] text-[13px]"
              value={range.end}
              min={range.start}
              max={todayBusinessDay()}
              onChange={(e) => e.target.value && setRange((r) => ({ ...r, end: e.target.value }))}
            />
          </div>
        )}
        <span className="ml-auto text-[11px] text-text-4">
          {mode === 'day'
            ? '单日模式：详细视图（时间线 / 逐小时热力 / 生产力）'
            : '范围模式：大统计（活动热力 / 时长分布 / 深夜趋势）'}
        </span>
      </Card>

      {/* 今日复盘摘要瓦片（仅单日：summary 接口只接受 date） */}
      {mode === 'day' && (summary.isLoading ? (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">{Array.from({ length: 5 }, (_, i) => <Skeleton key={i} className="h-20" />)}</div>
      ) : (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
          <SummaryTile label="记录时长" value={m?.totalRecordedDuration ?? '—'} />
          <SummaryTile label="活跃时长" value={fmtHm(activeMinutesTotal)} title="按 summary.heatmap 口径：包含仅有键盘输入、没有窗口事件的分钟，故可能高于记录时长" />
          <SummaryTile label="按键总数" value={(ks?.keyPresses ?? 0).toLocaleString()} countValue={ks?.keyPresses} />
          <SummaryTile label="点击总数" value={(ks?.totalClicks ?? 0).toLocaleString()} countValue={ks?.totalClicks} />
          <SummaryTile label="待处理建议" value={String(suggestions.filter((s) => s.status === 'pending').length)} warn={suggestions.some((s) => s.status === 'pending')} />
        </div>
      ))}

      {/* 单日（详细）：专注块 + 时间块热力 */}
      {mode === 'day' && (
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
          <Card className="p-4">
            <CardTitle>专注块</CardTitle>
            <div className="mt-2 space-y-1.5">
              {(focusBlocks.data?.items?.length ?? 0) === 0 ? (
                <EmptyState size="sm" title="当日暂无连续专注时段" />
              ) : (
                focusBlocks.data!.items.slice(0, 6).map((b) => (
                  <div key={b.startUtc} className="flex items-center gap-3 text-[13px]">
                    <span className="tnum shrink-0 text-text-3">{b.startLocal}–{b.endLocal}</span>
                    <span className="min-w-0 flex-1 truncate text-text-1">{b.mainApp}</span>
                    <span className="tnum shrink-0 text-text-3">{b.durationMinutes} 分钟</span>
                  </div>
                ))
              )}
            </div>
          </Card>
          <Card className="p-4">
            <div className="flex flex-wrap items-center gap-2">
              <CardTitle>时间块热力（60 分钟）</CardTitle>
              <span className="ml-auto">
                <Switch checked={showAllHours} onCheckedChange={setShowAllHours} />
              </span>
              <label className="cursor-pointer text-[11px] text-text-4" onClick={() => setShowAllHours((v) => !v)}>
                显示全部 24 小时
              </label>
            </div>
            <div className="mt-3 space-y-1.5">
              {hourlyRows.length === 0 ? (
                <EmptyState size="sm" title="暂无数据" />
              ) : (
                (showAllHours ? hourlyRows : hourlyRows.filter((r) => r.activeMinutes > 0)).map((row) => {
                  const color = INTENSITY_RAMP[Math.min(5, Math.max(1, row.intensity))]
                  const activePct = Math.min(100, (row.activeMinutes / 60) * 100)
                  const coveredPct = Math.min(activePct, (row.coveredMinutes / 60) * 100)
                  return (
                    <div
                      key={row.hour}
                      className="flex items-center gap-2"
                      title={[
                        `${row.label} 活跃 ${row.activeMinutes} 分钟`,
                        row.coveredMinutes > 0 ? `时间线覆盖 ${Math.round(row.coveredMinutes)} 分钟` : null,
                        row.activeMinutes - row.coveredMinutes > 0 ? `仅键鼠输入 ${Math.max(0, Math.round(row.activeMinutes - row.coveredMinutes))} 分钟（窗口事件未采集）` : null,
                        row.totalEvents > 0 ? `事件 ${row.totalEvents}` : null,
                        row.intensity > 0 ? `强度 ${row.intensity}/5` : null,
                        row.pending > 0 ? `待分类 ${row.pending} 条` : null,
                        row.topApp ? `主要：${row.topApp}` : null,
                      ].filter(Boolean).join(' · ')}
                    >
                      <span className="tnum w-10 shrink-0 text-[11px] text-text-4">{row.label}</span>
                      <div className="h-4 flex-1 overflow-hidden rounded-full bg-surface-2">
                        <div className="flex h-full" style={{ width: `${activePct}%` }}>
                          <div className="h-full rounded-l-full" style={{ width: `${coveredPct}%`, backgroundColor: color }} />
                          <div className="h-full rounded-r-full" style={{ width: `${activePct - coveredPct}%`, backgroundColor: color, opacity: 0.32 }} />
                        </div>
                      </div>
                      {row.pending > 0 && (
                        <StatusBadge size="xs" tone="warn" dot={false} className="shrink-0">{row.pending} 待分类</StatusBadge>
                      )}
                    </div>
                  )
                })
              )}
            </div>
            <p className="mt-2 text-[11px] text-text-4">
              条宽 = 该小时输入口径活跃分钟占 60 分钟的比例：深色 = 时间线有窗口记录覆盖，浅色 = 仅有键鼠输入（窗口事件未采集）；颜色 = 服务端强度档（1–5）。
            </p>
          </Card>
        </div>
      )}

      {/*
        分类时间线（每小时一行的时间条图；仅单日有分钟级 timeline）。
        数据直接透传 summary.timeline —— 组件自身不发请求，时刻按 +08:00 墙钟解释。
      */}
      {mode === 'day' && (
        <CategoryTimeline
          timeline={summary.data?.timeline}
          loading={summary.isLoading}
          /*
           * 错误信息必须显式提取：失败时 summary.data 为 undefined，
           * 若只传 data?.timeline，组件会把「请求失败」显示成「暂无数据」。
           */
          error={
            summary.isError
              ? summary.error instanceof Error
                ? summary.error.message
                : '无法获取该业务日的记录'
              : null
          }
          subtitle={`业务日 ${date}（+08:00 墙钟）`}
        />
      )}

      {/* 范围（大统计）：两张热力图并排（窄屏自动堆叠） + 分类时长分布 */}
      {mode === 'range' && (
        <>
          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <Card className="p-4">
              <div className="flex flex-wrap items-center gap-2">
                <CardTitle>活动热力</CardTitle>
                <span className="text-xs text-text-4">每天活动强度</span>
                {heatDataRange && <span className="text-xs text-text-4">· 有效数据 {heatDataRange}</span>}
              </div>
              <div className="mt-3">
                {heatCells.length === 0 ? (
                  <EmptyState size="sm" title="暂无数据" />
                ) : (
                  <GitHubHeatmap
                    days={heatCells}
                    ramp={HEAT_RAMP_BLUE}
                    formatValue={(v) => `按键 ${v.toLocaleString()} 次`}
                    maxWeeks={27}
                  />
                )}
              </div>
            </Card>
            <Card className="p-4">
              <div className="flex flex-wrap items-center gap-2">
                <CardTitle>深夜使用热力</CardTitle>
                <span className="text-xs text-text-4">每天 00:00–04:00 的使用分钟</span>
              </div>
              <div className="mt-3">
                {(lateNight.data?.items?.length ?? 0) === 0 ? (
                  <EmptyState size="sm" title="暂无数据" />
                ) : (
                  <GitHubHeatmap
                    days={(lateNight.data?.items ?? []).map((d) => ({ date: d.date, value: d.minutes }))}
                    ramp={HEAT_RAMP_ORANGE}
                    formatValue={(v) => `深夜 ${v} 分钟`}
                    emptyLabel="无深夜使用"
                  />
                )}
              </div>
            </Card>
          </div>

          <Card className="p-4">
            <CardTitle>分类时长分布</CardTitle>
            <div className="mt-2">
              {(categories.data?.items?.length ?? 0) === 0 ? (
                <EmptyState size="sm" title="暂无分类数据" />
              ) : (
                <EChartsBox option={pieOption} height={220} />
              )}
            </div>
          </Card>
        </>
      )}

      {/* 范围（大统计）：应用时长排行 */}
      {mode === 'range' && (
        <Card className="p-4">
          <CardTitle>应用时长排行</CardTitle>
          <div className="mt-2">
            {(appUsage.data?.items?.length ?? 0) === 0 ? (
              <EmptyState size="sm" title="暂无应用数据" />
            ) : (
              <EChartsBox option={barOption} height={220} />
            )}
          </div>
        </Card>
      )}

      {/* 生产力仪表盘 + 周趋势（仅单日：productivity 接口只接受 date） */}
      {mode === 'day' && (
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[280px_minmax(0,1fr)]">
        <Card className="flex flex-col items-center p-4">
          <CardTitle className="self-start">生产力仪表盘</CardTitle>
          <EChartsBox
            option={{
              tooltip: chartTooltip({
                formatter: () => {
                  const p = productivity.data
                  return `今日生产力评分 <b>${p?.todayScore ?? 0}</b><br/>生产力 ${p?.productiveHours ?? 0}h · 中性 ${p?.neutralHours ?? 0}h · 分心 ${p?.distractingHours ?? 0}h${p?.goalMet ? '<br/>已达成目标' : ''}`
                },
              }),
              series: [{
                type: 'gauge',
                min: 0, max: 100,
                progress: { show: true, width: 14, itemStyle: { color: productivity.data?.goalMet ? '#16A34A' : '#2563EB' } },
                axisLine: { lineStyle: { width: 14, color: [[1, '#F1F5F9']] } },
                axisTick: { show: false }, splitLine: { show: false }, axisLabel: { show: false }, pointer: { show: false },
                detail: { fontSize: 30, color: '#0F172A', formatter: '{value}' },
                data: [{ value: productivity.data?.todayScore ?? 0 }],
              }],
            }}
            height={170}
          />
          <div className="tnum text-xs text-text-3">
            生产力 {productivity.data?.productiveHours ?? 0}h · 分心 {productivity.data?.distractingHours ?? 0}h
          </div>
        </Card>
        <Card className="p-4">
          <CardTitle>近 7 天生产力趋势</CardTitle>
          <div className="mt-2">
            <EChartsBox option={trendOption} height={210} />
          </div>
        </Card>
      </div>
      )}

      {/* 键盘 + 鼠标热力（单日=summary.keystats；范围=keystats 范围聚合，字段同构） */}
      <Card className="p-4">
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>键盘热力图</CardTitle>
          {mode === 'range' && <span className="text-xs text-text-4">{range.start} ~ {range.end}</span>}
          <span className="tnum ml-auto text-xs text-text-4">峰值 KPS {ks?.peakKps ?? 0} · CPS {ks?.peakCps ?? 0}</span>
        </div>
        <div className="mt-3 grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_260px]">
          <KeyboardMatrix keyCounts={ks?.keyPressCounts ?? {}} />
          <MouseHeatmap
            left={ks?.leftClicks ?? 0}
            middle={ks?.middleClicks ?? 0}
            right={ks?.rightClicks ?? 0}
            sideBack={ks?.sideBackClicks ?? 0}
            sideForward={ks?.sideForwardClicks ?? 0}
            scrollDistance={ks?.scrollDistance}
          />
        </div>
      </Card>

      {/* 标注队列 + 上下文建议（仅单日：建议按业务日查询，重算也以该日为影响面） */}
      {mode === 'day' && (
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
          <LabelingQueue limit={5} />
          <ContextConfirmationPanel
            suggestions={suggestions.filter((s) => s.status === 'pending')}
            date={date}
          />
        </div>
      )}
    </div>
  )
}

function SummaryTile({
  label,
  value,
  warn,
  title,
  countValue,
}: {
  label: string
  value: string
  warn?: boolean
  title?: string
  /** 纯数值时启用数字滚动（首次到位/数据刷新时缓动） */
  countValue?: number
}) {
  return (
    <div className={cn('rounded-card border p-4', warn ? 'border-warn-border bg-warn-soft' : 'border-border bg-bg shadow-card')} title={title}>
      <div className="text-xs text-text-3">{label}</div>
      <div className="tnum mt-1 text-xl font-semibold text-text-1">
        {countValue != null ? <CountUp value={countValue} /> : value}
      </div>
    </div>
  )
}

/** 标注队列（可展开卡：建议分类快捷钮 + 自定义分类） */
export function LabelingQueue({ limit }: { limit: number }) {
  const { data, isLoading } = useLabelingQueue(limit)
  const { data: dictionary = [] } = useCategoryDictionary()
  const label = useLabelMutation()
  const [custom, setCustom] = useState<Record<string, string>>({})

  return (
    <Card className="p-4">
      <div className="flex items-center gap-2">
        <CardTitle>标注队列</CardTitle>
        <StatusBadge tone={data?.items?.length ? 'warn' : 'ok'} className="ml-auto">{data?.items?.length ?? 0}</StatusBadge>
      </div>
      <div className="mt-3 space-y-2">
        {isLoading ? (
          Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-14" />)
        ) : (data?.items?.length ?? 0) === 0 ? (
          <EmptyState size="sm" title="没有待标注的应用/域名" />
        ) : (
          data!.items.map((item) => (
            <div key={item.target} className="rounded-ctl border border-border px-3 py-2.5">
              <div className="flex items-center gap-2">
                <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-text-1">
                  {item.displayName}
                  <span className="ml-1.5 text-[11px] text-text-4">{item.targetType === 'app' ? '应用' : item.targetType === 'domain' ? '域名' : '手机应用'} · {item.minutes} 分钟</span>
                </span>
              </div>
              {item.sampleTitles.length > 0 && (
                <p className="mt-0.5 truncate text-[11px] text-text-4">{item.sampleTitles[0]}</p>
              )}
              <div className="mt-2 flex flex-wrap items-center gap-1.5">
                {dictionary.slice(0, 4).map((d) => (
                  <Chip
                    key={d.id}
                    onClick={() => {
                      label.mutate(
                        { targetType: item.targetType, target: item.target, categoryId: d.id, scope: 'all' },
                        { onSuccess: () => notifySuccess(`已归入「${d.name}」`) },
                      )
                    }}
                  >
                    {d.name}
                  </Chip>
                ))}
                <Input
                  className="h-7 w-24 text-xs"
                  placeholder="自定义分类"
                  value={custom[item.target] ?? ''}
                  onChange={(e) => setCustom((prev) => ({ ...prev, [item.target]: e.target.value }))}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      const name = custom[item.target]?.trim()
                      if (!name) return
                      label.mutate(
                        { targetType: item.targetType, target: item.target, categoryName: name, scope: 'all' },
                        { onSuccess: () => notifySuccess(`已创建并归入「${name}」`), onError: (err) => notifyError(err instanceof Error ? err.message : '标注失败') },
                      )
                    }
                  }}
                />
                {label.isPending && <span className="text-[11px] text-text-4">保存中…</span>}
              </div>
            </div>
          ))
        )}
      </div>
    </Card>
  )
}

/**
 * 上下文确认面板（先预览后应用）。
 *
 * 这个板块的用途：系统对「没有明确分类规则的应用」生成分类建议（基于使用上下文聚类），
 * 用户在此预览采纳后会影响多少记录，确认后写入应用知识库并沉淀为规则——
 * 之后同类应用就能自动分类，不再进入待分类队列。
 *
 * 显示名回退：建议对象可能没有友好名（appDisplayName 为空），
 * 此时从 clusterKey 解析（app:java → java；app:__idle__ → 空闲时段）。
 *
 * 归日（generatedForDate）：date 参数只决定后端扫描哪一天，返回的是全部待处理建议，
 * 每条建议自带其业务日；预览/应用的影响范围必须用建议自己的日期（PC-6 修复后下发），
 * 否则页面选 9.27 时会把影响面错套到 9.04 生成的旧建议上。
 */
export function ContextConfirmationPanel({
  suggestions,
  date,
}: {
  suggestions: { id: string; clusterKey?: string | null; appDisplayName: string | null; sampleCount: number; suggestedCategory: string | null; generatedForDate?: string | null }[]
  date: string
}) {
  const actions = useSuggestionActions()
  const [previewResult, setPreviewResult] = useState<{ id: string; text: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  /** 簇键 → 可读名：__idle__ 是空闲时段哨兵，不是真的「未知应用」 */
  const displayName = (s: { clusterKey?: string | null; appDisplayName: string | null }): string => {
    if (s.appDisplayName) return s.appDisplayName
    const key = s.clusterKey ?? ''
    if (/^app:/i.test(key)) {
      const name = key.slice(4)
      if (/^_+idle_+$/i.test(name)) return '空闲时段'
      if (name) return name
    }
    return '未知应用'
  }

  return (
    <Card className="p-4">
      <div className="flex items-center gap-2">
        <CardTitle>上下文确认</CardTitle>
        <StatusBadge tone={suggestions.length ? 'warn' : 'ok'} className="ml-auto">{suggestions.length}</StatusBadge>
      </div>
      <p className="mt-1 text-[11px] leading-4 text-text-4">
        对暂无分类规则的应用，系统会按使用上下文给出分类建议；确认后会沉淀到应用知识库，
        之后同类应用自动归类。date 参数只决定扫描哪一天，返回的是全部待处理建议——
        预览/应用的影响范围见每条建议自身的业务日。
      </p>
      <div className="mt-3 space-y-2">
        {suggestions.length === 0 ? (
          <EmptyState size="sm" title="没有待确认的分类建议" />
        ) : (
          suggestions.map((s) => {
            /* 影响面 = 建议自己的业务日；generatedForDate 缺失时才退回页面所选日 */
            const impactDate = s.generatedForDate ?? date
            return (
              <div key={s.id} className="rounded-ctl border border-border px-3 py-2.5">
                <div className="flex items-center gap-2 text-[13px]">
                  <span className="min-w-0 flex-1 truncate font-medium text-text-1">{displayName(s)}</span>
                  <span className="tnum text-[11px] text-text-4">业务日 {impactDate}</span>
                  <span className="text-[11px] text-text-4">{s.sampleCount} 条记录</span>
                </div>
                {s.suggestedCategory && (
                  <p className="mt-0.5 text-xs text-text-3">建议分类：{s.suggestedCategory}</p>
                )}
                {error && <p className="mt-1 text-[11px] text-crit">{error}</p>}
                {previewResult?.id === s.id && (
                  <p className="mt-1 rounded-ctl bg-surface px-2 py-1 text-[11px] text-text-3">{previewResult.text}</p>
                )}
                <div className="mt-2 flex gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    loading={actions.preview.isPending}
                    onClick={async () => {
                      setError(null)
                      try {
                        const res = await actions.preview.mutateAsync({ id: s.id, categoryName: s.suggestedCategory ?? undefined, date: impactDate })
                        setPreviewResult({ id: s.id, text: `影响业务日 ${impactDate} 的 ${res.preview.affectedRecordCount} 条记录 / ${Math.round(res.preview.affectedDurationSeconds / 60)} 分钟` })
                      } catch (e) {
                        setError(e instanceof Error ? e.message : '预览失败')
                      }
                    }}
                  >
                    预览影响
                  </Button>
                  <Button
                    variant="primary"
                    size="sm"
                    loading={actions.apply.isPending}
                    onClick={async () => {
                      setError(null)
                      try {
                        await actions.apply.mutateAsync({ id: s.id, categoryName: s.suggestedCategory ?? undefined, date: impactDate })
                        notifySuccess('已应用分类并沉淀知识')
                      } catch (e) {
                        setError(e instanceof Error ? e.message : '应用失败')
                      }
                    }}
                  >
                    应用
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => void actions.reject.mutateAsync(s.id)}>
                    拒绝
                  </Button>
                </div>
              </div>
            )
          })
        )}
      </div>
    </Card>
  )
}
