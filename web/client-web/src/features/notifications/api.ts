import { apiDelete, apiGet, apiPost, apiPut } from '@/api/client'

export interface NotificationPreferences {
  inAppEnabled: boolean
  pushEnabled: boolean
  taskCompleted: boolean
  noteTranscriptionCompleted: boolean
  noteDerivedCompleted: boolean
  calendarReminder: boolean
  updatedAt: string
}

export interface NotificationCapabilities {
  webPushConfigured: boolean
  webPushPublicKey: string | null
  sseEnabled: boolean
  eventTypes: string[]
}

export interface NotificationSubscription {
  id: string
  deviceId: string
  channel: string
  endpointHost: string
  status: string
  createdAt: string
  lastSeenAt: string | null
  revokedAt: string | null
}

export interface NotificationItem {
  id: string
  cursor: number
  type: string
  title: string
  body: string
  detailUrl: string | null
  isRead: boolean
  createdAt: string
  readAt: string | null
}

export interface NotificationList {
  items: NotificationItem[]
  unreadCount: number
}

export interface NotificationDelivery {
  id: string
  notificationId: string
  type: string
  title: string
  channel: string
  deviceId: string
  status: string
  attemptCount: number
  nextAttemptAt: string
  lastErrorCode: string | null
  lastErrorMessage: string | null
  createdAt: string
  deliveredAt: string | null
  deadLetteredAt: string | null
}

export interface NotificationDeliveryList {
  items: NotificationDelivery[]
}

const DEVICE_HEADER = 'X-Pim-Device-Id'

function deviceOptions(deviceId?: string) {
  return deviceId ? { headers: { [DEVICE_HEADER]: deviceId } } : undefined
}

export const notificationApi = {
  capabilities: () =>
    apiGet<NotificationCapabilities>('/api/v1/notifications/capabilities'),
  preferences: (deviceId?: string) =>
    apiGet<NotificationPreferences>(
      '/api/v1/notifications/preferences',
      deviceOptions(deviceId),
    ),
  updatePreferences: (
    body: Partial<Omit<NotificationPreferences, 'updatedAt'>>,
    deviceId?: string,
  ) =>
    apiPut<NotificationPreferences>(
      '/api/v1/notifications/preferences',
      body,
      deviceOptions(deviceId),
    ),
  subscriptions: () =>
    apiGet<NotificationSubscription[]>('/api/v1/notifications/subscriptions'),
  subscribe: (body: {
    deviceId: string
    channel: string
    endpoint: string
    keys: { p256dh: string; auth: string }
  }) => apiPost<NotificationSubscription>('/api/v1/notifications/subscriptions', body),
  revoke: (id: string) =>
    apiDelete<NotificationSubscription>(`/api/v1/notifications/subscriptions/${encodeURIComponent(id)}`),
  list: (limit = 50) =>
    apiGet<NotificationList>(`/api/v1/notifications?limit=${limit}`),
  markRead: (id: string) =>
    apiPost<{ id: string; readAt: string | null }>(
      `/api/v1/notifications/${encodeURIComponent(id)}/read`,
      {},
    ),
  markAllRead: () =>
    apiPost<{ updated: number }>('/api/v1/notifications/read-all', {}),
  deliveries: (limit = 50) =>
    apiGet<NotificationDeliveryList>(`/api/v1/notifications/deliveries?limit=${limit}`),
  retryDelivery: (id: string) =>
    apiPost<NotificationDelivery>(
      `/api/v1/notifications/deliveries/${encodeURIComponent(id)}/retry`,
      {},
    ),
  test: () =>
    apiPost<{ enqueued: boolean; message: string }>('/api/v1/notifications/test', {}),
}
