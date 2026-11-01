import { useState } from 'react'
import { Button, Card, Checkbox, EmptyState, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import type { RouteCreateIntent, RouteOptionView } from './types'

export interface RouteSetupPanelProps {
  routes: RouteOptionView[]
  selectedRouteId: string
  onSelectRoute: (routeId: string) => void
  cloudKeyReady: boolean
  onCreate?: (intent: RouteCreateIntent) => void | Promise<void>
  loading?: boolean
  loadError?: string | null
  readOnly?: boolean
  disabled?: boolean
}

export function RouteSetupPanel({
  routes,
  selectedRouteId,
  onSelectRoute,
  cloudKeyReady,
  onCreate,
  loading = false,
  loadError = null,
  readOnly = false,
  disabled = false,
}: RouteSetupPanelProps) {
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState('')
  const selected = routes.find((route) => route.id === selectedRouteId)
  const blocked = disabled || readOnly || busy || !onCreate
  const cloudMissingKey = selected?.kind === 'cloud' && !cloudKeyReady
  const canCreate = Boolean(selected && confirmed && !blocked && !cloudMissingKey && (
    selected.available || (!selected.available && selected.kind === 'local')
  ))
  const consentLabel = selected?.kind === 'cloud'
    ? '确认本次音频发送至云端，并理解长等待与可能产生的费用'
    : selected?.available
      ? '确认使用本机执行本次转写'
      : '确认保存为排队任务；本机可用前不会自动切换到云端或产生云端费用'

  async function create() {
    if (!selected || !canCreate || !onCreate) return
    setBusy(true)
    setActionError('')
    try {
      await onCreate({
        routeId: selected.id,
        mode: selected.available ? 'start' : 'queue',
        audioLeavesDevice: selected.kind === 'cloud',
        possibleCharge: selected.kind === 'cloud',
        confirmed: true,
      })
      setConfirmed(false)
    } catch (error) {
      setActionError(`${error instanceof Error ? error.message : '提交失败'}。原有任务与结果仍保留，页面不会自动重试或切换云端。`)
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <Skeleton className="h-56" />
  if (loadError) return <InlineAlert tone="crit" title="执行位置加载失败">{loadError}；不会自动回退到其他执行位置。</InlineAlert>
  if (!routes.length) return <EmptyState title="没有可用的执行位置" description="请由管理员先配置本机或云端转写目标。" />

  return <Card className="min-w-0 space-y-4 p-4 sm:p-5">
    <div className="min-w-0">
      <h2 className="text-sm font-semibold text-text-1">笔记录音执行位置</h2>
      <p className="mt-1 break-words text-xs text-text-3">每次新任务固定使用确认时的执行位置。本机不可用时只排队，不会自动改用云端。</p>
    </div>
    <div className="grid min-w-0 gap-2 sm:grid-cols-2" role="radiogroup" aria-label="执行位置">
      {routes.map((route) => <button
        key={route.id}
        type="button"
        role="radio"
        aria-checked={route.id === selectedRouteId}
        className={`min-w-0 rounded-ctl border p-3 text-left outline-none transition-colors focus-visible:outline-2 focus-visible:outline-primary-ring disabled:opacity-50 ${
          route.id === selectedRouteId ? 'border-primary bg-primary-soft' : 'border-border bg-bg hover:bg-surface'
        }`}
        disabled={disabled || readOnly}
        onClick={() => {
          setConfirmed(false)
          setActionError('')
          onSelectRoute(route.id)
        }}
      >
        <span className="flex flex-wrap items-center gap-1.5">
          <span className="text-sm font-medium text-text-1">{route.label}</span>
          {route.isDefault && <StatusBadge size="xs" tone="neutral" dot={false}>默认</StatusBadge>}
          <StatusBadge size="xs" tone={route.available ? 'ok' : 'warn'} dot={false}>{route.available ? '可执行' : '需排队'}</StatusBadge>
        </span>
        <span className="mt-1 block break-words text-xs text-text-3">{route.destinationLabel ?? route.availabilityLabel ?? '未提供目标信息'}</span>
        {!route.available && route.availabilityLabel && <span className="mt-1 block break-words text-xs text-warn">{route.availabilityLabel}</span>}
      </button>)}
    </div>

    {selected?.kind === 'cloud' && <InlineAlert tone="warn" title="云端音频出域与费用">
      <p className="break-words">{selected.destinationLabel ?? '云端执行位置'}。{selected.costNotice ?? '音频将离开本机，任务可能长时间等待，并按成功请求产生费用。'}</p>
      {cloudMissingKey && <p className="mt-1 text-crit">云端密钥未配置，不能创建云端任务。</p>}
    </InlineAlert>}
    {selected?.kind === 'local' && !selected.available && <InlineAlert tone="info" title="本机当前不可用">
      新任务会保持排队，本机恢复后按顺序处理；不会自动切换云端、创建新的付费请求或丢弃旧历史。
    </InlineAlert>}
    {selected?.kind === 'local' && selected.available && <InlineAlert tone="neutral" title="本机执行">
      本机转写不要求云端 API Key。录音不会因选择本机而离开设备。
    </InlineAlert>}

    {selected && <div className="space-y-3 rounded-ctl border border-border p-3">
      <label className="flex min-w-0 items-start gap-2 text-xs text-text-2">
        <Checkbox checked={confirmed} onCheckedChange={setConfirmed} disabled={blocked || cloudMissingKey} ariaLabel={consentLabel} />
        <span className="break-words">{consentLabel}</span>
      </label>
      <Button
        variant={selected.kind === 'cloud' ? 'primary' : 'secondary'}
        loading={busy}
        disabled={!canCreate}
        onClick={() => void create()}
      >
        {selected.kind === 'local' && !selected.available ? '保存排队任务' : selected.kind === 'cloud' ? '确认并创建云端任务' : '创建本机任务'}
      </Button>
    </div>}
    {actionError && <InlineAlert tone="crit" title="提交未确认">{actionError}</InlineAlert>}
    {readOnly && <InlineAlert tone="neutral" title="只读">当前账号只能查看执行位置与历史，不能创建或重跑任务。</InlineAlert>}
  </Card>
}
