import { useQuery } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { statusApi } from '../api'
import { mobileApi } from '@/features/mobile/api'
import { useQueryClient } from '@tanstack/react-query'
import { formatTime } from '@/lib/datetime'
import { HEALTH_LABEL, normalizeHealthStatus } from '@/lib/enums'
import { POLL } from '@/lib/polling'
import { Button, Card, CardTitle, EmptyState, InlineAlert, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

const STATUS_TONE = { healthy: 'ok', warning: 'warn', critical: 'crit', unknown: 'neutral' } as const

/** 状态页（02 §status：总体卡 + PC 质量 + 采集器健康 + 移动诊断 + 设备网格 + 组件网格） */
export function StatusPage() {
  const qc = useQueryClient()
  const detail = useQuery({
    queryKey: ['status', 'detail'],
    queryFn: statusApi.detail,
    refetchInterval: POLL.statusSummary,
  })
  const pcQuality = useQuery({ queryKey: ['status', 'pc-quality'], queryFn: statusApi.pcQuality, retry: false, meta: { silent: true } })
  const tracker = useQuery({ queryKey: ['status', 'tracker'], queryFn: statusApi.trackerHealth, retry: false, meta: { silent: true } })
  const daemons = useQuery({ queryKey: ['status', 'daemons'], queryFn: statusApi.daemonHeartbeats, retry: false, meta: { silent: true } })
  const mobileQuality = useQuery({ queryKey: ['status', 'mobile-quality'], queryFn: () => statusApi.mobileQuality(), retry: false, meta: { silent: true } })
  /* 移动设备（与守护心跳合并为一个设备列表，规格 operations-status.md:371） */
  const mobileDevices = useQuery({ queryKey: ['status', 'mobile-devices'], queryFn: mobileApi.devices, retry: false, meta: { silent: true } })

  const summary = detail.data?.summary
  const tone = summary ? STATUS_TONE[normalizeHealthStatus(summary.status)] : 'neutral'

  return (
    <div className="space-y-5">
      <PageHeader
        title="状态"
        subtitle={summary ? `${summary.label} · 检查于 ${formatTime(summary.checkedAt)}` : '系统与组件健康'}
        actions={
          <Button variant="secondary" size="sm" loading={detail.isFetching} onClick={() => void qc.invalidateQueries({ queryKey: ['status'] })}>
            <RefreshCw className="size-4" aria-hidden /> 刷新
          </Button>
        }
      />

      {/* 总体状态卡 */}
      {detail.isLoading ? (
        <Skeleton className="h-24" />
      ) : summary ? (
        <Card className={cn('flex items-center gap-4 p-5', tone === 'crit' && 'border-crit-border')}>
          <span className={cn('size-4 shrink-0 rounded-full', tone === 'ok' ? 'bg-ok' : tone === 'warn' ? 'bg-warn' : tone === 'crit' ? 'bg-crit status-dot-critical' : 'bg-neutral')} aria-hidden />
          <div className="min-w-0">
            <div className="text-xl font-semibold text-text-1">{summary.label}</div>
            <p className="mt-0.5 text-[13px] text-text-2">{summary.message}</p>
          </div>
          <StatusBadge tone={tone} className="ml-auto shrink-0">{HEALTH_LABEL[normalizeHealthStatus(summary.status)]}</StatusBadge>
        </Card>
      ) : (
        <InlineAlert tone="crit">无法获取系统状态</InlineAlert>
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        {/* PC 数据质量 */}
        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>PC 数据质量</CardTitle>
            {pcQuality.data && (
              <StatusBadge tone={STATUS_TONE[normalizeHealthStatus(pcQuality.data.overallStatus)]} dot={false} className="ml-auto">
                {pcQuality.data.label}
              </StatusBadge>
            )}
          </div>
          {pcQuality.isLoading ? (
            <Skeleton className="mt-3 h-20" />
          ) : pcQuality.data ? (
            <>
              <p className="mt-2 text-[13px] text-text-2">{pcQuality.data.message}</p>
              {pcQuality.data.issues.length > 0 && (
                <ul className="mt-2 space-y-1">
                  {pcQuality.data.issues.slice(0, 4).map((i) => (
                    <li key={i.code} className="text-xs text-text-3">· {i.message}{i.nextStep ? `（${i.nextStep}）` : ''}</li>
                  ))}
                </ul>
              )}
            </>
          ) : (
            <EmptyState size="sm" title="暂无数据" className="mt-3" />
          )}
        </Card>

        {/* 采集器健康 */}
        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>采集器健康</CardTitle>
            {tracker.data && (
              <StatusBadge tone={tracker.data.hookActive ? 'ok' : 'warn'} dot={false} className="ml-auto">
                {tracker.data.hookActive ? '运行中' : '钩子未激活'}
              </StatusBadge>
            )}
          </div>
          {tracker.isLoading ? (
            <Skeleton className="mt-3 h-20" />
          ) : tracker.data ? (
            <div className="mt-2 grid grid-cols-2 gap-2 text-center">
              <Tile label="上传事件" value={tracker.data.hookActive ? tracker.data.browserConnected ? '通道正常' : '浏览器未连' : '—'} />
              <Tile label="浏览器心跳" value={tracker.data.browserHeartbeatAgeSeconds != null ? `${tracker.data.browserHeartbeatAgeSeconds}s 前` : '—'} />
            </div>
          ) : (
            <p className="mt-3 text-[13px] text-text-4">守护进程未上报</p>
          )}
        </Card>

        {/* 移动诊断 */}
        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>移动数据质量</CardTitle>
            {mobileQuality.data && (
              <StatusBadge tone={STATUS_TONE[normalizeHealthStatus(mobileQuality.data.overallStatus)]} dot={false} className="ml-auto">
                {mobileQuality.data.label}
              </StatusBadge>
            )}
          </div>
          {mobileQuality.isLoading ? (
            <Skeleton className="mt-3 h-20" />
          ) : mobileQuality.data ? (
            <p className="mt-2 text-[13px] text-text-2">{mobileQuality.data.message}</p>
          ) : (
            <EmptyState size="sm" title="暂无数据" className="mt-3" />
          )}
        </Card>
      </div>

      {/* 连接设备网格（Windows 工作站 ∪ 移动设备） */}
      <Card className="p-4">
        <CardTitle>连接设备</CardTitle>
        <div className="mt-3 grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
          {(daemons.data ?? []).map((d) => (
            <div key={d.deviceId} className="rounded-card border border-border p-3">
              <div className="flex items-center gap-2">
                <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-text-1">{d.deviceId}</span>
                <StatusBadge tone={d.collectionPaused ? 'warn' : 'ok'} dot={false}>{d.collectionPaused ? '已暂停' : '采集中'}</StatusBadge>
              </div>
              <p className="mono mt-1 truncate text-[11px] text-text-4">{d.daemonKind} · {d.version}</p>
              <p className="tnum mt-1 text-[11px] text-text-3">
                上传队列 {d.uploadQueueCount ?? 0} · 最近上传 {d.lastSuccessfulUploadAt ? formatTime(d.lastSuccessfulUploadAt) : '—'}
              </p>
            </div>
          ))}
          {(mobileDevices.data ?? []).map((d) => (
            <div key={d.deviceId} className="rounded-card border border-border p-3">
              <div className="flex items-center gap-2">
                <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-text-1">{d.displayName}</span>
                <StatusBadge tone={d.isActive ? 'ok' : 'neutral'} dot={false}>{d.isActive ? '活跃' : '离线'}</StatusBadge>
              </div>
              <p className="mono mt-1 truncate text-[11px] text-text-4">
                {d.brand} {d.model} · Android {d.androidVersion}（API {d.sdkInt}）
              </p>
              <p className="mt-1 text-[11px] text-text-3">
                <span className="tnum">客户端 {d.appVersion}</span>
                <span className="tnum ml-2">最近活跃 {formatTime(d.lastSeenAt)}</span>
              </p>
            </div>
          ))}
          {(daemons.data?.length ?? 0) + (mobileDevices.data?.length ?? 0) === 0 && (
            <div className="col-span-full">
              <EmptyState size="sm" title="没有已连接的设备" description="Windows 工作站与 Android 设备上报后会出现在这里。" />
            </div>
          )}
        </div>
      </Card>

      {/* 组件网格 */}
      <Card className="p-4">
        <CardTitle>组件状态</CardTitle>
        <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {detail.isLoading ? (
            Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="h-20" />)
          ) : (
            (detail.data?.components ?? []).map((c) => {
              const t = STATUS_TONE[normalizeHealthStatus(c.status)]
              return (
                <div key={c.key} className="rounded-card border border-border p-3">
                  <div className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-text-1">{c.name}</span>
                    <StatusBadge tone={t} dot={false}>{HEALTH_LABEL[normalizeHealthStatus(c.status)]}</StatusBadge>
                  </div>
                  <p className="mt-1 text-xs text-text-3">{c.message}</p>
                  <p className="tnum mt-1 text-[11px] text-text-4">{formatTime(c.checkedAt)}</p>
                </div>
              )
            })
          )}
        </div>
      </Card>

      {/* 需要关注 */}
      {(detail.data?.nextSteps?.length ?? 0) > 0 && (
        <InlineAlert tone="warn" title="需要关注">
          <ul className="list-disc pl-4">
            {(detail.data?.nextSteps ?? []).map((s, i) => <li key={i}>{s}</li>)}
          </ul>
        </InlineAlert>
      )}
    </div>
  )
}

function Tile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-ctl bg-surface py-2">
      <div className="text-[13px] font-semibold text-text-1">{value}</div>
      <div className="text-[10px] text-text-4">{label}</div>
    </div>
  )
}
