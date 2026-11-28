// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

const fixture = vi.hoisted(() => ({
  token: 'token-account-a',
  clearTokens: vi.fn(),
  setTokens: vi.fn(),
  me: vi.fn(),
  login: vi.fn(),
  revokeCurrent: vi.fn(),
  notifyError: vi.fn(),
}))

vi.mock('@/api/client', () => {
  class ApiError extends Error {
    readonly code: number
    readonly status: number
    constructor(message: string, code = -1, status = 0) {
      super(message)
      this.code = code
      this.status = status
    }
  }
  return {
    ApiError,
    getAccessToken: () => fixture.token,
    clearTokens: fixture.clearTokens,
    setTokens: fixture.setTokens,
  }
})
vi.mock('@/api/auth', () => ({
  authApi: { me: fixture.me, login: fixture.login },
}))
vi.mock('@/lib/host-channel', () => ({ detectHostChannel: () => 'browser' }))
vi.mock('@/lib/host-session', () => ({ getHostSession: () => null }))
vi.mock('@/lib/notify', () => ({ notifyError: fixture.notifyError }))
vi.mock('@/features/notifications/api', () => ({
  notificationApi: { revokeCurrent: fixture.revokeCurrent },
}))

import { AuthProvider, useAuth } from './auth-context'

function Probe() {
  const { user, logout } = useAuth()
  return (
    <div>
      <div>{user?.username ?? 'anonymous'}</div>
      <button type="button" onClick={logout}>退出登录</button>
    </div>
  )
}

describe('AuthProvider browser logout', () => {
  const originalServiceWorker = Object.getOwnPropertyDescriptor(navigator, 'serviceWorker')
  const unsubscribe = vi.fn()
  const getRegistration = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
    fixture.token = 'token-account-a'
    fixture.me.mockResolvedValue({
      id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      username: 'alice',
      displayName: 'Alice',
      role: 'user',
    })
    unsubscribe.mockResolvedValue(true)
    getRegistration.mockResolvedValue({
      pushManager: {
        getSubscription: async () => ({
          endpoint: 'https://fcm.googleapis.com/wp/account-a',
          unsubscribe,
          toJSON: () => ({
            endpoint: 'https://fcm.googleapis.com/wp/account-a',
            keys: { p256dh: 'p256dh', auth: 'auth' },
          }),
        }),
      },
    })
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { getRegistration },
    })
    fixture.revokeCurrent.mockResolvedValue({ revoked: 1 })
  })

  afterEach(() => {
    cleanup()
    if (originalServiceWorker) {
      Object.defineProperty(navigator, 'serviceWorker', originalServiceWorker)
    } else {
      delete (navigator as { serviceWorker?: unknown }).serviceWorker
    }
  })

  it('unsubscribes the browser endpoint and revokes it through the shared auth channel', async () => {
    const user = userEvent.setup()
    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )

    expect(await screen.findByText('alice')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: '退出登录' }))

    await waitFor(() => expect(unsubscribe).toHaveBeenCalled())
    await waitFor(() =>
      expect(fixture.revokeCurrent).toHaveBeenCalledWith(
        { deviceId: expect.any(String) },
      ),
    )
    expect(fixture.clearTokens).toHaveBeenCalled()
    expect(fixture.revokeCurrent.mock.invocationCallOrder[0])
      .toBeLessThan(fixture.clearTokens.mock.invocationCallOrder[0])
  })

  it('keeps a retry marker when server revocation fails and still clears local tokens', async () => {
    const user = userEvent.setup()
    fixture.revokeCurrent.mockRejectedValue(new Error('network'))
    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )

    expect(await screen.findByText('alice')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: '退出登录' }))

    await waitFor(() => expect(fixture.revokeCurrent).toHaveBeenCalled())
    expect(fixture.clearTokens).toHaveBeenCalled()
    await waitFor(() => {
      const raw = localStorage.getItem('pim.pendingPushRevocations')
      expect(raw).toBeTruthy()
      expect(JSON.parse(raw!)).toEqual([
        {
          deviceId: expect.any(String),
          userId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
        },
      ])
    })
  })
})
