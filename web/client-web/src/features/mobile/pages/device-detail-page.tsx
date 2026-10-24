import { Link, useParams } from 'react-router'
import { ArrowLeft } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { mobileApi } from '../api'

import { formatTime } from '@/lib/datetime'
import { Card, CardTitle, EmptyState, PageHeader, Skeleton, StatusBadge } from '@/components/ui'

/** 设备详情（02 §devices/:deviceId：只读页） */
export function DeviceDetailPage() {
  const { deviceId = '' } = useParams()
  const { data, isLoading } = useQuery({
    queryKey: ['mobile', 'device-detail', deviceId],
    queryFn: () => mobileApi.deviceDetail(deviceId),
    enabled: deviceId !== '',
  })

  return (
    <div>
      <PageHeader
        title={data?.device.displayName ?? '设备详情'}
        subtitle={deviceId}
        actions={
          <Link to="/devices" className="inline-flex items-center gap-1 text-[13px] text-text-3 hover:text-text-1 outline-none">
            <ArrowLeft className="size-4" aria-hidden /> 返回列表
          </Link>
        }
      />

      {isLoading ? (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-40" />)}</div>
      ) : !data ? (
        <Card className="p-6"><p className="text-center text-[13px] text-text-3">设备不存在</p></Card>
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <Card className="p-4">
            <CardTitle>规格</CardTitle>
            <div className="mt-2 space-y-1.5 text-[13px]">
              {[
                ['设备 ID', data.device.deviceId],
                ['品牌 / 型号', `${data.device.brand} ${data.device.model}`],
                ['系统', data.device.osVersion],
                ['App 版本', data.device.appVersion],
                ['注册时间', formatTime(data.device.registeredAtUtc)],
                ['最近活跃', formatTime(data.device.lastSeenAtUtc)],
              ].map(([k, v]) => (
                <div key={k} className="flex gap-3">
                  <span className="w-20 shrink-0 text-text-3">{k}</span>
                  <span className="mono min-w-0 flex-1 break-all text-text-1">{v}</span>
                </div>
              ))}
            </div>
          </Card>

          <Card className="p-4">
            <CardTitle>数据统计</CardTitle>
            <div className="mt-3 grid grid-cols-4 gap-2 text-center">
              {[
                ['会话', data.stats.sessionCount],
                ['事件', data.stats.eventCount],
                ['位置', data.stats.locationCount],
                ['存储 KB', data.stats.storageEstimateKb],
              ].map(([label, v]) => (
                <div key={label as string} className="rounded-ctl bg-surface py-2">
                  <div className="tnum text-lg font-semibold text-text-1">{Number(v).toLocaleString()}</div>
                  <div className="text-[10px] text-text-4">{label}</div>
                </div>
              ))}
            </div>
            <div className="mt-4">
              <CardTitle className="text-[13px]">7 天在线</CardTitle>
              <div className="mt-2 flex gap-1">
                {(data.healthTimeline ?? []).map((d: string) => {
                  const online = d.endsWith(':online')
                  return (
                    <div
                      key={d}
                      title={d}
                      className={`h-6 flex-1 rounded-[3px] ${online ? 'bg-ok' : 'bg-surface-2'}`}
                    />
                  )
                })}
              </div>
            </div>
          </Card>

          <Card className="p-4 lg:col-span-2">
            <CardTitle>同步历史（最近 10 批）</CardTitle>
            <div className="mt-2 divide-y divide-divider">
              {data.syncHistory.length === 0 ? (
                <EmptyState size="sm" title="暂无同步批次" />
              ) : (
                (data.syncHistory ?? []).map((b: { batchId: string; createdAt: string; acceptedCount: number; status: string }) => (
                  <div key={b.batchId} className="flex items-center gap-3 py-2 text-[13px]">
                    <span className="mono min-w-0 flex-1 truncate text-text-3">{b.batchId}</span>
                    <span className="tnum text-text-2">{b.acceptedCount} 条</span>
                    <StatusBadge tone={b.status === 'completed' ? 'ok' : b.status === 'failed' ? 'crit' : 'warn'} dot={false}>{b.status}</StatusBadge>
                    <span className="tnum ml-auto text-text-4">{formatTime(b.createdAt)}</span>
                  </div>
                ))
              )}
            </div>
          </Card>
        </div>
      )}
    </div>
  )
}
