// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const fixture = vi.hoisted(() => ({
  revokeCurrent: vi.fn(),
}))

vi.mock('@/api/client', () => ({
  getAccessToken: () => 'token-account-a',
}))
vi.mock('./api', () => ({
  notificationApi: { revokeCurrent: fixture.revokeCurrent },
}))
vi.mock('./device', () => ({ notificationDeviceId: () => 'device-browser-a' }))

import {
  flushPendingPushRevocations,
  pendingPushRevocations,
  revokeBrowserPushOnLogout,
} from './logout'

describe('browser push logout revocation', () => {
  const originalServiceWorker = Object.getOwnPropertyDescriptor(navigator, 'serviceWorker')
  const unsubscribe = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
    unsubscribe.mockResolvedValue(true)
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: {
        getRegistration: async () => ({
          pushManager: {
            getSubscription: async () => ({
              endpoint: 'https://fcm.googleapis.com/wp/account-a',
              unsubscribe,
            }),
          },
        }),
      },
    })
  })

  afterEach(() => {
    localStorage.clear()
    if (originalServiceWorker) {
      Object.defineProperty(navigator, 'serviceWorker', originalServiceWorker)
    } else {
      delete (navigator as { serviceWorker?: unknown }).serviceWorker
    }
  })

  it('queues the device id when the server revocation fails so the next login can retry', async () => {
    fixture.revokeCurrent.mockRejectedValue(new Error('network'))

    await expect(revokeBrowserPushOnLogout()).resolves.toBeUndefined()

    expect(pendingPushRevocations()).toEqual(['device-browser-a'])
    expect(localStorage.getItem('pim.pendingPushRevocations'))
      .not.toContain('fcm.googleapis.com')
  })

  it('clears the queue only after a successful retry', async () => {
    fixture.revokeCurrent.mockRejectedValueOnce(new Error('network'))
    await revokeBrowserPushOnLogout()

    fixture.revokeCurrent.mockRejectedValueOnce(new Error('still offline'))
    await flushPendingPushRevocations()
    expect(pendingPushRevocations()).toEqual(['device-browser-a'])

    fixture.revokeCurrent.mockResolvedValue({ revoked: 1 })
    await flushPendingPushRevocations()
    expect(pendingPushRevocations()).toEqual([])
    expect(fixture.revokeCurrent).toHaveBeenLastCalledWith({ deviceId: 'device-browser-a' })
  })

  it('starts the server revocation before awaiting the service worker', async () => {
    fixture.revokeCurrent.mockResolvedValue({ revoked: 1 })
    let releaseRegistration!: () => void
    const registrationGate = new Promise<void>((resolve) => {
      releaseRegistration = resolve
    })
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: {
        getRegistration: async () => {
          await registrationGate
          return null
        },
      },
    })

    const logout = revokeBrowserPushOnLogout()
    expect(fixture.revokeCurrent).toHaveBeenCalledWith({ deviceId: 'device-browser-a' })

    releaseRegistration()
    await logout
  })

  it('does not revoke a queued device for a different account', async () => {
    fixture.revokeCurrent.mockRejectedValue(new Error('network'))
    await revokeBrowserPushOnLogout('user-a')
    expect(pendingPushRevocations()).toEqual(['device-browser-a'])

    fixture.revokeCurrent.mockReset()
    fixture.revokeCurrent.mockResolvedValue({ revoked: 1 })
    await flushPendingPushRevocations('user-b')
    expect(fixture.revokeCurrent).not.toHaveBeenCalled()
    expect(pendingPushRevocations()).toEqual(['device-browser-a'])

    await flushPendingPushRevocations('user-a')
    expect(fixture.revokeCurrent).toHaveBeenCalledWith({ deviceId: 'device-browser-a' })
    expect(pendingPushRevocations()).toEqual([])
  })

  it('does not queue a device after a successful logout revocation', async () => {
    fixture.revokeCurrent.mockResolvedValue({ revoked: 1 })

    await revokeBrowserPushOnLogout()

    expect(fixture.revokeCurrent).toHaveBeenCalledWith({ deviceId: 'device-browser-a' })
    expect(pendingPushRevocations()).toEqual([])
  })
})
