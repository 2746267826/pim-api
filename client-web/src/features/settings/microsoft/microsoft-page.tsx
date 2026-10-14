import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Circle, RefreshCw, Unplug, Zap } from 'lucide-react'
import { apiDelete, apiGet, apiPost, apiPut } from '@/api/client'
import { filesApi } from '@/features/files/api'
import { useProviders, useSyncStatus } from '@/features/files/queries'
import { outlookApi } from '@/features/calendar/api'
import type { OutlookSyncBatch } from '@/features/calendar/types'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { DeviceCodeFlow } from './device-code-flow'
import { Button, Card, CardTitle, Chip, Dialog, DialogBody, DialogContent, DialogHeader, EmptyState, Input, Label, PageHeader, Segmented, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

type Tab = 'outlook' | 'onedrive'

/**
 * Microsoft 账户（v0.4）：
 * - 顶部「共享配置」区：Azure Client ID 是 Outlook 与 OneDrive 共有的资源，单独成一个板块，
 *   一次填写可同时保存到两端（后端两者独立存储，本页可一键同步写入）
 * - 下方 Tab 只保留各自专有功能与同步历史
 */
export function MicrosoftPage() {
  const [params, setParams] = useSearchParams()
  const tab: Tab = params.get('tab') === 'onedrive' ? 'onedrive' : 'outlook'
  const qc = useQueryClient()
  const outlook = useOutlookSettingsSafe()
  const providers = useProviders()
  const provider = providers.data?.[0]

  const outlookConnected = outlook.data?.uiStatus === 'connected'
  const oneDriveConnected = provider?.status === 'connected'

  const invalidateAll = () => {
    void qc.invalidateQueries({ queryKey: ['calendar', 'outlook'] })
    void qc.invalidateQueries({ queryKey: ['files'] })
  }

  return (
    <div>
      <PageHeader
        title="Microsoft 账户"
        subtitle="Azure 应用配置为两端共用；Outlook 日历与 OneDrive 文件各自连接与同步"
      />

      {/* 共享状态条 */}
      <Card className="mb-4 flex flex-wrap items-center gap-x-6 gap-y-2 p-3.5">
        <span className="flex items-center gap-2 text-[13px]">
          <Circle className={cn('size-2.5', outlookConnected ? 'fill-ok text-ok' : 'fill-neutral text-neutral')} aria-hidden />
          日历 {outlookConnected ? '已连接' : '未连接'}
        </span>
        <span className="flex items-center gap-2 text-[13px]">
          <Circle className={cn('size-2.5', oneDriveConnected ? 'fill-ok text-ok' : 'fill-neutral text-neutral')} aria-hidden />
          文件 {oneDriveConnected ? '已绑定' : '未绑定'}
        </span>
        {outlook.data?.lastSyncedAt && (
          <span className="tnum text-xs text-text-4">日历最近同步 {formatTime(outlook.data.lastSyncedAt)}</span>
        )}
        {provider?.lastSyncAt && (
          <span className="tnum text-xs text-text-4">文件最近同步 {formatTime(provider.lastSyncAt)}</span>
        )}
      </Card>

      {/* ── 共享配置板块（Client ID 是两端共用资源） ── */}
      <SharedAzureConfig
        outlookClientId={outlook.data?.clientId ?? null}
        oneDriveClientId={provider?.clientId ?? null}
        oneDriveProviderId={provider?.id}
        outlookTokenHealth={outlook.data?.tokenHealth ?? null}
        outlookUiStatus={outlook.data?.uiStatus ?? null}
        onChanged={invalidateAll}
      />

      {/* ── 各端专有功能 ── */}
      <div className="mt-4 mb-3 flex items-center gap-2">
        <Segmented
          value={tab}
          onValueChange={(v) => setParams({ tab: v })}
          options={[
            { value: 'outlook', label: 'Outlook 日历' },
            { value: 'onedrive', label: 'OneDrive 文件' },
          ]}
        />
        <span className="text-xs text-text-4">
          {tab === 'outlook' ? '设备码授权、同步操作与历史' : '绑定、文件同步与历史'}
        </span>
      </div>

      {tab === 'outlook' ? <OutlookTab onChanged={invalidateAll} /> : <OneDriveTab onChanged={invalidateAll} />}
    </div>
  )
}

/** 容错的 Outlook 设置查询（未配置时后端返回默认值） */
function useOutlookSettingsSafe() {
  return useQuery({
    queryKey: ['calendar', 'outlook', 'settings', 'microsoft-page'],
    queryFn: () => outlookApi.settings(),
    refetchInterval: (q) => (q.state.data?.status === 'waiting-for-user' ? 3000 : false),
    retry: false,
    meta: { silent: true },
  })
}

/* ── 共享 Azure 配置板块 ──────────────────────────────────── */

function SharedAzureConfig({
  outlookClientId,
  oneDriveClientId,
  oneDriveProviderId,
  outlookTokenHealth,
  outlookUiStatus,
  onChanged,
}: {
  outlookClientId: string | null
  oneDriveClientId: string | null
  oneDriveProviderId: string | undefined
  outlookTokenHealth: string | null
  outlookUiStatus: string | null
  onChanged: () => void
}) {
  const [clientId, setClientId] = useState('')
  const [busy, setBusy] = useState<'save-both' | 'save-outlook' | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    setClientId(outlookClientId ?? oneDriveClientId ?? '')
  }, [outlookClientId, oneDriveClientId])

  const bothSame = outlookClientId != null && oneDriveClientId != null && outlookClientId === oneDriveClientId
  const differs = outlookClientId !== oneDriveClientId

  async function save(target: 'both' | 'outlook') {
    const value = clientId.trim()
    if (!value) return
    setBusy(target === 'both' ? 'save-both' : 'save-outlook')
    setMessage(null)
    try {
      await apiPut('/api/v1/calendar/outlook/settings', { clientId: value })
      if (target === 'both' && oneDriveProviderId) {
        // OneDrive 的 Client ID 在重新绑定设备码时写入；已绑定时提示重新发起
        setMessage('已写入日历端；文件端将在下次「发起绑定」时使用该 Client ID（或先断开再绑定以立即生效）')
      }
      notifySuccess('Client ID 已保存')
      onChanged()
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '保存失败')
    } finally {
      setBusy(null)
    }
  }

  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center gap-2">
        <CardTitle>Azure 应用配置（两端共用）</CardTitle>
        <StatusBadge tone={bothSame ? 'ok' : differs && (outlookClientId || oneDriveClientId) ? 'warn' : 'neutral'} dot={false} className="ml-auto">
          {bothSame ? '两端一致' : differs ? '两端不一致' : '未配置'}
        </StatusBadge>
      </div>
      <p className="mt-1 text-xs text-text-3">
        一个 Azure 应用注册即可同时用于日历与文件（需允许公共客户端流 / 设备码）。两侧 Client ID 在后端各自存储，
        此处可一次填写并分别应用到两端。
      </p>

      <div className="mt-3 grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,1fr)_300px]">
        <div>
          <Label htmlFor="shared-client-id">Client ID</Label>
          <Input
            id="shared-client-id"
            className="mono"
            placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            value={clientId}
            onChange={(e) => setClientId(e.target.value)}
          />
          <div className="mt-2 flex flex-wrap gap-2">
            <Button
              variant="primary"
              size="sm"
              disabled={!clientId.trim()}
              loading={busy === 'save-both'}
              onClick={() => void save('both')}
            >
              保存到两端
            </Button>
            <Button
              variant="secondary"
              size="sm"
              disabled={!clientId.trim()}
              loading={busy === 'save-outlook'}
              onClick={() => void save('outlook')}
            >
              仅保存到日历端
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={async () => {
                try {
                  await apiPost('/api/v1/calendar/outlook/check', {})
                  notifySuccess('日历端连接检查完成')
                  onChanged()
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '检查失败')
                }
              }}
            >
              <Zap className="size-3.5" aria-hidden /> 检查日历连接
            </Button>
          </div>
          {message && <p className="mt-2 text-[11px] text-warn">{message}</p>}
        </div>

        {/* 两端当前取值对照 */}
        <div className="space-y-2 rounded-ctl bg-surface p-3">
          <div>
            <div className="text-[11px] text-text-4">日历端（Outlook）</div>
            <div className="mono truncate text-xs text-text-2" title={outlookClientId ?? ''}>
              {outlookClientId ?? '未配置'}
            </div>
            <div className="mt-0.5 flex gap-2 text-[11px] text-text-4">
              <span>{outlookUiStatus ?? '—'}</span>
              <span>令牌 {outlookTokenHealth ?? '—'}</span>
            </div>
          </div>
          <div className="border-t border-divider pt-2">
            <div className="text-[11px] text-text-4">文件端（OneDrive）</div>
            <div className="mono truncate text-xs text-text-2" title={oneDriveClientId ?? ''}>
              {oneDriveClientId ?? '未配置'}
            </div>
          </div>
        </div>
      </div>
    </Card>
  )
}

/* ── Outlook Tab：设备码 + 同步操作 + 同步历史 ─────────────── */

function OutlookTab({ onChanged }: { onChanged: () => void }) {
  const qc = useQueryClient()
  const [syncRangeOpen, setSyncRangeOpen] = useState(false)
  const [rangeStart, setRangeStart] = useState('')
  const [rangeEnd, setRangeEnd] = useState('')
  const [detailBatch, setDetailBatch] = useState<OutlookSyncBatch | null>(null)

  /* 同步历史（注意：分页字段是 total，不是 totalCount） */
  const batches = useQuery({
    queryKey: ['calendar', 'outlook', 'batches', 'page', 1],
    queryFn: () => outlookApi.batches(1, 20),
    refetchInterval: (q) => (q.state.data?.items.some((b) => b.status === 'running') ? 5000 : false),
  })

  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ['calendar', 'outlook'] })
    onChanged()
  }

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
      {/* 设备码授权（3 秒轮询） */}
      <DeviceCodeFlow
        title="设备码授权"
        hint="点击获取代码后在浏览器打开验证页、输入代码完成授权（每 3 秒自动检查）。"
        pollIntervalMs={3000}
        start={async () => {
          const s = await apiPost<{ id: string; userCode: string | null; verificationUri: string | null; expiresAt: string | null }>(
            '/api/v1/calendar/outlook/device-code',
            {},
          )
          return { sessionId: s.id, userCode: s.userCode, verificationUri: s.verificationUri, expiresAt: s.expiresAt }
        }}
        poll={async (sessionId) => {
          const s = await apiPost<{ status: string; errorMessage: string | null }>(
            '/api/v1/calendar/outlook/device-code/poll',
            { sessionId },
          )
          return { status: s.status, errorMessage: s.errorMessage }
        }}
        cancel={async (sessionId) => {
          await apiPost(`/api/v1/calendar/outlook/device-code/${sessionId}/cancel`, {})
        }}
        onConnected={invalidate}
      />

      {/* 同步操作 */}
      <Card className="p-4">
        <CardTitle>同步操作</CardTitle>
        <div className="mt-3 flex flex-wrap gap-2">
          {[
            { mode: 'normal', label: '立即同步' },
            { mode: 'full-resources', label: '深度同步' },
          ].map((m) => (
            <Button
              key={m.mode}
              variant="secondary"
              size="sm"
              onClick={async () => {
                try {
                  await apiPost('/api/v1/calendar/outlook/sync', { mode: m.mode })
                  notifySuccess(`已触发${m.label}`)
                  invalidate()
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '触发同步失败')
                }
              }}
            >
              <RefreshCw className="size-3.5" aria-hidden /> {m.label}
            </Button>
          ))}
          <Chip active={syncRangeOpen} onClick={() => setSyncRangeOpen((o) => !o)}>按范围同步…</Chip>
        </div>
        {syncRangeOpen && (
          <div className="mt-3 flex flex-wrap items-end gap-2">
            <div>
              <Label htmlFor="sync-range-start" className="text-[11px]">开始</Label>
              <Input id="sync-range-start" type="datetime-local" className="h-8 w-44" value={rangeStart} onChange={(e) => setRangeStart(e.target.value)} />
            </div>
            <div>
              <Label htmlFor="sync-range-end" className="text-[11px]">结束</Label>
              <Input id="sync-range-end" type="datetime-local" className="h-8 w-44" value={rangeEnd} onChange={(e) => setRangeEnd(e.target.value)} />
            </div>
            <Button
              variant="primary"
              size="sm"
              disabled={!rangeStart || !rangeEnd}
              onClick={async () => {
                try {
                  await apiPost('/api/v1/calendar/outlook/sync', {
                    mode: 'range-instances',
                    rangeStart: new Date(rangeStart).toISOString(),
                    rangeEnd: new Date(rangeEnd).toISOString(),
                  })
                  notifySuccess('已按范围触发同步')
                  invalidate()
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '触发失败')
                }
              }}
            >
              同步该范围
            </Button>
          </div>
        )}
      </Card>

      {/* 同步历史（分页批次卡列表，含每日历失败重试与取消） */}
      <Card className="p-4 xl:col-span-2">
        <div className="flex items-center gap-2">
          <CardTitle>同步历史</CardTitle>
          <span className="tnum ml-auto text-xs text-text-4">共 {batches.data?.total ?? 0} 批</span>
        </div>
        <div className="mt-3 space-y-2">
          {batches.isLoading ? (
            Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-16" />)
          ) : (batches.data?.items.length ?? 0) === 0 ? (
            <EmptyState size="sm" title="还没有同步批次" />
          ) : (
            batches.data!.items.map((b) => (
              <div key={b.id} className="rounded-card border border-border px-3 py-2.5">
                <div className="flex flex-wrap items-center gap-2">
                  <StatusBadge
                    tone={b.status === 'completed' ? 'ok' : b.status === 'running' ? 'info' : b.status === 'failed' ? 'crit' : 'warn'}
                  >
                    {b.status}
                  </StatusBadge>
                  {b.mode && <span className="text-[11px] text-text-4">{b.mode}</span>}
                  <span className="tnum ml-auto text-xs text-text-3">
                    {formatTime(b.startedAt)}
                    {b.finishedAt && ` → ${formatTime(b.finishedAt)}`}
                  </span>
                </div>
                <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px]">
                  <span className="tnum text-text-3">读 {b.readCount}</span>
                  <span className="tnum text-ok">建 {b.createdCount}</span>
                  <span className="tnum text-info">改 {b.updatedCount}</span>
                  {b.conflictCount > 0 && <span className="tnum text-warn">冲突 {b.conflictCount}</span>}
                  {b.failureCount > 0 && <span className="tnum text-crit">失败 {b.failureCount}</span>}
                  <div className="ml-auto flex gap-1.5">
                    <Button variant="ghost" size="sm" onClick={() => setDetailBatch(b)}>
                      详情
                    </Button>
                    {b.status === 'failed' && (
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={async () => {
                          try {
                            await apiPost('/api/v1/calendar/outlook/sync', { mode: b.mode ?? 'normal', retryOfBatchId: b.id })
                            notifySuccess('已按该批次重试')
                            invalidate()
                          } catch (err) {
                            notifyError(err instanceof Error ? err.message : '重试失败')
                          }
                        }}
                      >
                        重试
                      </Button>
                    )}
                    {b.status === 'running' && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-crit"
                        onClick={async () => {
                          try {
                            await apiPost(`/api/v1/calendar/outlook/sync/${b.id}/cancel`, {})
                            notifySuccess('已请求取消')
                            invalidate()
                          } catch (err) {
                            notifyError(err instanceof Error ? err.message : '取消失败')
                          }
                        }}
                      >
                        取消
                      </Button>
                    )}
                  </div>
                </div>
                {b.errorSummary && <p className="mt-1 text-[11px] text-crit">{b.errorSummary}</p>}
              </div>
            ))
          )}
        </div>
      </Card>

      <OutlookDangerZone onChanged={invalidate} />

      {/* 批次详情（含每日历结果 JSON） */}
      <Dialog open={detailBatch != null} onOpenChange={(o) => !o && setDetailBatch(null)}>
        <DialogContent className="max-w-[560px]">
          <DialogHeader title="同步批次详情" description={detailBatch?.id} />
          <DialogBody className="space-y-3 text-[13px]">
            {detailBatch && (
              <>
                <div className="grid grid-cols-2 gap-x-4 gap-y-1.5">
                  {[
                    ['状态', detailBatch.status],
                    ['模式', detailBatch.mode ?? '—'],
                    ['读取', String(detailBatch.readCount)],
                    ['新建', String(detailBatch.createdCount)],
                    ['更新', String(detailBatch.updatedCount)],
                    ['冲突', String(detailBatch.conflictCount)],
                    ['失败', String(detailBatch.failureCount)],
                    ['已请求取消', detailBatch.cancelRequested ? '是' : '否'],
                    ['开始', formatTime(detailBatch.startedAt)],
                    ['结束', detailBatch.finishedAt ? formatTime(detailBatch.finishedAt) : '—'],
                  ].map(([k, v]) => (
                    <div key={k} className="flex gap-2">
                      <span className="w-20 shrink-0 text-text-3">{k}</span>
                      <span className="tnum min-w-0 flex-1 break-all text-text-1">{v}</span>
                    </div>
                  ))}
                </div>
                {detailBatch.errorSummary && (
                  <div className="rounded-ctl border border-crit-border bg-crit-soft px-3 py-2 text-crit">{detailBatch.errorSummary}</div>
                )}
                <div>
                  <Label>每日历结果（perCalendarJson）</Label>
                  <pre className="mono max-h-48 overflow-auto rounded-ctl bg-surface p-3 text-[11px] text-text-2">
                    {detailBatch.perCalendarJson ?? '—'}
                  </pre>
                </div>
              </>
            )}
          </DialogBody>
          <div className="flex justify-end gap-2 px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setDetailBatch(null)}>关闭</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

function OutlookDangerZone({ onChanged }: { onChanged: () => void }) {
  const [preview, setPreview] = useState<{ bindingCount: number; calendarCount: number; eventCount: number } | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)

  return (
    <Card className="p-4 xl:col-span-2">
      <CardTitle>连接与本地数据</CardTitle>
      <div className="mt-3 flex flex-wrap gap-2">
        <Button
          variant="danger-soft"
          size="sm"
          onClick={async () => {
            try {
              await apiPost('/api/v1/calendar/outlook/disconnect', {})
              notifySuccess('已断开 Outlook 连接')
              onChanged()
            } catch (err) {
              notifyError(err instanceof Error ? err.message : '断开失败')
            }
          }}
        >
          <Unplug className="size-3.5" aria-hidden /> 断开连接
        </Button>
        <Button
          variant="danger-soft"
          size="sm"
          onClick={async () => {
            const res = await apiGet<{ bindingCount: number; calendarCount: number; eventCount: number }>(
              '/api/v1/calendar/outlook/local-data/preview',
            )
            setPreview(res)
            setConfirmOpen(true)
          }}
        >
          清理本地数据…
        </Button>
      </div>
      <p className="mt-2 text-[11px] text-text-4">断开不会删除本地日历与事件；「清理本地数据」会软删除它们（云端数据不受影响）。</p>

      <Dialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <DialogContent className="max-w-[420px]">
          <DialogHeader title="清理本地 Outlook 数据？" description="软删除 outlook 来源的日历与事件、删除绑定、清空令牌缓存。" />
          <DialogBody>
            {preview ? (
              <div className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2.5 text-[13px] text-warn">
                {preview.bindingCount} 个绑定 · {preview.calendarCount} 个日历 · {preview.eventCount} 个事件
              </div>
            ) : (
              <Skeleton className="h-16" />
            )}
          </DialogBody>
          <div className="flex justify-end gap-2 px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setConfirmOpen(false)}>取消</Button>
            <Button
              variant="danger"
              size="sm"
              onClick={async () => {
                try {
                  await apiDelete('/api/v1/calendar/outlook/local-data')
                  notifySuccess('本地数据已清理')
                  setConfirmOpen(false)
                  onChanged()
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '清理失败')
                }
              }}
            >
              确认清理
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

/* ── OneDrive Tab：绑定 + 同步状态 ─────────────────────────── */

function OneDriveTab({ onChanged }: { onChanged: () => void }) {
  const providers = useProviders()
  // 工单 05 / F2.3：不再写死 providers[0]。同一 provider 可以有多个账号，
  // 这里列出全部并让用户选一个查看/操作（默认选中「默认落点」那个）。
  const accounts = (providers.data ?? []).filter((p) => p.provider === 'onedrive')
  const connectedAccounts = accounts.filter((a) => a.status === 'connected')
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null)
  const provider =
    connectedAccounts.find((a) => a.id === selectedAccountId) ??
    connectedAccounts.find((a) => a.isDefault) ??
    connectedAccounts[0] ??
    accounts[0]
  const syncStatus = useSyncStatus(provider?.status === 'connected' ? provider.id : undefined)
  const [bindingSession, setBindingSession] = useState<{ providerId: string; userCode: string; verificationUri: string; expiresAt: string | null } | null>(null)
  const [manualBinding, setManualBinding] = useState(false)

  const invalidate = () => {
    onChanged()
  }

  return (
    <div className="space-y-4">
      {/* 账号列表：多账号并存时能看出各自的行与游标，并指定默认落点 */}
      {accounts.length > 0 && (
        <Card className="p-4">
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>OneDrive 账号</CardTitle>
            <Chip className="ml-auto">{connectedAccounts.length} 个已连接</Chip>
          </div>
          <div className="mt-3 space-y-2">
            {accounts.map((account) => (
              <div
                key={account.id}
                className={cn(
                  'flex flex-wrap items-center gap-2 rounded-ctl border px-3 py-2 text-[13px]',
                  account.id === provider?.id ? 'border-primary bg-primary-soft/40' : 'border-border',
                )}
              >
                <button
                  type="button"
                  className="min-w-0 flex-1 truncate text-left outline-none"
                  onClick={() => setSelectedAccountId(account.id)}
                >
                  {account.accountName ?? account.accountId ?? account.id.slice(0, 8)}
                </button>
                {account.isDefault && <Chip>默认落点</Chip>}
                <StatusBadge tone={account.status === 'connected' ? 'ok' : account.status === 'expired' ? 'warn' : 'neutral'}>
                  {account.status === 'connected' ? '已连接' : account.status === 'expired' ? '授权已失效' : '待授权'}
                </StatusBadge>
                <span className="tnum text-[11px] text-text-4">本次同步 {account.syncedItemCount}</span>
                {account.status === 'connected' && !account.isDefault && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={async () => {
                      try {
                        await filesApi.setDefault(account.id)
                        notifySuccess('已设为默认落点账号（附件与闪念图片存入该账号）')
                        onChanged()
                      } catch (err) {
                        notifyError(err instanceof Error ? err.message : '设置失败')
                      }
                    }}
                  >
                    设为默认
                  </Button>
                )}
              </div>
            ))}
          </div>
          <p className="mt-2 text-[11px] text-text-4">
            附件与闪念图片存入「默认落点」账号；文件页可在账号之间切换浏览。
          </p>
        </Card>
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {/* 绑定状态 / 手动绑定 */}
        <Card className="p-4">
          <CardTitle>OneDrive 绑定</CardTitle>
          {provider?.status === 'connected' ? (
            <div className="mt-3 space-y-2 text-[13px]">
              <div className="flex items-center gap-2">
                <span className="font-medium text-text-1">{provider.accountName ?? '已绑定'}</span>
                <StatusBadge tone="ok" dot={false} className="ml-auto">已连接</StatusBadge>
              </div>
              <div className="mono space-y-0.5 text-[11px] text-text-4">
                <div className="break-all">driveId: {provider.driveId ?? '—'}</div>
                <div className="break-all">Client ID: {provider.clientId ?? '—'}</div>
                <div>本次同步应用: {provider.syncedItemCount.toLocaleString()}（0 不代表失败）</div>
                <div>已索引总量: {provider.totalIndexedCount != null ? provider.totalIndexedCount.toLocaleString() : '未知'}</div>
                {provider.lastSyncAt && <div>最近同步: {formatTime(provider.lastSyncAt)}</div>}
              </div>
              <div className="flex flex-wrap gap-2 pt-1">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={async () => {
                    try {
                      await filesApi.startSync(provider.id)
                      notifySuccess('已开始同步')
                      invalidate()
                    } catch (err) {
                      notifyError(err instanceof Error ? err.message : '同步失败')
                    }
                  }}
                >
                  <RefreshCw className="size-3.5" aria-hidden /> 手动同步
                </Button>
                <Button variant="secondary" size="sm" onClick={() => setManualBinding(true)}>
                  重新绑定…
                </Button>
                <Button
                  variant="danger-soft"
                  size="sm"
                  onClick={async () => {
                    if (!window.confirm('断开 OneDrive？本地文件元数据会被清除，云端文件不受影响。')) return
                    try {
                      await filesApi.unbind(provider.id)
                      notifySuccess('已断开 OneDrive')
                      invalidate()
                    } catch (err) {
                      notifyError(err instanceof Error ? err.message : '断开失败')
                    }
                  }}
                >
                  <Unplug className="size-3.5" aria-hidden /> 断开
                </Button>
              </div>
            </div>
          ) : (
            <div className="mt-3 space-y-3">
              <p className="text-[13px] text-text-3">
                尚未绑定。请在上方「Azure 应用配置」粘贴 Client ID，然后点击下方按钮发起设备码绑定。
              </p>
              <Button
                variant="primary"
                size="sm"
                onClick={async () => {
                  try {
                    const s = await filesApi.bindOneDrive(provider?.clientId ?? '')
            if (!s.userCode || !s.verificationUri) {
              notifyError('绑定响应缺少设备码，请重试')
              return
            }
                    if (!s.userCode || !s.verificationUri) {
                      notifyError('绑定响应缺少设备码，请重试')
                      return
                    }
                    setBindingSession({
                      providerId: s.providerId,
                      userCode: s.userCode,
                      verificationUri: s.verificationUri,
                      expiresAt: new Date(Date.now() + (s.expiresIn ?? 900) * 1000).toISOString(),
                    })
                  } catch (err) {
                    notifyError(err instanceof Error ? err.message : '发起绑定失败')
                  }
                }}
              >
                发起绑定
              </Button>
            </div>
          )}
        </Card>

        {/* 文件同步状态（条件轮询 2s） */}
        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>文件同步状态</CardTitle>
            <StatusBadge
              tone={syncStatus.data?.syncStatus === 'syncing' ? 'info' : syncStatus.data?.syncStatus === 'error' ? 'crit' : 'ok'}
              className="ml-auto"
            >
              {syncStatus.data?.syncStatus === 'syncing' ? '同步中' : syncStatus.data?.syncStatus === 'error' ? '异常' : '空闲'}
            </StatusBadge>
          </div>
          <div className="mt-2 space-y-1 text-xs text-text-3">
            <div>本次同步应用：{syncStatus.data?.syncedItemCount?.toLocaleString() ?? '—'}（0 不代表失败）</div>
            <div>已索引总量：{syncStatus.data?.totalIndexedCount != null ? syncStatus.data.totalIndexedCount.toLocaleString() : '未知'}</div>
            <div>最近完成：{syncStatus.data?.lastSyncAt ? formatTime(syncStatus.data.lastSyncAt) : '—'}</div>
            {syncStatus.data?.lastError && <div className="text-crit">{syncStatus.data.lastError}</div>}
          </div>
          {syncStatus.data?.syncStatus === 'syncing' && (
            <p className="mt-2 text-[11px] text-info">同步中：状态每 2 秒自动刷新，完成后停止。</p>
          )}
        </Card>
      </div>

      {/* OneDrive 设备码向导（5 秒轮询） */}
      {(bindingSession || manualBinding) && provider?.status !== 'connected' && (
        <DeviceCodeFlow
          title="OneDrive 设备码绑定"
          hint="在浏览器打开验证页并输入代码；绑定完成后文件页即可浏览（每 5 秒自动检查）。"
          pollIntervalMs={5000}
          start={async () => {
            if (bindingSession) {
              return {
                sessionId: bindingSession.providerId,
                userCode: bindingSession.userCode,
                verificationUri: bindingSession.verificationUri,
                expiresAt: bindingSession.expiresAt,
              }
            }
            const s = await filesApi.bindOneDrive(provider?.clientId ?? '')
            if (!s.userCode || !s.verificationUri) {
              // 抛出去让向导显示原因；静默 return 会让按钮「点了没反应」
              throw new Error('绑定响应缺少设备码，请重试')
            }
            const session = {
              providerId: s.providerId,
              userCode: s.userCode,
              verificationUri: s.verificationUri,
              expiresAt: new Date(Date.now() + (s.expiresIn ?? 900) * 1000).toISOString(),
            }
            setBindingSession(session)
            return { sessionId: session.providerId, userCode: session.userCode, verificationUri: session.verificationUri, expiresAt: session.expiresAt }
          }}
          poll={async (providerId) => {
            const s = await filesApi.bindingStatus(providerId)
            return { status: s.status, errorMessage: null, accountName: s.accountName }
          }}
          onConnected={() => {
            setBindingSession(null)
            setManualBinding(false)
            invalidate()
          }}
        />
      )}

      {/* 同步与令牌详情（后端未提供文件同步批次历史端点，此处展示真实可得的同步态与令牌健康） */}
      <Card className="p-4">
        <div className="flex items-center gap-2">
          <CardTitle>同步与令牌</CardTitle>
          <span className="ml-auto text-xs text-text-4">数据源：GET /files/providers/{'{id}'}/sync-status</span>
        </div>
        <div className="mt-3 grid grid-cols-1 gap-x-6 gap-y-2 text-[12px] sm:grid-cols-2">
          <div className="flex justify-between gap-3 border-b border-divider py-1.5">
            <span className="text-text-3">同步状态</span>
            <span className="font-medium text-text-1">
              {syncStatus.data?.syncStatus === 'syncing' ? '同步中' : syncStatus.data?.syncStatus === 'error' ? '异常' : '空闲'}
            </span>
          </div>
          <div className="flex justify-between gap-3 border-b border-divider py-1.5">
            <span className="text-text-3">本次同步应用</span>
            <span className="tnum font-medium text-text-1">{syncStatus.data?.syncedItemCount?.toLocaleString() ?? '—'}</span>
          </div>
          <div className="flex justify-between gap-3 border-b border-divider py-1.5">
            <span className="text-text-3">已索引总量</span>
            <span className="tnum font-medium text-text-1">{syncStatus.data?.totalIndexedCount != null ? syncStatus.data.totalIndexedCount.toLocaleString() : '未知'}</span>
          </div>
          <div className="flex justify-between gap-3 border-b border-divider py-1.5">
            <span className="text-text-3">最近同步完成</span>
            <span className="tnum font-medium text-text-1">{syncStatus.data?.lastSyncAt ? formatTime(syncStatus.data.lastSyncAt) : '—'}</span>
          </div>
          <div className="flex justify-between gap-3 border-b border-divider py-1.5">
            <span className="text-text-3">令牌到期</span>
            <span className="tnum font-medium text-text-1">{provider?.tokenExpiresAt ? formatTime(provider.tokenExpiresAt) : '—'}</span>
          </div>
          <div className="flex justify-between gap-3 border-b border-divider py-1.5 sm:col-span-2">
            <span className="text-text-3">账号</span>
            <span className="font-medium text-text-1">
              {provider?.accountName ?? '—'}
              {provider?.accountId && <span className="mono ml-2 text-text-4">{provider.accountId}</span>}
            </span>
          </div>
          {provider?.lastError && (
            <div className="rounded-ctl border border-crit/30 bg-crit-soft px-2.5 py-1.5 text-crit sm:col-span-2">
              最近错误：{provider.lastError}
            </div>
          )}
        </div>
      </Card>
    </div>
  )
}
