import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bell, BellRing, CheckCheck, RefreshCw, Send, Trash2, Wifi, WifiOff } from 'lucide-react'
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
  Switch,
} from '@/components/ui'
import { apiUrl } from '@/lib/apiBase'
import { getAccessToken } from '@/api/client'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { cn } from '@/lib/utils'
import {
  notificationApi,
  type NotificationDelivery,
  type NotificationItem,
  type NotificationList,
  type NotificationPreferences,
} from './api'
import { notificationDeviceId } from './device'
import { SseParser } from './sse'

const DEFAULT_PREFERENCES: Omit<NotificationPreferences, 'updatedAt'> = {
  inAppEnabled: true,
  pushEnabled: true,
  taskCompleted: true,
  noteTranscriptionCompleted: true,
  noteDerivedCompleted: true,
  calendarReminder: true,
}

const PREFERENCE_ROWS: Array<{
  key: keyof typeof DEFAULT_PREFERENCES
  title: string
  description: string
}> = [
  { key: 'inAppEnabled', title: '站内通知', description: '账号级通知中心（所有设备共享）' },
  { key: 'pushEnabled', title: '浏览器推送', description: '本设备通过浏览器接收系统通知' },
  { key: 'taskCompleted', title: '任务完成', description: '本设备接收任务标记为 COMPLETED 的推送' },
  { key: 'noteTranscriptionCompleted', title: '转写完成', description: '本设备接收笔记语音转写完成的推送' },
  { key: 'noteDerivedCompleted', title: '整理完成', description: '本设备接收完整记录与独立分析生成的推送' },
  { key: 'calendarReminder', title: '日历提醒', description: '本设备接收提醒到达预定时间的推送' },
]

function base64UrlBytes(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/')
  const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4)
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index++)
    bytes[index] = binary.charCodeAt(index)
  return bytes
}

function pushSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window
  )
}

function statusTone(status: string) {
  if (status === 'Delivered') return 'ok' as const
  if (status === 'Pending' || status === 'Retrying' || status === 'Processing') return 'info' as const
  if (status === 'DeadLetter') return 'crit' as const
  return 'warn' as const
}

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback
}

export function NotificationsSettingsPage() {
  const qc = useQueryClient()
  const currentDeviceId = notificationDeviceId()
  const capabilities = useQuery({
    queryKey: ['notifications', 'capabilities'],
    queryFn: notificationApi.capabilities,
  })
  const preferences = useQuery({
    queryKey: ['notifications', 'preferences', currentDeviceId],
    queryFn: () => notificationApi.preferences(currentDeviceId),
  })
  const subscriptions = useQuery({
    queryKey: ['notifications', 'subscriptions'],
    queryFn: notificationApi.subscriptions,
  })
  const notifications = useQuery({
    queryKey: ['notifications', 'list'],
    queryFn: () => notificationApi.list(50),
  })
  const deliveries = useQuery({
    queryKey: ['notifications', 'deliveries'],
    queryFn: () => notificationApi.deliveries(50),
  })

  const [preferenceState, setPreferenceState] = useState(DEFAULT_PREFERENCES)
  const [pushBusy, setPushBusy] = useState(false)
  const [sseActive, setSseActive] = useState(false)
  const [sseStatus, setSseStatus] = useState<'idle' | 'connecting' | 'live' | 'error'>('idle')

  useEffect(() => {
    if (!preferences.data) return
    setPreferenceState({
      inAppEnabled: preferences.data.inAppEnabled,
      pushEnabled: preferences.data.pushEnabled,
      taskCompleted: preferences.data.taskCompleted,
      noteTranscriptionCompleted: preferences.data.noteTranscriptionCompleted,
      noteDerivedCompleted: preferences.data.noteDerivedCompleted,
      calendarReminder: preferences.data.calendarReminder,
    })
  }, [preferences.data])

  const savePreferences = useMutation({
    mutationFn: (next: Partial<typeof DEFAULT_PREFERENCES>) =>
      notificationApi.updatePreferences(next, currentDeviceId),
    onSuccess: (data) => {
      qc.setQueryData(['notifications', 'preferences', currentDeviceId], data)
      notifySuccess('通知偏好已保存')
    },
    onError: (error) => notifyError(errorText(error, '保存通知偏好失败')),
  })

  const testNotification = useMutation({
    mutationFn: notificationApi.test,
    onSuccess: async (data) => {
      if (data.enqueued) {
        notifySuccess(data.message)
        await Promise.all([
          qc.invalidateQueries({ queryKey: ['notifications', 'list'] }),
          qc.invalidateQueries({ queryKey: ['notifications', 'deliveries'] }),
        ])
      } else {
        notifyError(data.message)
      }
    },
    onError: (error) => notifyError(errorText(error, '发送测试通知失败')),
  })

  const revoke = useMutation({
    mutationFn: notificationApi.revoke,
    onSuccess: async () => {
      notifySuccess('推送订阅已撤销')
      await qc.invalidateQueries({ queryKey: ['notifications', 'subscriptions'] })
    },
    onError: (error) => notifyError(errorText(error, '撤销推送订阅失败')),
  })

  const retryDelivery = useMutation({
    mutationFn: notificationApi.retryDelivery,
    onSuccess: async () => {
      notifySuccess('已重新排队，稍后自动重试')
      await qc.invalidateQueries({ queryKey: ['notifications', 'deliveries'] })
    },
    onError: (error) => notifyError(errorText(error, '重试投递失败')),
  })

  const markAllRead = useMutation({
    mutationFn: notificationApi.markAllRead,
    onSuccess: async (data) => {
      notifySuccess(`已标记 ${data.updated} 条通知为已读`)
      await qc.invalidateQueries({ queryKey: ['notifications', 'list'] })
    },
    onError: (error) => notifyError(errorText(error, '标记已读失败')),
  })

  const markRead = useMutation({
    mutationFn: notificationApi.markRead,
    onSuccess: async () => qc.invalidateQueries({ queryKey: ['notifications', 'list'] }),
    onError: (error) => notifyError(errorText(error, '标记已读失败')),
  })

  useEffect(() => {
    if (!sseActive) return
    const controller = new AbortController()
    let stopped = false
    let lastCursor: number | null =
      qc.getQueryData<NotificationList>(['notifications', 'list'])?.items[0]?.cursor ?? null
    let refreshedOnConnect = false
    setSseStatus('connecting')

    async function run() {
      while (!stopped && !controller.signal.aborted) {
        try {
          const token = getAccessToken()
          const query = lastCursor === null ? '' : `?after=${lastCursor}`
          const response = await fetch(apiUrl(`/api/v1/notifications/stream${query}`), {
            headers: token ? { Authorization: `Bearer ${token}` } : {},
            signal: controller.signal,
          })
          if (!response.ok || !response.body) throw new Error('事件流连接失败')
          setSseStatus('live')
          if (!refreshedOnConnect) {
            refreshedOnConnect = true
            await qc.invalidateQueries({ queryKey: ['notifications', 'list'] })
          }
          const reader = response.body.getReader()
          const decoder = new TextDecoder()
          const parser = new SseParser()
          while (!stopped) {
            const { value, done } = await reader.read()
            if (done) break
            for (const frame of parser.push(decoder.decode(value, { stream: true }))) {
              if (frame.event !== 'notification') continue
              try {
                const item = JSON.parse(frame.data) as NotificationItem
                lastCursor = item.cursor
                notifySuccess(`新通知：${item.title}`)
                await Promise.all([
                  qc.invalidateQueries({ queryKey: ['notifications', 'list'] }),
                  qc.invalidateQueries({ queryKey: ['notifications', 'deliveries'] }),
                ])
              } catch {
                // 单帧损坏不应终止实时通道。
              }
            }
          }
        } catch (error) {
          if (stopped || controller.signal.aborted) break
          setSseStatus('error')
          await new Promise((resolve) => setTimeout(resolve, 3000))
        }
      }
      if (!stopped) setSseStatus('idle')
    }

    void run()
    return () => {
      stopped = true
      controller.abort()
      setSseStatus('idle')
    }
  }, [sseActive, qc])

  const activeSubscriptions = useMemo(
    () => (subscriptions.data ?? []).filter((item) => item.status === 'Active'),
    [subscriptions.data],
  )
  const currentDeviceSubscriptions = useMemo(
    () => activeSubscriptions.filter((item) => item.deviceId === currentDeviceId),
    [activeSubscriptions, currentDeviceId],
  )
  const unread = notifications.data?.unreadCount ?? 0
  const webPushReady = capabilities.data?.webPushConfigured === true
  const supported = pushSupported()

  async function enablePush() {
    if (!capabilities.data?.webPushPublicKey) {
      notifyError('服务端未配置 Web Push，无法启用浏览器推送')
      return
    }
    if (!supported) {
      notifyError('当前浏览器不支持 Web Push')
      return
    }
    setPushBusy(true)
    try {
      const permission = await Notification.requestPermission()
      if (permission !== 'granted') {
        notifyError('浏览器未授予通知权限')
        return
      }
      const registration = await navigator.serviceWorker.register('/push-sw.js')
      const existing = await registration.pushManager.getSubscription()
      const subscription =
        existing ??
        (await registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: base64UrlBytes(capabilities.data.webPushPublicKey),
        }))
      const json = subscription.toJSON()
      if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) {
        throw new Error('浏览器未返回完整订阅密钥')
      }
      await notificationApi.subscribe({
        deviceId: currentDeviceId,
        channel: 'WebPush',
        endpoint: json.endpoint,
        keys: { p256dh: json.keys.p256dh, auth: json.keys.auth },
      })
      notifySuccess('浏览器推送已启用')
      await qc.invalidateQueries({ queryKey: ['notifications', 'subscriptions'] })
    } catch (error) {
      notifyError(errorText(error, '启用浏览器推送失败'))
    } finally {
      setPushBusy(false)
    }
  }

  async function disablePush() {
    setPushBusy(true)
    try {
      const registration = await navigator.serviceWorker.getRegistration()
      const subscription = await registration?.pushManager.getSubscription()
      await subscription?.unsubscribe()
      const currentDevice = currentDeviceId
      for (const item of activeSubscriptions.filter((value) => value.deviceId === currentDevice)) {
        await notificationApi.revoke(item.id)
      }
      notifySuccess('浏览器推送已关闭')
      await qc.invalidateQueries({ queryKey: ['notifications', 'subscriptions'] })
    } catch (error) {
      notifyError(errorText(error, '关闭浏览器推送失败'))
    } finally {
      setPushBusy(false)
    }
  }

  function updatePreference(key: keyof typeof DEFAULT_PREFERENCES, checked: boolean) {
    const next = { ...preferenceState, [key]: checked }
    setPreferenceState(next)
    savePreferences.mutate(next)
  }

  return (
    <div>
      <PageHeader
        title="通知"
        subtitle="站内通知、浏览器推送与可见的失败重试"
        actions={
          <div className="flex flex-wrap gap-2">
            <Button
              variant="secondary"
              size="sm"
              loading={testNotification.isPending}
              onClick={() => testNotification.mutate()}
            >
              <Send className="size-4" aria-hidden /> 发送测试通知
            </Button>
            <Button
              variant={sseActive ? 'danger' : 'primary'}
              size="sm"
              onClick={() => setSseActive((value) => !value)}
            >
              {sseActive ? <WifiOff className="size-4" aria-hidden /> : <Wifi className="size-4" aria-hidden />}
              {sseActive ? '停止实时接收' : '开始实时接收'}
            </Button>
          </div>
        }
      />

      <div className="max-w-[980px] space-y-4">
        {!webPushReady && (
          <InlineAlert tone="info" title="推送通道安全降级">
            服务端未配置 Web Push 提供方（VAPID）。站内通知与 SSE 实时事件流仍然可用；浏览器推送会显示为跳过，不会反复重试。
          </InlineAlert>
        )}
        {sseStatus === 'error' && (
          <InlineAlert tone="warn" title="实时通道暂时断开">
            正在自动重连；站内通知仍可通过刷新查看。
          </InlineAlert>
        )}

        <Card className="p-4">
          <CardTitle className="flex items-center gap-2">
            <Bell className="size-4 text-primary" aria-hidden /> 通知偏好
          </CardTitle>
          <CardSubtitle className="mt-1">
            站内通知为账号级；浏览器推送与类别开关按当前设备保存，只影响本设备。
          </CardSubtitle>
          {preferences.isLoading ? (
            <div className="mt-3 space-y-2">
              {Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}
            </div>
          ) : (
            <div className="mt-2 divide-y divide-divider">
              {PREFERENCE_ROWS.map((row) => (
                <div key={row.key} className="flex items-center gap-4 py-3">
                  <div className="min-w-0 flex-1">
                    <label htmlFor={`notification-pref-${row.key}`} className="text-[13px] text-text-1">
                      {row.title}
                    </label>
                    <div className="text-xs text-text-3">{row.description}</div>
                  </div>
                  <Switch
                    id={`notification-pref-${row.key}`}
                    checked={preferenceState[row.key]}
                    onCheckedChange={(checked) => updatePreference(row.key, checked)}
                  />
                </div>
              ))}
            </div>
          )}
        </Card>

        <div className="grid gap-4 lg:grid-cols-2">
          <Card className="p-4">
            <CardTitle className="flex items-center gap-2">
              <BellRing className="size-4 text-primary" aria-hidden /> 浏览器推送
            </CardTitle>
            <CardSubtitle className="mt-1">
              需要 HTTPS（或 localhost）与浏览器通知权限。Android FCM / Windows WNS 不由本 Web 入口验证。
            </CardSubtitle>
            <div className="mt-3 space-y-2 text-xs text-text-3">
              <div>当前浏览器：{supported ? '支持 Web Push' : '不支持 Web Push'}</div>
              <div>服务端通道：{webPushReady ? '已配置' : '未配置（安全降级）'}</div>
              <div>实时通道：{sseActive ? (sseStatus === 'live' ? 'SSE 已连接' : 'SSE 连接中') : '未开启'}</div>
            </div>
            {currentDeviceSubscriptions.length > 0 ? (
              <Button
                className="mt-3"
                variant="secondary"
                size="sm"
                loading={pushBusy}
                onClick={() => void disablePush()}
              >
                关闭本设备浏览器推送
              </Button>
            ) : (
              <Button
                className="mt-3"
                variant="primary"
                size="sm"
                loading={pushBusy}
                disabled={!supported || !webPushReady}
                onClick={() => void enablePush()}
              >
                启用浏览器推送
              </Button>
            )}
          </Card>

          <Card className="p-4">
            <div className="flex items-center justify-between gap-2">
              <CardTitle>推送订阅</CardTitle>
              <StatusBadge tone={activeSubscriptions.length > 0 ? 'ok' : 'neutral'} dot={false}>
                {activeSubscriptions.length} 个活跃
              </StatusBadge>
            </div>
            <div className="mt-2 space-y-2">
              {(subscriptions.data ?? []).length === 0 && (
                <div className="text-xs text-text-3">还没有设备订阅。启用浏览器推送后会出现在这里。</div>
              )}
              {(subscriptions.data ?? []).map((item) => (
                <div key={item.id} className="flex items-center gap-2 rounded-ctl border border-border px-3 py-2 text-xs">
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-text-1">{item.endpointHost}</div>
                    <div className="truncate text-text-4">{item.deviceId}</div>
                  </div>
                  <StatusBadge tone={item.status === 'Active' ? 'ok' : 'neutral'} dot={false}>
                    {item.status}
                  </StatusBadge>
                  {item.status === 'Active' && (
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={`撤销订阅 ${item.endpointHost}`}
                      onClick={() => revoke.mutate(item.id)}
                    >
                      <Trash2 className="size-3.5" aria-hidden />
                    </Button>
                  )}
                </div>
              ))}
            </div>
          </Card>
        </div>

        <Card className="p-4">
          <div className="flex items-center justify-between gap-2">
            <CardTitle className="flex items-center gap-2">
              <Bell className="size-4 text-primary" aria-hidden /> 站内通知
              {unread > 0 && <StatusBadge tone="info" dot={false}>{unread} 条未读</StatusBadge>}
            </CardTitle>
            <Button
              variant="ghost"
              size="sm"
              loading={markAllRead.isPending}
              disabled={unread === 0}
              onClick={() => markAllRead.mutate()}
            >
              <CheckCheck className="size-4" aria-hidden /> 全部已读
            </Button>
          </div>
          {notifications.isLoading ? (
            <div className="mt-3 space-y-2">
              {Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-12" />)}
            </div>
          ) : (notifications.data?.items.length ?? 0) === 0 ? (
            <EmptyState title="暂无通知" description="完成任务、等待提醒或点击上方测试按钮即可看到站内通知。" />
          ) : (
            <div className="mt-2 divide-y divide-divider">
              {notifications.data!.items.map((item) => (
                <div key={item.id} className={cn('flex items-start gap-3 py-3', !item.isRead && 'bg-primary/5')}>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="truncate text-[13px] font-medium text-text-1">{item.title}</span>
                      {!item.isRead && <span className="size-1.5 shrink-0 rounded-full bg-primary" aria-label="未读" />}
                    </div>
                    <div className="mt-0.5 whitespace-pre-line text-xs text-text-3">{item.body}</div>
                    <div className="mt-1 flex items-center gap-3 text-xs text-text-4">
                      <span>{formatTime(item.createdAt)}</span>
                      {item.detailUrl && (
                        <Link className="text-primary hover:underline" to={item.detailUrl}>
                          查看详情
                        </Link>
                      )}
                    </div>
                  </div>
                  {!item.isRead && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => markRead.mutate(item.id)}
                    >
                      标记已读
                    </Button>
                  )}
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card className="p-4">
          <div className="flex items-center justify-between gap-2">
            <CardTitle className="flex items-center gap-2">
              <RefreshCw className="size-4 text-primary" aria-hidden /> 推送投递与失败
            </CardTitle>
            <span className="text-xs text-text-4">重试次数、死信与最近错误均为真实队列状态</span>
          </div>
          {deliveries.isLoading ? (
            <div className="mt-3 space-y-2">
              {Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-12" />)}
            </div>
          ) : (deliveries.data?.items.length ?? 0) === 0 ? (
            <EmptyState title="暂无推送投递" description="没有活跃订阅或通知偏好关闭推送时，只会保留站内通知。" />
          ) : (
            <div className="mt-2 divide-y divide-divider">
              {deliveries.data!.items.map((item) => (
                <DeliveryRow
                  key={item.id}
                  item={item}
                  onRetry={() => retryDelivery.mutate(item.id)}
                  retrying={retryDelivery.isPending}
                />
              ))}
            </div>
          )}
        </Card>
      </div>
    </div>
  )
}

function DeliveryRow({
  item,
  onRetry,
  retrying,
}: {
  item: NotificationDelivery
  onRetry: () => void
  retrying: boolean
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 py-3 text-xs">
      <StatusBadge tone={statusTone(item.status)} dot={false}>{item.status}</StatusBadge>
      <span className="min-w-0 flex-1 truncate text-text-1">{item.title}</span>
      <span className="text-text-3">尝试 {item.attemptCount} 次</span>
      <span className="truncate text-text-4">{item.deviceId}</span>
      {item.lastErrorCode && (
        <span className="basis-full text-warn">
          {item.lastErrorCode}：{item.lastErrorMessage ?? '未提供安全错误说明'}
        </span>
      )}
      {(item.status === 'DeadLetter' || item.status === 'Skipped') && (
        <Button variant="secondary" size="sm" loading={retrying} onClick={onRetry}>
          重试
        </Button>
      )}
    </div>
  )
}
