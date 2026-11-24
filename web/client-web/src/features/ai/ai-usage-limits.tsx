import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Bell, Check, Plus, RefreshCw } from 'lucide-react'
import { Button, Card, Chip, EmptyState, Input, Label, Select, Skeleton, StatusBadge, Switch } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatTime } from '@/lib/datetime'
import {
  aiApi,
  type AiUsageLimitUsage,
  type AiUsageReminder,
  type SaveAiUsageLimitRule,
} from './api'

const statusLabels: Record<string, string> = {
  normal: '正常',
  warning: '接近上限',
  reached: '已达到上限',
  unknown: '用量未知',
}

function statusTone(value: string): 'ok' | 'warn' | 'crit' | 'neutral' {
  if (value === 'normal') return 'ok'
  if (value === 'warning') return 'warn'
  if (value === 'reached') return 'crit'
  return 'neutral'
}

function limitValue(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed) return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null
}

function usageText(value: number | null | undefined, suffix: string): string {
  return value == null ? '未知' : `${value}${suffix}`
}

export function AiUsageLimits({ userId }: { userId: string }) {
  const qc = useQueryClient()
  const prefix = ['ai', userId, 'usage-limits']
  const connections = useQuery({
    queryKey: ['ai', userId, 'connections'],
    queryFn: ({ signal }) => aiApi.connections(signal),
  })
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const selected = connections.data?.find((item) => item.id === selectedId) ?? connections.data?.[0] ?? null
  const usage = useQuery({
    queryKey: [...prefix, 'usage', selected?.id, selected?.limit.period],
    queryFn: ({ signal }) => aiApi.connectionUsage(selected!.id, selected!.limit.period, undefined, signal),
    enabled: selected != null,
    refetchInterval: (query) => (query.state.data?.runningRequestCount ?? 0) > 0 ? 3000 : false,
  })
  const reminders = useQuery({
    queryKey: [...prefix, 'reminders', selected?.id],
    queryFn: ({ signal }) => aiApi.usageReminders({ connectionId: selected!.id, limit: 50 }, signal),
    enabled: selected != null,
  })
  const [newName, setNewName] = useState('')
  const [connectionName, setConnectionName] = useState('')
  const [enabled, setEnabled] = useState(false)
  const [period, setPeriod] = useState<'day' | 'month'>('month')
  const [warningPercent, setWarningPercent] = useState('80')
  const [tokenLimit, setTokenLimit] = useState('')
  const [costLimit, setCostLimit] = useState('')
  const [currency, setCurrency] = useState('')
  const [timeZoneId, setTimeZoneId] = useState('Asia/Shanghai')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (selected) {
      setSelectedId(selected.id)
      setConnectionName(selected.name)
      setEnabled(selected.limit.enabled)
      setPeriod(selected.limit.period)
      setWarningPercent(String(selected.limit.warningPercent))
      setTokenLimit(selected.limit.tokenLimit == null ? '' : String(selected.limit.tokenLimit))
      setCostLimit(selected.limit.costLimit == null ? '' : String(selected.limit.costLimit))
      setCurrency(selected.limit.currency ?? '')
      setTimeZoneId(selected.limit.timeZoneId)
    }
  }, [selected])

  const refresh = async () => {
    await qc.invalidateQueries({ queryKey: ['ai', userId] })
  }

  async function createConnection() {
    const name = newName.trim()
    if (!name) return
    setBusy(true)
    try {
      const created = await aiApi.createConnection({ name })
      setNewName('')
      setSelectedId(created.id)
      await refresh()
      notifySuccess('AI服务连接已创建')
    } catch (error) {
      notifyError(errorText(error))
    } finally {
      setBusy(false)
    }
  }

  async function saveName() {
    if (!selected || !connectionName.trim()) return
    setBusy(true)
    try {
      await aiApi.updateConnection(selected.id, { name: connectionName.trim(), expectedRevision: selected.revision })
      await refresh()
      notifySuccess('连接名称已保存')
    } catch (error) {
      notifyError(errorText(error))
    } finally {
      setBusy(false)
    }
  }

  async function saveLimit() {
    if (!selected) return
    const warning = Number(warningPercent)
    if (!Number.isInteger(warning) || warning < 1 || warning > 99) {
      notifyError('提前提醒比例须为1–99的整数')
      return
    }
    const body: SaveAiUsageLimitRule = {
      expectedRevision: selected.limit.revision,
      enabled,
      period,
      warningPercent: warning,
      tokenLimit: limitValue(tokenLimit),
      costLimit: limitValue(costLimit),
      currency: costLimit.trim() ? currency.trim().toUpperCase() || null : null,
      timeZoneId: timeZoneId.trim(),
    }
    setBusy(true)
    try {
      await aiApi.saveConnectionUsageLimit(selected.id, body)
      await refresh()
      notifySuccess('用量限制已保存')
    } catch (error) {
      notifyError(errorText(error))
    } finally {
      setBusy(false)
    }
  }

  async function markRead(item: AiUsageReminder) {
    try {
      await aiApi.markUsageReminderRead(item.id)
      await qc.invalidateQueries({ queryKey: [...prefix, 'reminders'] })
    } catch (error) {
      notifyError(errorText(error))
    }
  }

  return <Card className="space-y-4 p-5">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h2 className="text-sm font-semibold text-text-1">连接级用量限制</h2>
        <p className="mt-1 max-w-[720px] text-xs leading-5 text-text-3">同一 AI 服务连接下的文字、视觉和 ASR 调用共享周期用量。缺少服务商 Token 或所选币种费用时，启用的限制会暂停新调用。</p>
      </div>
      <Button size="sm" variant="ghost" onClick={() => void refresh()}><RefreshCw className="size-4" aria-hidden />刷新</Button>
    </div>
    <div className="flex flex-wrap items-end gap-2 rounded-ctl border border-border p-3">
      <div className="min-w-[240px] flex-1"><Label htmlFor="ai-new-connection">新连接名称</Label><Input id="ai-new-connection" value={newName} onChange={(event) => setNewName(event.target.value)} placeholder="例如：主力云端模型" /></div>
      <Button variant="secondary" disabled={busy || !newName.trim()} onClick={() => void createConnection()}><Plus className="size-4" aria-hidden />新增连接</Button>
    </div>
    {connections.isLoading ? <Skeleton className="h-20" /> : connections.isError ? <EmptyState title="连接读取失败" description={errorText(connections.error)} action={<Button onClick={() => void connections.refetch()}>重试</Button>} /> : !connections.data?.length ? <EmptyState title="还没有连接级配置" description="保存模型时会自动创建连接，也可以在这里先建立共享连接。" />
      : <div className="flex flex-wrap gap-2">{connections.data.map((connection) => <button key={connection.id} type="button" className={`rounded-ctl border px-3 py-2 text-left ${connection.id === selected?.id ? 'border-accent bg-surface' : 'border-border'}`} onClick={() => setSelectedId(connection.id)}>
        <span className="block text-sm font-medium text-text-1">{connection.name}</span>
        <span className="mt-1 block text-xs text-text-3">{connection.modelCount} 个模型 · {connection.limit.enabled ? '限制已启用' : '限制未启用'}</span>
      </button>)}</div>}
    {selected && <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)]">
      <div className="space-y-4">
        <div className="rounded-ctl border border-border p-4">
          <div className="flex items-end gap-2"><div className="min-w-0 flex-1"><Label htmlFor="ai-connection-name">连接名称</Label><Input id="ai-connection-name" value={connectionName} disabled={busy} onChange={(event) => setConnectionName(event.target.value)} /></div><Button size="sm" variant="secondary" disabled={busy || !connectionName.trim()} onClick={() => void saveName()}>保存名称</Button></div>
          <p className="mt-2 text-xs text-text-3">绑定模型数 {selected.modelCount} · 版本 {selected.revision.slice(0, 8)}</p>
        </div>
        <div className="space-y-3 rounded-ctl border border-border p-4">
          <div className="flex items-center justify-between gap-3"><div><Label htmlFor="ai-usage-limit-enabled">启用用量限制</Label><p className="mt-1 text-xs text-text-3">达到任一上限或用量未知时，服务端会在发送模型前阻断请求。</p></div><Switch id="ai-usage-limit-enabled" checked={enabled} disabled={busy} onCheckedChange={setEnabled} /></div>
          <div className="grid gap-3 sm:grid-cols-2"><div><Label htmlFor="ai-usage-period">统计周期</Label><Select id="ai-usage-period" ariaLabel="统计周期" value={period} disabled={busy} options={[{ value: 'day', label: '每日' }, { value: 'month', label: '每月' }]} onValueChange={(value) => setPeriod(value as 'day' | 'month')} /></div><div><Label htmlFor="ai-usage-warning">提前提醒比例</Label><Input id="ai-usage-warning" inputMode="numeric" value={warningPercent} disabled={busy} onChange={(event) => setWarningPercent(event.target.value)} /></div></div>
          <div className="grid gap-3 sm:grid-cols-2"><div><Label htmlFor="ai-token-limit">Token 上限（可选）</Label><Input id="ai-token-limit" inputMode="numeric" value={tokenLimit} disabled={busy} onChange={(event) => setTokenLimit(event.target.value)} placeholder="留空表示不按 Token 限制" /></div><div><Label htmlFor="ai-cost-limit">费用上限（可选）</Label><Input id="ai-cost-limit" inputMode="decimal" value={costLimit} disabled={busy} onChange={(event) => setCostLimit(event.target.value)} placeholder="留空表示不按费用限制" /></div></div>
          <div className="grid gap-3 sm:grid-cols-2"><div><Label htmlFor="ai-cost-currency">费用币种</Label><Input id="ai-cost-currency" maxLength={3} value={currency} disabled={busy} onChange={(event) => setCurrency(event.target.value.toUpperCase())} placeholder="例如 CNY" /></div><div><Label htmlFor="ai-usage-time-zone">连接时区</Label><Input id="ai-usage-time-zone" value={timeZoneId} disabled={busy} onChange={(event) => setTimeZoneId(event.target.value)} /></div></div>
          <div className="flex justify-end"><Button variant="primary" disabled={busy} loading={busy} onClick={() => void saveLimit()}>保存用量限制</Button></div>
        </div>
      </div>
      <UsageSummary usage={usage.data} loading={usage.isLoading} error={usage.error} retry={() => void usage.refetch()} />
    </div>}
    {selected && <ReminderList reminders={reminders.data ?? []} loading={reminders.isLoading} error={reminders.error} retry={() => void reminders.refetch()} markRead={(item) => void markRead(item)} />}
  </Card>
}

function UsageSummary({ usage, loading, error, retry }: { usage?: AiUsageLimitUsage; loading: boolean; error: unknown; retry: () => void }) {
  if (loading) return <Skeleton className="h-64" />
  if (error || !usage) return <Card className="p-4"><EmptyState title="周期用量读取失败" description={errorText(error)} action={<Button onClick={retry}>重试</Button>} /></Card>
  return <div className="space-y-3 rounded-ctl border border-border p-4">
    <div className="flex flex-wrap items-center justify-between gap-2"><div><h3 className="text-sm font-semibold text-text-1">当前周期用量</h3><p className="mt-1 text-xs text-text-3">{usage.cycleKey} · {usage.timeZoneId}</p></div><StatusBadge dot={false} tone={statusTone(usage.status)}>{statusLabels[usage.status] ?? usage.status}</StatusBadge></div>
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">{[
      ['请求数', String(usage.requestCount)],
      ['Token', usageText(usage.totalTokens, '')],
      ['费用', usage.estimatedCost == null ? '未知' : `${usage.estimatedCost} ${usage.currency ?? ''}`.trim()],
      ['进行中', String(usage.runningRequestCount)],
    ].map(([label, value]) => <div key={label} className="rounded-ctl bg-surface p-3"><p className="text-xs text-text-3">{label}</p><p className="mt-1 text-sm font-semibold text-text-1">{value}</p></div>)}</div>
    <p className="text-xs leading-5 text-text-3">Token 状态 {statusLabels[usage.tokenStatus] ?? usage.tokenStatus} · 费用状态 {statusLabels[usage.costStatus] ?? usage.costStatus} · 未知 Token {usage.tokenUnknownRequestCount} 条 · 未知费用 {usage.costUnknownRequestCount} 条</p>
    {usage.coverageNotes.length > 0 && <ul className="space-y-1 text-xs leading-5 text-text-3">{usage.coverageNotes.map((note) => <li key={note}>· {note}</li>)}</ul>}
  </div>
}

function ReminderList({ reminders, loading, error, retry, markRead }: { reminders: AiUsageReminder[]; loading: boolean; error: unknown; retry: () => void; markRead: (item: AiUsageReminder) => void }) {
  if (loading) return <Skeleton className="h-24" />
  if (error) return <div className="rounded-ctl border border-border p-4"><EmptyState title="提醒读取失败" description={errorText(error)} action={<Button onClick={retry}>重试</Button>} /></div>
  return <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex items-center gap-2"><Bell className="size-4 text-text-3" aria-hidden /><h3 className="text-sm font-semibold text-text-1">用量提醒</h3><Chip>{reminders.filter((item) => !item.read).length} 条未读</Chip></div>
    {!reminders.length ? <p className="text-xs text-text-3">当前连接暂无提醒。</p> : <div className="space-y-2">{reminders.map((item) => <div key={item.id} className="flex flex-wrap items-center justify-between gap-3 rounded-ctl bg-surface p-3"><div className="min-w-0"><div className="flex flex-wrap items-center gap-2"><StatusBadge dot={false} tone={item.level === 'reached' ? 'crit' : 'warn'}>{item.level === 'reached' ? '已达到上限' : '接近上限'}</StatusBadge><span className="text-xs text-text-3">{item.cycleKey} · {formatTime(item.updatedAt)}</span></div><p className="mt-1 text-xs text-text-2">{item.metrics.join('、') || '用量变化'} · Token {item.snapshot.totalTokens ?? '未知'} · 费用 {item.snapshot.estimatedCost ?? '未知'}</p></div>{item.read ? <span className="text-xs text-text-3">已读</span> : <Button size="sm" variant="ghost" onClick={() => markRead(item)}><Check className="size-4" aria-hidden />标记已读</Button>}</div>)}</div>}
  </div>
}

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : '请求失败，请稍后重试'
}
