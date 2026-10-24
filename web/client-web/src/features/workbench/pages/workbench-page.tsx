import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { CalendarClock, CheckCircle2, Sparkles } from 'lucide-react'
import {
  useAiActions,
  useAiSuggestions,
  useInboxTasks,
  useLayers,
  useOutlookSettings,
  useToggleTaskComplete,
} from '@/features/calendar/queries'
import { usePendingConfirmations } from '@/features/operations/queries'
import { TASK_STATUS } from '@/features/calendar/types'
import type { AiPlanPlaceholder } from '@/features/calendar/types'
import { DueText } from '@/features/calendar/components/due-text'
import { apiGet } from '@/api/client'
import { todayBusinessDay } from '@/lib/businessDay'
import { formatDurationC, formatRange, formatTime } from '@/lib/datetime'
import { POLL } from '@/lib/polling'
import { Button, Card, CardTitle, EmptyState, MetricCard, PageHeader, Segmented, StatusBadge } from '@/components/ui'
import { notifySuccess } from '@/lib/notify'

interface PcSummaryTiles {
  keystats?: { keyPresses?: number } | null
  metrics?: { activeInputDuration?: string; totalRecordedDuration?: string; switchFrequency?: number; appSwitchCount?: number } | null
  categories?: { categoryName: string; share: number; color: string }[]
}

/** PC 概览（工作台 3 瓦片，GET /pc/summary?date=，P3 将全面接管 PC 域） */
function usePcSummary() {
  const date = todayBusinessDay()
  return useQuery({
    queryKey: ['pc', 'summary', date],
    queryFn: () => apiGet<PcSummaryTiles>(`/api/v1/pc/summary?date=${date}`, { auth: true }),
    refetchInterval: POLL.statusSummary,
    staleTime: 60_000,
    retry: false,
    meta: { silent: true },
  })
}

function AiSuggestionCard({ item }: { item: AiPlanPlaceholder }) {
  const actions = useAiActions()
  return (
    <Card className="flex flex-col gap-2 p-4">
      <div className="flex items-center gap-2">
        <StatusBadge tone="info" dot={false}>
          <Sparkles className="mr-0.5 inline size-3" aria-hidden />
          {item.source === 'ai' ? 'AI 规则' : item.source === 'rule-engine' ? '规则引擎' : item.source}
        </StatusBadge>
        <span className="tnum ml-auto text-xs text-text-3">{formatRange(item.startsAt, item.endsAt)}</span>
      </div>
      <div className="text-sm font-medium text-text-1">{item.title}</div>
      <p className="line-clamp-2 text-xs text-text-3">{item.reason}</p>
      <div className="mt-auto flex gap-2 pt-1">
        <Button
          variant="primary"
          size="sm"
          loading={actions.confirm.isPending}
          onClick={async () => {
            await actions.confirm.mutateAsync(item.id)
            notifySuccess('已采纳：将创建操作确认单，请到确认中心完成确认')
          }}
        >
          采纳
        </Button>
        <Button variant="secondary" size="sm" loading={actions.dismiss.isPending} onClick={() => void actions.dismiss.mutateAsync(item.id)}>
          忽略
        </Button>
      </div>
    </Card>
  )
}

function OutlookStatusCard() {
  const { data } = useOutlookSettings()
  const ui = data?.uiStatus ?? 'not-configured'
  const tone = ui === 'connected' ? 'ok' : ui === 'waiting-auth' ? 'warn' : 'neutral'
  const label =
    ui === 'connected' ? '已连接' : ui === 'waiting-auth' ? '等待授权' : ui === 'reauth-required' ? '需重新授权' : ui === 'not-configured' ? '未配置' : '连接异常'
  return (
    <Card className="flex flex-col gap-2 p-4">
      <div className="flex items-center gap-2">
        <CardTitle>Outlook 同步</CardTitle>
        <StatusBadge tone={tone} className="ml-auto">{label}</StatusBadge>
      </div>
      <div className="space-y-1 text-xs text-text-3">
        <div>令牌健康：{data?.tokenHealth ?? '—'}</div>
        <div>最近同步：{data?.lastSyncedAt ? formatTime(data.lastSyncedAt) : '—'}</div>
        {data?.lastError && <div className="truncate text-crit">{data.lastError}</div>}
      </div>
      <Link to="/settings/microsoft" className="text-xs text-primary hover:underline outline-none">
        配置连接 →
      </Link>
    </Card>
  )
}

const HORizons = [
  { value: '3', label: '3 天' },
  { value: '7', label: '7 天' },
  { value: '14', label: '14 天' },
] as const

/** 工作台（02 §workbench：运营驾驶舱） */
export function WorkbenchPage() {
  const navigate = useNavigate()
  const [horizon, setHorizon] = useState<'3' | '7' | '14'>('7')

  const range = useMemo(() => {
    const s = new Date()
    s.setHours(0, 0, 0, 0)
    const e = new Date(s)
    e.setDate(e.getDate() + 1)
    return { start: s.toISOString(), end: e.toISOString() }
  }, [])

  const { data: layers } = useLayers(range.start, range.end, 'all', false, true)
  const { data: pending = [] } = usePendingConfirmations()
  const { data: suggestions = [] } = useAiSuggestions()
  const { data: inbox } = useInboxTasks()
  const { data: pcSummary } = usePcSummary()
  const toggleComplete = useToggleTaskComplete()
  const ai = useAiActions()

  const layerCounts = useMemo(() => {
    const items = layers?.items ?? []
    return {
      events: items.filter((i) => i.layer === 'events').length,
      taskSegments: items.filter((i) => i.layer === 'task-segments').length,
      habits: items.filter((i) => i.layer === 'habits').length,
      availability: items.filter((i) => i.layer === 'availability').length,
      ai: items.filter((i) => i.layer === 'ai-placeholders').length,
    }
  }, [layers])

  const todos = useMemo(
    () => (inbox ?? []).filter((t) => t.status !== TASK_STATUS.completed).slice(0, 8),
    [inbox],
  )

  return (
    <div>
      <PageHeader
        title="工作台"
        subtitle="排程工作台 / 运营驾驶舱"
        actions={
          <>
            <span className="hidden text-xs text-text-3 sm:inline">地平线</span>
            <Segmented
              size="sm"
              value={horizon}
              onValueChange={setHorizon}
              options={HORizons.map((h) => ({ value: h.value, label: h.label }))}
            />
          </>
        }
      />

      {/* 统计卡行 */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
        <MetricCard label="今日日程图层" value={layerCounts.events} icon={CalendarClock} />
        <MetricCard label="任务时间段" value={layerCounts.taskSegments} icon={CalendarClock} />
        <MetricCard label="待确认操作" value={pending.length} hint={pending.length > 0 ? '需要处理' : undefined} hintTone={pending.length > 0 ? 'warn' : 'neutral'} icon={CheckCircle2} />
        <MetricCard label="AI 排程建议" value={suggestions.length} icon={Sparkles} />
        <MetricCard
          label="按键总数（今日）"
          value={pcSummary?.keystats?.keyPresses != null ? pcSummary.keystats.keyPresses.toLocaleString() : '—'}
          icon={CalendarClock}
        />
      </div>

      <div className="mt-5 grid grid-cols-1 gap-4 xl:grid-cols-3">
        {/* AI 排程建议 */}
        <section className="xl:col-span-2">
          <div className="mb-2 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-text-1">AI 智能排程建议</h2>
            <Button
              variant="secondary"
              size="sm"
              loading={ai.generate.isPending}
              onClick={async () => {
                const res = await ai.generate.mutateAsync(Number(horizon))
                notifySuccess(res.placeholders.length > 0 ? `已生成 ${res.placeholders.length} 条建议` : '没有可排的任务')
              }}
            >
              <Sparkles className="size-4" aria-hidden /> 一键生成
            </Button>
          </div>
          {suggestions.length === 0 ? (
            <Card>
              <EmptyState
                size="sm"
                icon={Sparkles}
                title="暂无排程建议"
                description="点击「一键生成」，AI 会按地平线范围给出可采纳的排期建议。"
              />
            </Card>
          ) : (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              {suggestions.map((s) => (
                <AiSuggestionCard key={s.id} item={s} />
              ))}
            </div>
          )}

          {/* 待确认 + Outlook 状态 */}
          <div className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Card className="flex flex-col p-4">
              <div className="flex items-center gap-2">
                <CardTitle>待确认操作</CardTitle>
                <StatusBadge tone={pending.length > 0 ? 'warn' : 'ok'} className="ml-auto">{pending.length}</StatusBadge>
              </div>
              {pending.length === 0 ? (
                <EmptyState size="sm" title="没有待确认的操作" />
              ) : (
                <ul className="mt-2 space-y-1.5">
                  {pending.slice(0, 3).map((c: { id: string; summary: string }) => (
                    <li key={c.id} className="truncate rounded-ctl bg-surface px-2.5 py-1.5 text-[13px] text-text-2">
                      {c.summary}
                    </li>
                  ))}
                </ul>
              )}
              <Link to="/confirmations" className="mt-auto pt-2 text-xs text-primary hover:underline outline-none">
                打开确认中心 →
              </Link>
            </Card>
            <OutlookStatusCard />
          </div>
        </section>

        {/* 右列：待办 + PC 概览 + 链接 */}
        <section className="space-y-4">
          <Card className="flex max-h-[420px] flex-col p-4">
            <div className="flex items-center justify-between">
              <CardTitle>待办任务</CardTitle>
              <Link to="/tasks" className="text-xs text-primary hover:underline outline-none">全部 →</Link>
            </div>
            <div className="mt-2 min-h-0 flex-1 overflow-y-auto">
              {todos.length === 0 ? (
                <EmptyState size="sm" title="没有未完成的任务" />
              ) : (
                <ul className="divide-y divide-divider">
                  {todos.map((t) => (
                    <li key={t.id} className="flex items-center gap-2 py-2">
                      <button
                        type="button"
                        aria-label={`完成 ${t.title}`}
                        onClick={() => toggleComplete.mutate({ task: t, completed: true })}
                        className="size-4 shrink-0 rounded-full border border-border-strong transition-colors hover:border-ok hover:bg-ok-soft outline-none"
                      />
                      <button
                        type="button"
                        className="min-w-0 flex-1 text-left text-[13px] text-text-1 outline-none hover:text-primary"
                        onClick={() => navigate(`/tasks`)}
                      >
                        <span className="block truncate">{t.title}</span>
                        <span className="text-[11px] text-text-4">
                          {t.due
                            ? <DueText due={t.due} inactive={t.status === TASK_STATUS.cancelled} />
                            : '无截止'}
                          {t.estimatedDuration ? ` · ${formatDurationC(t.estimatedDuration)}` : ''}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </Card>

          <Card className="p-4">
            <div className="flex items-center gap-2">
              <CardTitle>PC 记录概览</CardTitle>
              <Link to="/pc-tracker" className="ml-auto text-xs text-primary hover:underline outline-none">详情 →</Link>
            </div>
            <div className="mt-3 grid grid-cols-3 gap-2">
              <div className="rounded-ctl bg-surface px-3 py-2.5 text-center">
                <div className="tnum text-lg font-semibold text-text-1">
                  {pcSummary?.metrics?.activeInputDuration?.split(':').slice(0, 2).join(':') ?? '—'}
                </div>
                <div className="text-[11px] text-text-3">有效输入</div>
              </div>
              <div className="rounded-ctl bg-surface px-3 py-2.5 text-center">
                <div className="tnum text-lg font-semibold text-text-1">
                  {pcSummary?.metrics?.switchFrequency != null ? pcSummary.metrics.switchFrequency.toFixed(1) : '—'}
                </div>
                <div className="text-[11px] text-text-3">切换频率</div>
              </div>
              <div className="rounded-ctl bg-surface px-3 py-2.5 text-center">
                <div className="tnum text-lg font-semibold text-text-1">{pcSummary?.categories?.length ?? '—'}</div>
                <div className="text-[11px] text-text-3">活跃分类</div>
              </div>
            </div>
          </Card>

          <Card className="p-4">
            <CardTitle>快捷入口</CardTitle>
            <div className="mt-2 grid grid-cols-2 gap-2">
              {[
                { to: '/status', label: '系统状态' },
                { to: '/endpoint-shell', label: '端点外壳' },
                { to: '/data-center', label: '数据中心' },
                { to: '/reports', label: '报告' },
              ].map((l) => (
                <Link
                  key={l.to}
                  to={l.to}
                  className="rounded-ctl border border-border px-3 py-2 text-[13px] text-text-2 transition-colors hover:border-border-strong hover:text-text-1 outline-none"
                >
                  {l.label}
                </Link>
              ))}
            </div>
          </Card>
        </section>
      </div>
    </div>
  )
}
