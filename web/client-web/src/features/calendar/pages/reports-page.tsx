import { useMemo, useState } from 'react'
import { FileText, Sparkles } from 'lucide-react'
import { useDomainProjects, useGenerateReport, useReports } from '../queries'
import { parseReportMetrics, REPORT_METRIC_GROUP_LABELS } from '../report-metrics'
import { ReportMarkdown } from '../components/report-markdown'
import type { ReportArtifact } from '../types'
import { todayBusinessDay } from '@/lib/businessDay'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { EChartsBox } from '@/components/viz/echarts-box'
import { Button, Card, CardSubtitle, CardTitle, Chip, EmptyState, InlineAlert, MetricCard, PageHeader, Segmented, Select, Skeleton, StatusBadge } from '@/components/ui'

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
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null)
  const reportsQuery = useReports()
  const reports = useMemo(() => reportsQuery.data ?? [], [reportsQuery.data])
  const isLoading = reportsQuery.isLoading
  const projectsQuery = useDomainProjects()
  const generate = useGenerateReport()
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const projects = useMemo(() => projectsQuery.data ?? [], [projectsQuery.data])

  /*
   * 项目选择（api#462）：后端对 kind=Project 且缺 projectId 报 02046，前端必须
   * 先选项目才允许生成；切走类型或项目被删时清掉选择，避免拿旧上下文生成。
   */
  /*
   * projectId 只在 Project 上下文有意义；切走类型时在事件里同步清空，
   * 避免回到项目报告时残留旧项目。
   */
  const switchKind = (next: Kind) => {
    setKind(next)
    if (next !== 'Project') setSelectedProjectId(null)
  }
  /* 项目从列表消失时选中值在渲染期收敛为空，生成按钮随之回到必选状态 */
  const projectId = useMemo(
    () => (selectedProjectId != null && projects.some((p) => p.id === selectedProjectId) ? selectedProjectId : null),
    [projects, selectedProjectId],
  )

  const filtered = useMemo(
    () => reports.filter(
      (r) => r.kind === kind
        && (kind !== 'Project' || r.projectId === projectId)
        && (statusFilter === 'all' || r.status === statusFilter),
    ),
    [reports, kind, projectId, statusFilter],
  )
  const selected: ReportArtifact | null = filtered.find((r) => r.id === selectedId) ?? filtered[0] ?? null

  const metrics = useMemo(() => {
    return selected ? parseReportMetrics(selected.metricsJson) : null
  }, [selected])

  const metricGroups = useMemo(() => {
    if (!metrics) return []
    const order = ['plan-vs-actual', 'task-progress', 'calendar', 'collection'] as const
    return order
      .map((group) => ({ group, label: REPORT_METRIC_GROUP_LABELS[group], items: metrics.filter((m) => m.group === group) }))
      .filter((g) => g.items.length > 0)
  }, [metrics])

  /* 内嵌展览馆图表：漏斗（任务完成流）+ 仪表盘（完成率） */
  /* 完成率只在「任务数与完成数都可计算且总数>0」时成立；无数据/0 任务不画假图 */
  const taskMetric = metrics?.find((m) => m.key === 'tasks')?.value ?? null
  const completedMetric = metrics?.find((m) => m.key === 'completedTasks')?.value ?? null
  const completionKnowable = taskMetric !== null && completedMetric !== null && taskMetric > 0

  const funnelOption = useMemo(() => {
    return {
      tooltip: {},
      series: [{
        type: 'funnel',
        left: '10%',
        top: 10,
        bottom: 10,
        label: { color: '#334155', fontSize: 12 },
        data: [
          { name: '任务总数', value: taskMetric ?? 0 },
          { name: '已完成', value: completedMetric ?? 0 },
        ],
        color: ['#3B82F6', '#16A34A'],
      }],
    }
  }, [taskMetric, completedMetric])

  const gaugeOption = useMemo(() => {
    const pct = completionKnowable && taskMetric! > 0 ? Math.round((completedMetric! / taskMetric!) * 100) : null
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
        data: [{ value: pct ?? 0 }],
      }],
    }
  }, [completionKnowable, taskMetric, completedMetric])

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
            disabled={kind === 'Project' && (projectId == null || projectsQuery.isLoading || projectsQuery.isError)}
            onClick={async () => {
              try {
                const r = await generate.mutateAsync({
                  kind,
                  date: todayBusinessDay(),
                  ...(kind === 'Project' && projectId ? { projectId } : {}),
                })
                notifySuccess(`已生成 ${KIND_OPTIONS.find((k) => k.value === kind)?.label}`)
                if (statusFilter === 'Archived') setStatusFilter('all')
                setSelectedId(r.id)
              } catch (error) {
                /* 02046 未选项目 / 02047 项目不存在 / 02048 项目下无任务等按后端口径呈现 */
                notifyError(error instanceof Error && error.message ? error.message : '生成报告失败，请稍后重试。')
              }
            }}
          >
            <Sparkles className="size-4" aria-hidden /> 生成报告
          </Button>
        }
      />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Segmented
          value={kind}
          onValueChange={switchKind}
          options={KIND_OPTIONS.map((o) => ({ ...o, disabled: generate.isPending }))}
        />
        {kind === 'Project' && (
          <div className="flex min-w-64 items-center gap-2">
            {projectsQuery.isError ? (
              <InlineAlert tone="crit" title="项目列表加载失败">
                无法读取项目列表，请刷新重试。
              </InlineAlert>
            ) : projectsQuery.isLoading ? (
              <Skeleton className="h-9 w-56" />
            ) : projects.length === 0 ? (
              <InlineAlert tone="warn" title="还没有项目">
                请先在任务页创建项目，再回到这里生成项目报告。
              </InlineAlert>
            ) : (
              <Select
                ariaLabel="选择项目"
                placeholder="选择项目（必选）"
                value={projectId ?? ''}
                onValueChange={(v) => setSelectedProjectId(v === '' ? null : v)}
                options={projects.map((p) => ({ value: p.id, label: p.name }))}
                disabled={generate.isPending}
              />
            )}
          </div>
        )}
        <div className="ml-auto flex gap-1.5">
          {(['all', 'Active', 'Archived'] as const).map((s) => (
            <Chip key={s} active={statusFilter === s} onClick={() => setStatusFilter(s)}>
              {s === 'all' ? '全部' : s === 'Active' ? '已发布' : '已归档'}
            </Chip>
          ))}
        </div>
      </div>

      {/* 统计卡行 */}
      <div className="grid grid-cols-2 gap-3">
        <MetricCard label="报告数（当前类型）" value={filtered.length} icon={FileText} />
        <MetricCard
          label="已完成任务（当前报告）"
          value={metrics?.find((m) => m.key === 'completedTasks')?.value ?? '—'}
          hint={
            metrics?.find((m) => m.key === 'tasks')?.value != null
              ? `共 ${metrics.find((m) => m.key === 'tasks')?.value} 项`
              : undefined
          }
          icon={FileText}
        />
      </div>

      <div className="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-[360px_minmax(0,1fr)]">
        {/* 报告列表 */}
        <div className="space-y-2">
          {reportsQuery.isError ? (
            <Card>
              <EmptyState
                size="sm"
                icon={FileText}
                title="报告列表加载失败"
                description="请刷新页面重试；网络恢复前无法生成或查看报告。"
              />
            </Card>
          ) : isLoading ? (
            Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-20" />)
          ) : filtered.length === 0 ? (
            <Card>
              <EmptyState
                size="sm"
                icon={FileText}
                title="暂无报告"
                description={kind === 'Project' ? '选择项目后点击右上角生成一份项目报告。' : '点击右上角生成一份。'}
              />
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
                  {r.kind === 'Project' && (
                    <span className="truncate text-[11px] text-text-4">{projects.find((p) => p.id === r.projectId)?.name ?? r.projectId}</span>
                  )}
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
                <div className="mt-3 space-y-3 text-[13px] leading-5 text-text-2">
                  <ReportMarkdown content={selected.contentMarkdown} />
                </div>
              </Card>

              {/* 指标 + 图表 */}
              <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                <Card className="p-4">
                  <CardTitle>关键指标</CardTitle>
                  {metrics === null ? (
                    <div className="mt-2">
                      <EmptyState size="sm" title="指标数据无法读取" description="报告元数据损坏或为空，请重新生成报告。" />
                    </div>
                  ) : (
                    <div className="mt-2 space-y-3">
                      {metricGroups.map(({ group, label, items }) => (
                        <div key={group}>
                          <CardSubtitle>{label}</CardSubtitle>
                          <div className="mt-1 divide-y divide-divider">
                            {items.map((m) => (
                              <div key={m.key} className="flex items-center justify-between gap-3 py-1.5 text-[13px]">
                                <span className="text-text-3">{m.label}</span>
                                <span className={`tnum text-right font-medium ${m.value === null ? 'text-text-4' : 'text-text-1'}`}>
                                  {m.display}
                                  {m.unit ? <span className="ml-0.5 text-[11px] font-normal text-text-4">{m.unit}</span> : null}
                                </span>
                              </div>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </Card>
                <Card className="p-4">
                  <CardTitle>任务完成漏斗</CardTitle>
                  {completionKnowable ? (
                    <EChartsBox option={funnelOption} height={170} />
                  ) : (
                    <div className="flex h-[170px] items-center justify-center text-[12px] text-text-4">
                      暂无可展示的任务完成分布
                    </div>
                  )}
                  <CardSubtitle>完成率仪表盘</CardSubtitle>
                  {completionKnowable ? (
                    <EChartsBox option={gaugeOption} height={150} />
                  ) : (
                    <div className="flex h-[150px] items-center justify-center rounded-ctl bg-surface-2 text-[12px] text-text-4">
                      任务数为 0 或无数据，无法计算完成率
                    </div>
                  )}
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
