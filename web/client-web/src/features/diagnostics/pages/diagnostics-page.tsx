import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, RefreshCw, ShieldAlert, X } from 'lucide-react'
import {
  Button,
  Card,
  CardSubtitle,
  CardTitle,
  EmptyState,
  InlineAlert,
  PageHeader,
  Skeleton,
  StatusBadge,
  Textarea,
  type Tone,
} from '@/components/ui'
import { downloadBlob } from '@/lib/download'
import { formatTime } from '@/lib/datetime'
import { diagnosticsApi } from '../api'
import { diagnosticQueue } from '../queue'
import type { DiagnosticEvent, DiagnosticSeverity } from '../types'

const MAX_DOWNLOAD_BYTES = 16 * 1024 * 1024

const PLATFORM_LABEL: Record<string, string> = {
  android: 'Android',
  windows: 'Windows',
  web: 'Web',
}

const KIND_LABEL: Record<string, string> = {
  crash: '崩溃',
  error: '错误',
  warning: '警告',
  manual: '手动报告',
}

const SEVERITY_LABEL: Record<DiagnosticSeverity, string> = {
  warning: '警告',
  error: '错误',
  fatal: '严重',
}

const SEVERITY_TONE: Record<DiagnosticSeverity, Tone> = {
  warning: 'warn',
  error: 'crit',
  fatal: 'crit',
}

function deviceKey(device: { platform: string; deviceId: string }): string {
  return `${device.platform}:${device.deviceId}`
}

function splitDeviceKey(key: string): { platform?: string; deviceId?: string } {
  if (!key) return {}
  const separator = key.indexOf(':')
  if (separator <= 0) return {}
  return {
    platform: key.slice(0, separator),
    deviceId: key.slice(separator + 1) || undefined,
  }
}

export function DiagnosticsPage() {
  const queryClient = useQueryClient()
  const [selectedDeviceKey, setSelectedDeviceKey] = useState('')
  const [days, setDays] = useState(7)
  const [description, setDescription] = useState('')
  const [formMessage, setFormMessage] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)
  const [exportNotice, setExportNotice] = useState<string | null>(null)
  const exportController = useRef<AbortController | null>(null)

  const queue = useSyncExternalStore(
    diagnosticQueue.subscribe,
    diagnosticQueue.getSnapshot,
    diagnosticQueue.getSnapshot,
  )

  const deviceFilter = splitDeviceKey(selectedDeviceKey)
  const overview = useQuery({
    queryKey: ['diagnostics', 'overview', selectedDeviceKey],
    queryFn: ({ signal }) => diagnosticsApi.overview(
      {
        deviceId: deviceFilter.deviceId,
        platform: deviceFilter.platform,
        limit: 100,
      },
      signal,
    ),
  })

  useEffect(() => () => exportController.current?.abort(), [])

  const selectedDevice = overview.data?.devices?.find(
    (device) => deviceKey(device) === selectedDeviceKey,
  )

  function submitManualReport() {
    setFormError(null)
    setFormMessage(null)
    if (!description.trim()) {
      setFormError('请先填写问题描述。')
      return
    }

    const queued = diagnosticQueue.enqueue({
      kind: 'manual',
      severity: 'warning',
      message: description,
      attributes: {
        route: window.location.pathname,
        source: 'web-diagnostics-page',
      },
    })
    if (!queued) {
      setFormError('诊断队列尚未就绪，请重新登录后再试。')
      return
    }

    setDescription('')
    setFormMessage('已加入本机持久队列；离线时会自动退避重试。')
    void diagnosticQueue.flush().then(() => {
      void queryClient.invalidateQueries({ queryKey: ['diagnostics'] })
    })
  }

  async function exportDiagnostics() {
    exportController.current?.abort()
    const controller = new AbortController()
    exportController.current = controller
    setExporting(true)
    setExportError(null)
    setExportNotice(null)

    try {
      const blob = await diagnosticsApi.exportZip(
        {
          deviceId: deviceFilter.deviceId,
          platform: deviceFilter.platform,
          days,
        },
        controller.signal,
      )
      if (blob.size === 0 || blob.size > MAX_DOWNLOAD_BYTES) {
        throw new Error('诊断包为空或超过 16 MB，请缩小时间范围后重试。')
      }
      const stamp = new Date().toISOString().slice(0, 19).replaceAll(':', '-')
      downloadBlob(`pim-diagnostics-${stamp}.zip`, blob)
      setExportNotice('诊断 ZIP 已生成并开始下载。')
    } catch (error) {
      if (controller.signal.aborted
        || (error instanceof Error && error.name === 'AbortError')) {
        setExportError('导出已取消。')
      } else {
        setExportError(error instanceof Error ? error.message : '诊断导出失败，请重试。')
      }
    } finally {
      if (exportController.current === controller) {
        exportController.current = null
        setExporting(false)
      }
    }
  }

  return (
    <div className="space-y-5">
      <PageHeader
        title="诊断"
        subtitle="查看本账号由 Android、Windows 与 Web 主动上报的错误，并一键导出已脱敏诊断包"
        actions={
          <Button
            variant="secondary"
            size="sm"
            loading={overview.isFetching}
            onClick={() => void queryClient.invalidateQueries({ queryKey: ['diagnostics'] })}
          >
            <RefreshCw className="size-4" aria-hidden /> 刷新
          </Button>
        }
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.4fr)_minmax(320px,0.8fr)]">
        <Card className="p-4">
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>最近诊断事件</CardTitle>
            {overview.data && (
              <StatusBadge tone="neutral" dot={false} className="ml-auto">
                近 24 小时 {overview.data.last24HoursCount} 条
              </StatusBadge>
            )}
          </div>
          <CardSubtitle className="mt-1">
            事件按登录账号隔离；不同账号即使使用相同设备标识也不会互相可见。
          </CardSubtitle>

          <div className="mt-3 flex flex-wrap items-center gap-2">
            <label className="text-xs text-text-3" htmlFor="diagnostic-device-filter">设备</label>
            <select
              id="diagnostic-device-filter"
              value={selectedDeviceKey}
              onChange={(event) => setSelectedDeviceKey(event.target.value)}
              className="h-8 min-w-[190px] rounded-ctl border border-border bg-bg px-2 text-xs text-text-1 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring"
            >
              <option value="">全部设备（{overview.data?.devices?.length ?? 0}）</option>
              {(overview.data?.devices ?? []).map((device) => (
                <option key={deviceKey(device)} value={deviceKey(device)}>
                  {PLATFORM_LABEL[device.platform] ?? device.platform} · {device.deviceId} · {device.eventCount} 条
                </option>
              ))}
            </select>
            {selectedDevice && (
              <StatusBadge tone="neutral" dot={false}>
                {PLATFORM_LABEL[selectedDevice.platform] ?? selectedDevice.platform}
              </StatusBadge>
            )}
          </div>

          <div className="mt-4 space-y-2">
            {overview.isLoading ? (
              Array.from({ length: 4 }, (_, index) => <Skeleton key={index} className="h-20" />)
            ) : overview.isError ? (
              <InlineAlert tone="crit">无法读取诊断事件，请检查网络或稍后重试。</InlineAlert>
            ) : (overview.data?.items?.length ?? 0) === 0 ? (
              <EmptyState
                size="sm"
                title="暂无诊断事件"
                description="受控错误、崩溃和手动报告会在客户端队列可用时自动出现在这里。"
              />
            ) : (
              (overview.data!.items ?? []).map((event) => <EventRow key={event.id} event={event} />)
            )}
          </div>
        </Card>

        <div className="space-y-4">
          <Card className="p-4">
            <CardTitle>本机上报队列</CardTitle>
            <CardSubtitle className="mt-1">
              Web 事件先写入本机持久队列，离线或服务端限流时按退避重试。重复发送由事件编号幂等收敛。
            </CardSubtitle>
            <div className="mt-3 grid grid-cols-3 gap-2 text-center">
              <QueueMetric label="待发送" value={queue.pendingCount} />
              <QueueMetric label="已接收" value={queue.sentCount} />
              <QueueMetric label="已丢弃" value={queue.droppedCount} />
            </div>
            {queue.nextAttemptAtMs != null && queue.pendingCount > 0 && (
              <p className="mt-2 text-xs text-text-3">
                下次重试：{formatTime(new Date(queue.nextAttemptAtMs).toISOString())}
              </p>
            )}
            {queue.lastError && (
              <p className="mt-2 break-words text-xs text-crit">{queue.lastError}</p>
            )}
            <div className="mt-3 flex flex-wrap gap-2">
              <Button
                size="sm"
                variant="secondary"
                loading={queue.flushing}
                onClick={() => void diagnosticQueue.flush()}
              >
                立即重试
              </Button>
              <Button
                size="sm"
                variant="ghost"
                disabled={queue.pendingCount === 0}
                onClick={() => diagnosticQueue.clearPending()}
              >
                清空本地队列
              </Button>
            </div>
          </Card>

          <Card className="p-4">
            <CardTitle>导出诊断包</CardTitle>
            <CardSubtitle className="mt-1">
              ZIP 只含当前账号可见事件和服务端白名单摘要：版本、运行环境、日志文件计数、任务计数与组件状态。
              不导出日志正文、令牌、GraphEventId、ChangeKey、临时路径或 provider secret。
            </CardSubtitle>
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <label className="text-xs text-text-3" htmlFor="diagnostic-days">时间范围</label>
              <select
                id="diagnostic-days"
                value={days}
                onChange={(event) => setDays(Number(event.target.value))}
                className="h-8 rounded-ctl border border-border bg-bg px-2 text-xs text-text-1 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring"
              >
                {[1, 3, 7, 14, 30].map((value) => (
                  <option key={value} value={value}>{value} 天</option>
                ))}
              </select>
              <span className="text-xs text-text-4">
                {deviceFilter.deviceId
                  ? `${deviceFilter.deviceId} · ${selectedDevice?.platform ?? deviceFilter.platform ?? '设备'}`
                  : '全部设备'}
              </span>
            </div>
            <div className="mt-3 flex flex-wrap gap-2">
              <Button
                variant="primary"
                size="sm"
                disabled={exporting}
                onClick={() => void exportDiagnostics()}
              >
                <Download className="size-4" aria-hidden /> 一键导出 ZIP
              </Button>
              {exporting && (
                <Button
                  variant="danger-soft"
                  size="sm"
                  onClick={() => exportController.current?.abort()}
                >
                  <X className="size-4" aria-hidden /> 取消导出
                </Button>
              )}
              {exportError && !exporting && (
                <Button variant="secondary" size="sm" onClick={() => void exportDiagnostics()}>
                  重试导出
                </Button>
              )}
            </div>
            {exportError && <InlineAlert tone="crit" className="mt-2">{exportError}</InlineAlert>}
            {exportNotice && <InlineAlert tone="ok" className="mt-2">{exportNotice}</InlineAlert>}
          </Card>

          <Card className="p-4">
            <div className="flex items-center gap-2">
              <ShieldAlert className="size-4 text-text-3" aria-hidden />
              <CardTitle>手动报告问题</CardTitle>
            </div>
            <label htmlFor="diagnostic-description" className="mt-3 block text-xs text-text-3">
              问题描述
            </label>
            <Textarea
              id="diagnostic-description"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="例如：打开笔记页后白屏；可附上错误码或操作步骤。请勿粘贴令牌或密钥。"
              className="mt-1 min-h-[96px]"
            />
            {formError && <InlineAlert tone="crit" className="mt-2">{formError}</InlineAlert>}
            {formMessage && <InlineAlert tone="ok" className="mt-2">{formMessage}</InlineAlert>}
            <Button
              variant="secondary"
              size="sm"
              className="mt-3"
              onClick={submitManualReport}
            >
              提交诊断报告
            </Button>
          </Card>
        </div>
      </div>
    </div>
  )
}

function EventRow({ event }: { event: DiagnosticEvent }) {
  const severity = event.severity as DiagnosticSeverity
  return (
    <div className="rounded-card border border-border px-3 py-2.5">
      <div className="flex flex-wrap items-center gap-2">
        <StatusBadge tone={SEVERITY_TONE[severity] ?? 'neutral'} dot={false}>
          {SEVERITY_LABEL[severity] ?? event.severity}
        </StatusBadge>
        <span className="text-[13px] font-medium text-text-1">
          {KIND_LABEL[event.kind] ?? event.kind}
        </span>
        <span className="mono ml-auto text-[11px] text-text-4">
          {PLATFORM_LABEL[event.platform] ?? event.platform} · {event.deviceId}
        </span>
      </div>
      <p className="mt-1.5 break-words text-[13px] text-text-2">{event.message}</p>
      <p className="tnum mt-1 text-[11px] text-text-4">
        发生 {formatTime(event.occurredAtUtc)} · 接收 {formatTime(event.receivedAtUtc)} · 客户端 {event.appVersion || '未知'}
      </p>
    </div>
  )
}

function QueueMetric({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-ctl bg-surface py-2">
      <div className="tnum text-base font-semibold text-text-1">{value}</div>
      <div className="text-[10px] text-text-4">{label}</div>
    </div>
  )
}
