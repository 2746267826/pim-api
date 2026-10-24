import { useMemo, useState } from 'react'
import { FileText, Sparkles } from 'lucide-react'
import { useGenerateReport, useReports } from '../queries'
import type { ReportArtifact } from '../types'
import { todayBusinessDay } from '@/lib/businessDay'
import { formatTime } from '@/lib/datetime'
import { notifySuccess } from '@/lib/notify'
import { EChartsBox } from '@/components/viz/echarts-box'
import { Button, Card, CardSubtitle, CardTitle, Chip, EmptyState, MetricCard, PageHeader, Segmented, Skeleton, StatusBadge } from '@/components/ui'

type Kind = 'Daily' | 'Weekly' | 'Monthly' | 'Project'

const KIND_OPTIONS: { value: Kind; label: string }[] = [
  { value: 'Daily', label: '日报' },
  { value: 'Weekly', label: '周报' },
  { value: 'Monthly', label: '月报' },
  { value: 'Project', label: '项目报告' },
]

/** 报告页（02 §reports：类型标签 + 统计卡 + 报告面板 + 漏斗/仪表盘内嵌图表） */
export function ReportsPage() {
  const [kind, setKind] = useState<Kind>('Daily')
  const [statusFilter, setStatusFilter] = useState<'all' | 'Active' | 'Archived'>('all')
  const { data: reports = [], isLoading } = useReports()
  const generate = useGenerateReport()
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const filtered = useMemo(
    () => reports.filter((r) => r.kind === kind && (statusFilter === 'all' || r.status === statusFilter)),
    [reports, kind, statusFilter],
  )
  const selected: ReportArtifact | null = filtered.find((r) => r.id === selectedId) ?? filtered[0] ?? null

  const metrics = useMemo(() => {
    if (!selected) return null
    try {
      return JSON.parse(selected.metricsJson) as Record<string, number>
    } catch {
      return null
    }
  }, [selected])

  /* 内嵌展览馆图表：漏斗（任务完成流）+ 仪表盘（完成率） */
  const funnelOption = useMemo(() => {
    const done = Number(metrics?.completedTasks ?? 0)
    const total = Number(metrics?.tasks ?? 0)
    return {
      tooltip: {},
      series: [{
        type: 'funnel',
        left: '10%',
        top: 10,
        bottom: 10,
        label: { color: '#334155', fontSize: 12 },
        data: [
          { name: '任务总数', value: Math.max(total, 1) },
          { name: '已完成', value: done },
        ],
        color: ['#3B82F6', '#16A34A'],
      }],
    }
  }, [metrics])

  const gaugeOption = useMemo(() => {
    const done = Number(metrics?.completedTasks ?? 0)
    const total = Math.max(Number(metrics?.tasks ?? 0), 1)
    const pct = Math.round((done / total) * 100)
    return {
      series: [{
        type: 'gauge',
        startAngle: 220,
        endAngle: -40,
        min: 0,
        max: 100,
        progress: { show: true, width: 12, itemStyle: { color: '#2563EB' } },
        axisLine: { lineStyle: { width: 12, color: [[1, '#F1F5F9']] } },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { show: false },
        pointer: { show: false },
        detail: { valueAnimation: true, fontSize: 26, color: '#0F172A', formatter: '{value}%' },
        data: [{ value: pct }],
      }],
    }
  }, [metrics])

  return (
    <div>
      <PageHeader
        title="报告"
        subtitle="报告生成与浏览"
        actions={
          <Button
            variant="primary"
            size="sm"
            loading={generate.isPending}
            onClick={async () => {
              const r = await generate.mutateAsync({ kind, date: todayBusinessDay() })
              notifySuccess(`已生成 ${KIND_OPTIONS.find((k) => k.value === kind)?.label}`)
              setSelectedId(r.id)
            }}
          >
            <Sparkles className="size-4" aria-hidden /> 生成报告
          </Button>
        }
      />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Segmented value={kind} onValueChange={setKind} options={KIND_OPTIONS} />
        <div className="ml-auto flex gap-1.5">
          {(['all', 'Active', 'Archived'] as const).map((s) => (
            <Chip key={s} active={statusFilter === s} onClick={() => setStatusFilter(s)}>
              {s === 'all' ? '全部' : s === 'Active' ? '已发布' : '已归档'}
            </Chip>
          ))}
        </div>
      </div>

      {/* 统计卡行 */}
      <div className="grid grid-cols-3 gap-3">
        <MetricCard label="报告数（当前类型）" value={filtered.length} icon={FileText} />
        <MetricCard
          label="已完成任务（最新报告）"
          value={metrics?.completedTasks ?? '—'}
          hint={metrics ? `共 ${metrics.tasks} 项` : undefined}
          icon={FileText}
        />
        <MetricCard
          label="待跟进确认"
          value={useMemoPendingCount()}
          icon={FileText}
        />
      </div>

      <div className="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-[360px_minmax(0,1fr)]">
        {/* 报告列表 */}
        <div className="space-y-2">
          {isLoading ? (
            Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-20" />)
          ) : filtered.length === 0 ? (
            <Card>
              <EmptyState size="sm" icon={FileText} title="暂无报告" description="点击右上角生成一份。" />
            </Card>
          ) : (
            filtered.map((r) => (
              <button
                key={r.id}
                type="button"
                onClick={() => setSelectedId(r.id)}
                className={`w-full rounded-card border px-4 py-3 text-left transition-colors outline-none ${
                  selected?.id === r.id ? 'border-primary bg-primary-soft' : 'border-border bg-bg shadow-card hover:border-border-strong'
                }`}
              >
                <div className="flex items-center gap-2">
                  <span className="text-[13px] font-semibold text-text-1">
                    {KIND_OPTIONS.find((k) => k.value === r.kind)?.label} · {r.generatedAt.slice(5, 10).replace('-', '/')}
                  </span>
                  <StatusBadge tone={r.status === 'Active' ? 'ok' : 'neutral'} dot={false} className="ml-auto">
                    {r.status === 'Active' ? '已发布' : '已归档'}
                  </StatusBadge>
                </div>
                <div className="mt-0.5 tnum text-[11px] text-text-4">{formatTime(r.generatedAt)}</div>
              </button>
            ))
          )}
        </div>

        {/* 报告内容面板 */}
        <div className="space-y-4">
          {selected ? (
            <>
              <Card className="p-5">
                <div className="flex flex-wrap items-center gap-2">
                  <CardTitle className="text-base">
                    {KIND_OPTIONS.find((k) => k.value === selected.kind)?.label} · {selected.generatedAt.slice(0, 10)}
                  </CardTitle>
                  <StatusBadge tone="neutral" dot={false} className="ml-auto">{selected.riskLevel}</StatusBadge>
                  <span className="tnum text-xs text-text-4">生成于 {formatTime(selected.generatedAt)}</span>
                </div>
                <div className="mt-3 whitespace-pre-wrap text-[13px] leading-5 text-text-2">
                  {selected.contentMarkdown}
                </div>
              </Card>

              {/* 指标 + 图表 */}
              <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                <Card className="p-4">
                  <CardTitle>关键指标</CardTitle>
                  <div className="mt-2 divide-y divide-divider">
                    {metrics ? (
                      Object.entries(metrics).slice(0, 8).map(([k, v]) => (
                        <div key={k} className="flex items-center justify-between py-1.5 text-[13px]">
                          <span className="text-text-3">{k}</span>
                          <span className="tnum font-medium text-text-1">{String(v)}</span>
                        </div>
                      ))
                    ) : (
                      <EmptyState size="sm" title="无指标数据" />
                    )}
                  </div>
                </Card>
                <Card className="p-4">
                  <CardTitle>任务完成漏斗</CardTitle>
                  <EChartsBox option={funnelOption} height={170} />
                  <CardSubtitle>完成率仪表盘</CardSubtitle>
                  <EChartsBox option={gaugeOption} height={150} />
                </Card>
              </div>
            </>
          ) : (
            <Card>
              <EmptyState icon={FileText} title="选择左侧报告查看内容" />
            </Card>
          )}
        </div>
      </div>
    </div>
  )
}

/** 待跟进确认（占位 0，P3 接确认中心联动） */
function useMemoPendingCount(): number {
  return useMemo(() => 0, [])
}
