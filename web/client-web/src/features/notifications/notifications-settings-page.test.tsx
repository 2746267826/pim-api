// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type {
  NotificationDeliveryList,
  NotificationList,
  NotificationPreferences,
  NotificationSubscription,
} from './api'

const fixture = vi.hoisted(() => ({
  api: {
    capabilities: vi.fn(),
    preferences: vi.fn(),
    updatePreferences: vi.fn(),
    subscriptions: vi.fn(),
    subscribe: vi.fn(),
    revoke: vi.fn(),
    list: vi.fn(),
    markRead: vi.fn(),
    markAllRead: vi.fn(),
    deliveries: vi.fn(),
    retryDelivery: vi.fn(),
    test: vi.fn(),
  },
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
}))

vi.mock('./api', () => ({ notificationApi: fixture.api }))
vi.mock('@/lib/notify', () => ({
  notifySuccess: fixture.notifySuccess,
  notifyError: fixture.notifyError,
}))
vi.mock('@/lib/datetime', () => ({ formatTime: () => '2026-11-26 08:00' }))
vi.mock('@/lib/apiBase', () => ({ apiUrl: (path: string) => path }))
vi.mock('@/api/client', () => ({ getAccessToken: () => 'test-token' }))
vi.mock('react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => (
    <a href={to}>{children}</a>
  ),
}))

import { NotificationsSettingsPage } from './notifications-settings-page'

const preferences: NotificationPreferences = {
  inAppEnabled: true,
  pushEnabled: true,
  taskCompleted: true,
  noteTranscriptionCompleted: true,
  noteDerivedCompleted: true,
  calendarReminder: true,
  updatedAt: '2026-11-26T00:00:00Z',
}

const activeSubscription: NotificationSubscription = {
  id: '11111111-1111-4111-8111-111111111111',
  deviceId: 'browser-a',
  channel: 'WebPush',
  endpointHost: 'fcm.googleapis.com',
  status: 'Active',
  createdAt: '2026-11-26T00:00:00Z',
  lastSeenAt: '2026-11-26T00:00:00Z',
  revokedAt: null,
}

const notificationList: NotificationList = {
  unreadCount: 1,
  items: [
    {
      id: '22222222-2222-4222-8222-222222222222',
      cursor: 9,
      type: 'calendar.task.completed',
      title: '任务已完成：写周报',
      body: '任务已标记为完成。',
      detailUrl: '/tasks',
      isRead: false,
      createdAt: '2026-11-26T00:00:00Z',
      readAt: null,
    },
  ],
}

const deliveries: NotificationDeliveryList = {
  items: [
    {
      id: '33333333-3333-4333-8333-333333333333',
      notificationId: '22222222-2222-4222-8222-222222222222',
      type: 'calendar.task.completed',
      title: '任务已完成：写周报',
      channel: 'WebPush',
      deviceId: 'browser-a',
      status: 'DeadLetter',
      attemptCount: 3,
      nextAttemptAt: '2026-11-26T00:00:00Z',
      lastErrorCode: 'retry_exhausted',
      lastErrorMessage: '多次重试仍未送达，已进入死信',
      createdAt: '2026-11-26T00:00:00Z',
      deliveredAt: null,
      deadLetteredAt: '2026-11-26T00:00:00Z',
    },
  ],
}

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: 0 } },
  })
  return render(
    <QueryClientProvider client={client}>
      <NotificationsSettingsPage />
    </QueryClientProvider>,
  )
}

describe('NotificationsSettingsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperties(HTMLElement.prototype, {
      hasPointerCapture: { configurable: true, value: () => false },
      setPointerCapture: { configurable: true, value: () => undefined },
      releasePointerCapture: { configurable: true, value: () => undefined },
    })
    fixture.api.capabilities.mockResolvedValue({
      webPushConfigured: true,
      webPushPublicKey: 'test-public-key',
      sseEnabled: true,
      eventTypes: ['calendar.task.completed'],
    })
    fixture.api.preferences.mockResolvedValue({ ...preferences })
    fixture.api.updatePreferences.mockImplementation(async (body) => ({
      ...preferences,
      ...body,
    }))
    fixture.api.subscriptions.mockResolvedValue([activeSubscription])
    fixture.api.list.mockResolvedValue(notificationList)
    fixture.api.deliveries.mockResolvedValue(deliveries)
    fixture.api.markRead.mockResolvedValue({ id: notificationList.items[0].id, readAt: '2026-11-26T00:00:00Z' })
    fixture.api.markAllRead.mockResolvedValue({ updated: 1 })
    fixture.api.revoke.mockResolvedValue({ ...activeSubscription, status: 'Revoked' })
    fixture.api.retryDelivery.mockResolvedValue({ ...deliveries.items[0], status: 'Pending' })
  })

  afterEach(() => cleanup())

  it('loads preferences and saves a partial update when a category is switched off', async () => {
    const user = userEvent.setup()
    renderPage()

    const taskSwitch = await screen.findByRole('switch', { name: '任务完成' })
    expect(taskSwitch.getAttribute('data-state')).toBe('checked')
    await user.click(taskSwitch)

    await waitFor(() =>
      expect(fixture.api.updatePreferences).toHaveBeenCalledWith({
        inAppEnabled: true,
        pushEnabled: true,
        taskCompleted: false,
        noteTranscriptionCompleted: true,
        noteDerivedCompleted: true,
        calendarReminder: true,
      }, expect.any(String)),
    )
    await waitFor(() =>
      expect(fixture.notifySuccess).toHaveBeenCalledWith('通知偏好已保存'),
    )
  })

  it('shows the in-app notification and exposes dead-letter retry with the safe error text', async () => {
    const user = userEvent.setup()
    renderPage()

    expect((await screen.findAllByText('任务已完成：写周报')).length).toBe(2)
    expect(screen.getByText('1 条未读')).toBeTruthy()
    expect(screen.getByText('retry_exhausted：多次重试仍未送达，已进入死信')).toBeTruthy()

    await user.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() =>
      expect(fixture.api.retryDelivery).toHaveBeenCalledWith(
        deliveries.items[0].id,
        expect.anything(),
      ),
    )
  })

  it('revokes an active subscription only through the subscription action', async () => {
    const user = userEvent.setup()
    renderPage()

    expect(await screen.findByText('fcm.googleapis.com')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: /撤销订阅 fcm\.googleapis\.com/ }))

    await waitFor(() =>
      expect(fixture.api.revoke).toHaveBeenCalledWith(
        activeSubscription.id,
        expect.anything(),
      ),
    )
  })

  it('degrades safely when the server has no Web Push provider', async () => {
    fixture.api.capabilities.mockResolvedValue({
      webPushConfigured: false,
      webPushPublicKey: null,
      sseEnabled: true,
      eventTypes: [],
    })
    fixture.api.subscriptions.mockResolvedValue([])
    renderPage()

    expect(await screen.findByText('推送通道安全降级')).toBeTruthy()
    const enable = screen.getByRole('button', { name: '启用浏览器推送' })
    expect((enable as HTMLButtonElement).disabled).toBe(true)
    expect(fixture.api.subscriptions).toHaveBeenCalled()
  })

  it('marks all notifications read through the real endpoint', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: /全部已读/ }))
    await waitFor(() => expect(fixture.api.markAllRead).toHaveBeenCalledTimes(1))
    await waitFor(() =>
      expect(fixture.notifySuccess).toHaveBeenCalledWith('已标记 1 条通知为已读'),
    )
  })
})
