import { useMemo, useState } from 'react'
import { Upload } from 'lucide-react'
import { pcApi } from '../api'
import { pcKeys } from '../queries'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DayGanttBars } from '@/components/viz/day-gantt-bars'
import { EChartsBox, asTooltipItem, chartTooltip } from '@/components/viz/echarts-box'
import { todayBusinessDay, businessDayShift } from '@/lib/businessDay'
import { MetricCard, Button, Card, CardTitle, Chip, Dialog, DialogBody, DialogContent, DialogHeader, EmptyState, Input, Label, PageHeader, Segmented, Textarea } from '@/components/ui'
import { notifySuccess } from '@/lib/notify'

const PALETTE = ['#2563EB', '#16A34A', '#F59E0B', '#8B5CF6', '#EC4899', '#14B8A6', '#F97316', '#6366F1', '#84CC16', '#64748B']

type Mode = 'day' | 'range'

/** 浏览器使用（02 §pc-tracker/browser：单日/范围双模式 + 导入弹窗） */
export function BrowserPage() {
  const [mode, setMode] = useState<Mode>('day')
  const [day, setDay] = useState(() => todayBusinessDay())
  const [from, setFrom] = useState(() => businessDayShift(todayBusinessDay(), -6))
  const [to, setTo] = useState(() => todayBusinessDay())
  const [importOpen, setImportOpen] = useState(false)
  const qc = useQueryClient()

  const summaryKey = mode === 'day' ? day : `${from}~${to}`
  const { data: summary } = useQuery({
    queryKey: pcKeys.browserSummary(summaryKey),
    queryFn: () => pcApi.browserSummary(mode === 'day' ? { date: day } : { from, to }),
  })
  const { data: daily } = useQuery({
    queryKey: pcKeys.browserDaily(from, to),
    queryFn: () => pcApi.browserDaily({ from, to }),
    enabled: mode === 'range',
  })
  const { data: timeline } = useQuery({
    queryKey: pcKeys.browserTimeline(day),
    queryFn: () => pcApi.browserTimeline(day),
    enabled: mode === 'day',
  })

  /* 单日时段分布（甘特条） */
  const gantt = useMemo(() => {
    const byHost = new Map<string, { start: number; dur: number }[]>()
    for (const row of timeline ?? []) {
      const list = byHost.get(row.host) ?? []
      list.push({ start: row.startMs, dur: row.durationMs })
      byHost.set(row.host, list)
    }
    const segs: { start: string; end: string; label: string; color: string }[] = []
    let ci = 0
    for (const [host, list] of byHost) {
      const color = PALETTE[ci++ % PALETTE.length]
      for (const seg of list) {
        const s = new Date(seg.start)
        const e = new Date(seg.start + seg.dur)
        segs.push({ start: s.toISOString(), end: e.toISOString(), label: host, color })
      }
    }
    return segs
  }, [timeline])

  /* 范围日趋势（按日总专注柱状） */
  const trendOption = useMemo(() => {
    const rows = daily ?? []
    const byDate = new Map<string, { hours: number; visits: number }>()
    for (const r of rows) {
      const prev = byDate.get(r.date) ?? { hours: 0, visits: 0 }
      byDate.set(r.date, { hours: prev.hours + r.focusMs / 3600_000, visits: prev.visits + r.visitCount })
    }
    const dates = [...byDate.keys()].sort()
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (p) => {
          const it = asTooltipItem(p)
          const d = byDate.get(dates[it.dataIndex ?? 0])
          return `${it.axisValueLabel ?? it.name}<br/>专注 <b>${(d?.hours ?? 0).toFixed(2)}</b> 小时<br/>访问 ${d?.visits ?? 0} 次`
        },
      }),
      grid: { left: 46, right: 12, top: 10, bottom: 24 },
      xAxis: { type: 'category', data: dates.map((d) => d.slice(5)), axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: { type: 'value', name: '小时', nameTextStyle: { fontSize: 9, color: '#94A3B8' }, axisLabel: { fontSize: 10, color: '#94A3B8' } },
      series: [{ type: 'bar', barWidth: 18, itemStyle: { color: '#2563EB', borderRadius: [3, 3, 0, 0] }, data: dates.map((d) => Number((byDate.get(d)?.hours ?? 0).toFixed(2))) }],
    }
  }, [daily])

  /* 站点排行条形 */
  const hostsOption = useMemo(() => {
    const hosts = (summary?.topHosts ?? []).slice(0, 8).slice().reverse()
    return {
      tooltip: chartTooltip({
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (p) => {
          const it = asTooltipItem(p)
          const h = hosts.find((x) => x.host === it.name)
          return `${it.name}<br/>专注 <b>${Math.round(Number(it.value))}</b> 分钟${h ? `<br/>访问 ${h.visitCount} 次` : ''}`
        },
      }),
      grid: { left: 104, right: 24, top: 6, bottom: 20 },
      xAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94A3B8' } },
      yAxis: { type: 'category', data: hosts.map((h) => h.host), axisLabel: { fontSize: 11, color: '#64748B' } },
      series: [{ type: 'bar', barWidth: 12, itemStyle: { color: '#16A34A', borderRadius: [0, 3, 3, 0] }, data: hosts.map((h) => Math.round(h.focusMs / 60000)) }],
    }
  }, [summary])

  const hours = Math.round((summary?.totalFocusMs ?? 0) / 3600_000 * 10) / 10

  return (
    <div className="space-y-5">
      <PageHeader
        title="浏览器使用"
        subtitle="站点级停留分析（Time Tracker 插件 + 守护进程通道）"
        actions={
          <>
            <Segmented
              size="sm"
              value={mode}
              onValueChange={(v) => setMode(v)}
              options={[
                { value: 'day', label: '单日' },
                { value: 'range', label: '范围' },
              ]}
            />
            <Button variant="secondary" size="sm" onClick={() => setImportOpen(true)}>
              <Upload className="size-4" aria-hidden /> 导入历史
            </Button>
          </>
        }
      />

      {/* 日期控制 */}
      {mode === 'day' ? (
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" onClick={() => setDay(businessDayShift(day, -1))}>←</Button>
          <Chip active className="tnum">{day}</Chip>
          <Button variant="ghost" size="sm" onClick={() => setDay(businessDayShift(day, 1))}>→</Button>
          <Chip onClick={() => setDay(todayBusinessDay())}>今天</Chip>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <Chip active={from === businessDayShift(todayBusinessDay(), -6)} onClick={() => { setFrom(businessDayShift(todayBusinessDay(), -6)); setTo(todayBusinessDay()) }}>近 7 天</Chip>
          <Chip active={from === businessDayShift(todayBusinessDay(), -29)} onClick={() => { setFrom(businessDayShift(todayBusinessDay(), -29)); setTo(todayBusinessDay()) }}>近 30 天</Chip>
          <Input type="date" className="w-40" value={from} onChange={(e) => setFrom(e.target.value)} />
          <span className="text-text-3">→</span>
          <Input type="date" className="w-40" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      )}

      {/* 摘要卡条 */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <MetricCard label="总专注时长" value={`${hours}h`} />
        <MetricCard label="总访问次数" value={(summary?.totalVisits ?? 0).toLocaleString()} />
        <MetricCard label="站点数" value={summary?.siteCount ?? 0} />
        <MetricCard label="媒体播放" value={`${Math.round((summary?.totalMediaMs ?? 0) / 60000)} 分钟`} />
      </div>

      {/* 图表区 */}
      {mode === 'day' ? (
        <Card className="p-4">
          <CardTitle>各域名时段分布</CardTitle>
          <div className="mt-3">
            {gantt.length === 0 ? (
              <EmptyState size="sm" title="当日暂无站点停留数据" />
            ) : (
              <DayGanttBars segments={gantt} height={30} />
            )}
          </div>
        </Card>
      ) : (
        <Card className="p-4">
          <CardTitle>专注时长日趋势</CardTitle>
          <div className="mt-3">
            <EChartsBox option={trendOption} height={230} />
          </div>
        </Card>
      )}

      <Card className="p-4">
        <CardTitle>站点排行（专注分钟）</CardTitle>
        <div className="mt-3">
          {(summary?.topHosts?.length ?? 0) === 0 ? (
            <EmptyState size="sm" title="暂无站点数据" />
          ) : (
            <EChartsBox option={hostsOption} height={220} />
          )}
        </div>
      </Card>

      <ImportDialog open={importOpen} onClose={() => { setImportOpen(false); void qc.invalidateQueries({ queryKey: ['pc', 'browser'] }) }} />
    </div>
  )
}

function ImportDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [content, setContent] = useState('')
  const [mode, setMode] = useState<'overwrite' | 'add'>('overwrite')
  const qc = useQueryClient()

  return (
    <Dialog open={open} onOpenChange={onClose}>
      <DialogContent className="max-w-[520px]">
        <DialogHeader title="导入历史站点数据" description="支持 tt4b 备份 markdown 或记录页导出 JSON" />
        <DialogBody className="space-y-3">
          <div>
            <Label>模式</Label>
            <Segmented
              value={mode}
              onValueChange={(v) => setMode(v)}
              options={[
                { value: 'overwrite', label: '覆盖' },
                { value: 'add', label: '累加' },
              ]}
            />
          </div>
          <Textarea rows={6} className="mono text-xs" placeholder="粘贴文件内容…" value={content} onChange={(e) => setContent(e.target.value)} />
        </DialogBody>
        <Button
          variant="primary"
          className="absolute bottom-4 right-4"
          disabled={!content.trim()}
          onClick={async () => {
            const res = await pcApi.browserImport({ content, mode })
            notifySuccess(`导入 ${res.rows} 行 · ${res.dates} 天 · ${res.hosts} 站点`)
            qc.invalidateQueries({ queryKey: ['pc', 'browser'] })
            setContent('')
            onClose()
          }}
        >
          导入
        </Button>
      </DialogContent>
    </Dialog>
  )
}
