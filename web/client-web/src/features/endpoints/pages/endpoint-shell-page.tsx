import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, HeartPulse, Send } from 'lucide-react'
import { apiGet, apiPost } from '@/api/client'
import { deferredIntervalMs } from '@/lib/polling'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, Card, CardTitle, EmptyState, Input, Label, PageHeader, Segmented, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

interface EndpointStatus {
  deviceId: string
  platform: string
  appVersion: string | null
  uploadStatus: string
  collectionCacheCount: number
  onlineOnlyBlockedCount: number
  lastHeartbeatAt: string | null
}

interface CollectionQuality {
  deviceId: string
  platform: string
  uploadStatus: string
  issueCount: number
  checkedAt: string
}

/** 端点外壳（02 §endpoint-shell：调试页——手动心跳 / 端点状态 / 采集质量 / 通知动作模拟） */
export function EndpointShellPage() {
  const qc = useQueryClient()
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)

  const endpoints = useQuery({
    queryKey: ['endpoints', 'list'],
    queryFn: () => apiGet<EndpointStatus[]>('/api/v1/endpoints'),
    refetchInterval: () => deferredIntervalMs(),
  })

  const quality = useQuery({
    queryKey: ['endpoints', 'quality', selectedDeviceId],
    queryFn: () => apiGet<CollectionQuality>(`/api/v1/endpoints/${encodeURIComponent(selectedDeviceId!)}/collection-quality`),
    enabled: selectedDeviceId != null,
    refetchInterval: () => deferredIntervalMs(),
  })

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['endpoints'] })

  return (
    <div>
      <PageHeader title="端点外壳" subtitle="设备端点心跳与采集质量调试（开发工具页）" />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[380px_minmax(0,1fr)]">
        {/* 手动心跳工具 */}
        <HeartbeatTool onDone={invalidate} />

        {/* 端点状态列表 */}
        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>端点状态（{endpoints.data?.length ?? 0}）</CardTitle>
            <StatusBadge tone="neutral" dot={false} className="ml-auto">延迟轮询</StatusBadge>
          </div>
          <div className="mt-3 space-y-2">
            {endpoints.isLoading ? (
              <Skeleton className="h-32" />
            ) : (endpoints.data?.length ?? 0) === 0 ? (
              <EmptyState size="sm" title="没有已注册的端点" />
            ) : (
              endpoints.data!.map((e) => (
                <button
                  key={e.deviceId}
                  type="button"
                  onClick={() => setSelectedDeviceId(e.deviceId)}
                  className={cn(
                    'w-full rounded-card border px-3 py-2.5 text-left transition-colors outline-none',
                    selectedDeviceId === e.deviceId ? 'border-primary bg-primary-soft' : 'border-border hover:border-border-strong',
                  )}
                >
                  <div className="flex items-center gap-2">
                    <span className="mono min-w-0 flex-1 truncate text-[13px] text-text-1">{e.deviceId}</span>
                    <StatusBadge tone={e.uploadStatus === 'Healthy' ? 'ok' : 'warn'} dot={false}>{e.uploadStatus}</StatusBadge>
                  </div>
                  <div className="mt-1 flex items-center gap-3 text-[11px] text-text-4">
                    <span>{e.platform}</span>
                    {e.appVersion && <span>v{e.appVersion}</span>}
                    <span className="tnum">缓存 {e.collectionCacheCount}</span>
                    {e.onlineOnlyBlockedCount > 0 && <span className="tnum text-warn">在线阻塞 {e.onlineOnlyBlockedCount}</span>}
                    <span className="tnum ml-auto">{e.lastHeartbeatAt ? formatTime(e.lastHeartbeatAt) : '—'}</span>
                  </div>
                </button>
              ))
            )}
          </div>
        </Card>
      </div>

      <div className="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-2">
        {/* 采集质量 */}
        <Card className="p-4">
          <CardTitle>采集质量</CardTitle>
          {selectedDeviceId == null ? (
            <p className="mt-3 text-[13px] text-text-4">在上方选择一个端点查看采集质量</p>
          ) : quality.isLoading ? (
            <Skeleton className="mt-3 h-20" />
          ) : quality.data ? (
            <div className="mt-3 space-y-2 text-[13px]">
              <div className="flex items-center gap-2">
                <span className="mono min-w-0 flex-1 truncate text-text-2">{quality.data.deviceId}</span>
                <StatusBadge tone={quality.data.issueCount === 0 ? 'ok' : 'warn'} dot={false}>
                  {quality.data.issueCount === 0 ? '无问题' : `${quality.data.issueCount} 个问题`}
                </StatusBadge>
              </div>
              <div className="text-[11px] text-text-4">
                上传状态 {quality.data.uploadStatus} · 检查于 {formatTime(quality.data.checkedAt)}
              </div>
            </div>
          ) : (
            <p className="mt-3 text-[13px] text-crit">质量检查失败</p>
          )}
        </Card>

        {/* 通知动作模拟 */}
        <NotificationActionsTool deviceId={selectedDeviceId} onDone={invalidate} />
      </div>
    </div>
  )
}

function HeartbeatTool({ onDone }: { onDone: () => void }) {
  const [deviceId, setDeviceId] = useState('')
  const [platform, setPlatform] = useState<'windows' | 'android'>('windows')
  const [sending, setSending] = useState(false)
  const qc = useQueryClient()
  const endpoints = qc.getQueryData<EndpointStatus[]>(['endpoints', 'list']) ?? []
  const blocked = endpoints.reduce((acc, e) => acc + e.onlineOnlyBlockedCount, 0)

  return (
    <Card className="h-fit p-4">
      <CardTitle>发送心跳</CardTitle>
      <div className="mt-3 space-y-3">
        <div>
          <Label htmlFor="hb-device">设备 ID</Label>
          <Input id="hb-device" className="mono" placeholder="如 ws-01" value={deviceId} onChange={(e) => setDeviceId(e.target.value)} />
        </div>
        <div>
          <Label>平台</Label>
          <Segmented
            value={platform}
            onValueChange={(v) => setPlatform(v)}
            options={[
              { value: 'windows', label: 'Windows' },
              { value: 'android', label: 'Android' },
            ]}
          />
        </div>
        <Button
          variant="primary"
          size="sm"
          disabled={!deviceId.trim()}
          loading={sending}
          onClick={async () => {
            setSending(true)
            try {
              await apiPost(`/api/v1/endpoints/${encodeURIComponent(deviceId.trim())}/heartbeat`, {
                platform,
                appVersion: null,
                uploadStatus: 'Healthy',
                collectionCacheCount: 0,
              })
              notifySuccess('心跳已发送')
              onDone()
            } catch (err) {
              notifyError(err instanceof Error ? err.message : '发送失败')
            } finally {
              setSending(false)
            }
          }}
        >
          <Send className="size-4" aria-hidden /> 发送
        </Button>
        <div className="flex items-center gap-2 rounded-ctl bg-surface px-3 py-2 text-xs">
          <HeartPulse className="size-3.5 text-text-3" aria-hidden />
          <span className="text-text-3">在线专属拦截计数</span>
          <span className={cn('tnum ml-auto font-semibold', blocked > 0 ? 'text-warn' : 'text-text-1')}>{blocked}</span>
        </div>
      </div>
    </Card>
  )
}

function NotificationActionsTool({ deviceId, onDone }: { deviceId: string | null; onDone: () => void }) {
  const [action, setAction] = useState('remind-later')
  const [riskLevel, setRiskLevel] = useState<'L1LowRiskAction' | 'L4BatchOrDestructiveGovernance'>('L1LowRiskAction')
  const [result, setResult] = useState<{ result: string; detailUrl: string | null; message: string | null } | null>(null)

  return (
    <Card className="p-4">
      <CardTitle>通知动作模拟</CardTitle>
      <div className="mt-3 space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <div>
            <Label htmlFor="na-action">动作</Label>
            <Input id="na-action" value={action} onChange={(e) => setAction(e.target.value)} />
          </div>
          <div>
            <Label>风险等级</Label>
            <Segmented
              size="sm"
              value={riskLevel}
              onValueChange={(v) => setRiskLevel(v)}
              options={[
                { value: 'L1LowRiskAction', label: '低风险' },
                { value: 'L4BatchOrDestructiveGovernance', label: '高风险' },
              ]}
            />
          </div>
        </div>
        <Button
          variant="secondary"
          size="sm"
          disabled={deviceId == null || !action.trim()}
          onClick={async () => {
            try {
              const res = await apiPost<{ result: string; detailUrl: string | null; message: string | null }>(
                `/api/v1/endpoints/${encodeURIComponent(deviceId!)}/notification-actions`,
                { action, riskLevel },
              )
              setResult(res)
              notifySuccess(`结果：${res.result}`)
              onDone()
            } catch (err) {
              notifyError(err instanceof Error ? err.message : '执行失败')
            }
          }}
        >
          <Activity className="size-4" aria-hidden /> 执行动作
        </Button>
        {result && (
          <div className="rounded-ctl border border-border bg-surface px-3 py-2 text-[13px]">
            <StatusBadge tone={result.result === 'Executed' ? 'ok' : result.result === 'OpenDetailRequired' ? 'warn' : 'neutral'} dot={false}>
              {result.result}
            </StatusBadge>
            {result.message && <p className="mt-1 text-xs text-text-3">{result.message}</p>}
            {result.detailUrl && <p className="mono mt-1 text-[11px] text-primary">{result.detailUrl}</p>}
          </div>
        )}
        {deviceId == null && <p className="text-[11px] text-text-4">先在上方选择端点设备</p>}
      </div>
    </Card>
  )
}
