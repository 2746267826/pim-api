import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { STORAGE_KEYS } from '@/lib/storage'
import { DiagnosticQueue, type DiagnosticQueueStorage } from './queue'
import type { DiagnosticEventUploadRequest, DiagnosticUploadResult } from './types'

class MemoryStorage implements DiagnosticQueueStorage {
  readonly values = new Map<string, string>()

  getItem(key: string): string | null {
    return this.values.get(key) ?? null
  }

  setItem(key: string, value: string): void {
    this.values.set(key, value)
  }

  removeItem(key: string): void {
    this.values.delete(key)
  }
}

function accepted(request: DiagnosticEventUploadRequest): DiagnosticUploadResult {
  return {
    acceptedCount: request.events.length,
    skippedCount: 0,
    rejectedCount: 0,
    items: request.events.map((event) => ({
      clientEventId: event.clientEventId,
      outcome: 'accepted',
      code: '0',
    })),
  }
}

describe('DiagnosticQueue', () => {
  it('persists pending events and reuses the same idempotency key after a failed send', async () => {
    const storage = new MemoryStorage()
    let now = Date.parse('2026-11-25T12:00:00Z')
    const requests: DiagnosticEventUploadRequest[] = []
    const first = new DiagnosticQueue()
    let fail = true
    first.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      now: () => now,
      baseBackoffMs: 2_000,
      send: async (request) => {
        requests.push(request)
        if (fail) throw new TypeError('offline')
        return accepted(request)
      },
    })
    first.enqueue({ kind: 'error', severity: 'error', message: 'render failed' })
    await first.flush()

    expect(first.getSnapshot().pendingCount).toBe(1)
    expect(requests).toHaveLength(1)
    const firstId = requests[0]!.events[0]!.clientEventId

    const second = new DiagnosticQueue()
    fail = false
    second.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      now: () => now,
      send: async (request) => {
        requests.push(request)
        return accepted(request)
      },
    })
    expect(second.getSnapshot().pendingCount).toBe(1)

    now += 3_000
    await second.flush()

    expect(requests).toHaveLength(2)
    expect(requests[1]!.events[0]!.clientEventId).toBe(firstId)
    expect(second.getSnapshot().pendingCount).toBe(0)
    expect(second.getSnapshot().sentCount).toBe(1)
  })

  it('honors Retry-After backoff before retrying a queued event', async () => {
    const storage = new MemoryStorage()
    let now = Date.parse('2026-11-25T12:00:00Z')
    const queue = new DiagnosticQueue()
    let attempts = 0
    queue.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      now: () => now,
      send: async (request) => {
        attempts += 1
        if (attempts === 1) throw new ApiError('达到限额', 42901, 429, 30_000)
        return accepted(request)
      },
    })

    queue.enqueue({ kind: 'manual', severity: 'warning', message: 'quota test' })
    await queue.flush()
    expect(queue.getSnapshot().pendingCount).toBe(1)
    expect(queue.getSnapshot().nextAttemptAtMs).toBe(now + 30_000)

    now += 30_001
    await queue.flush()
    expect(attempts).toBe(2)
    expect(queue.getSnapshot().pendingCount).toBe(0)
  })

  it('keeps events after a 401 and retries them after the session is restored', async () => {
    const storage = new MemoryStorage()
    let now = Date.parse('2026-11-25T12:00:00Z')
    let attempts = 0
    const queue = new DiagnosticQueue()
    queue.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      now: () => now,
      baseBackoffMs: 1_000,
      send: async (request) => {
        attempts += 1
        if (attempts === 1) throw new ApiError('登录已过期', 40101, 401)
        return accepted(request)
      },
    })

    queue.enqueue({ kind: 'error', severity: 'error', message: 'auth retry' })
    await queue.flush()
    expect(queue.getSnapshot().pendingCount).toBe(1)
    expect(queue.getSnapshot().droppedCount).toBe(0)

    now += 1_001
    await queue.flush()
    expect(attempts).toBe(2)
    expect(queue.getSnapshot().pendingCount).toBe(0)
  })

  it('sanitizes persisted events and rejects a tampered device id', async () => {
    const storage = new MemoryStorage()
    const ownerScope = 'server-a|user-a'
    const queueKey = `${STORAGE_KEYS.diagnosticQueuePrefix}${encodeURIComponent(ownerScope)}`
    const deviceKey = `${STORAGE_KEYS.diagnosticDevicePrefix}${encodeURIComponent(ownerScope)}`
    storage.setItem(queueKey, JSON.stringify({
      version: 1,
      events: [
        {
          clientEventId: 'persisted-event',
          kind: 'error',
          severity: 'error',
          occurredAtUtc: '2026-11-25T12:00:00Z',
          appVersion: '1.0.0',
          osVersion: 'web',
          message: 'Authorization: Bearer abcdefghijklmnop',
          stackTrace: null,
          attributes: {
            access_token: 'must-not-survive',
            route: '/diagnostics',
          },
          attemptCount: 0,
          nextAttemptAtMs: 0,
        },
        { malformed: true },
      ],
      sentCount: 0,
      droppedCount: 0,
    }))
    storage.setItem(deviceKey, 'Bearer abcdefghijklmnop')
    const requests: DiagnosticEventUploadRequest[] = []
    const queue = new DiagnosticQueue()
    queue.configure({
      ownerScope,
      appVersion: '1.0.0',
      storage,
      autoFlush: false,
      send: async (request) => {
        requests.push(request)
        return accepted(request)
      },
    })

    expect(queue.getSnapshot().pendingCount).toBe(1)
    await queue.flush()

    expect(requests).toHaveLength(1)
    expect(requests[0]!.deviceId).toMatch(/^web-device-/)
    expect(requests[0]!.deviceId).not.toContain('abcdefghijklmnop')
    expect(requests[0]!.events[0]!.message).not.toContain('abcdefghijklmnop')
    expect(requests[0]!.events[0]!.attributes).toEqual({ route: '/diagnostics' })
  })

  it('redacts secrets before persistence and keeps account queues separate', async () => {
    const storage = new MemoryStorage()
    const requests: DiagnosticEventUploadRequest[] = []
    const sender = async (request: DiagnosticEventUploadRequest) => {
      requests.push(request)
      return accepted(request)
    }
    const queueA = new DiagnosticQueue()
    const queueB = new DiagnosticQueue()
    queueA.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      send: sender,
    })
    queueB.configure({
      ownerScope: 'server-a|user-b',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      send: sender,
    })

    queueA.enqueue({
      kind: 'error',
      severity: 'error',
      message: 'Authorization: Bearer abcdefghijklmnop token=plain-token-secret /tmp/pim/private.txt',
      attributes: {
        route: '/diagnostics',
        access_token: 'must-not-persist',
        '/tmp/pim/attribute-key': 'must-not-persist-either',
      },
    })
    queueB.enqueue({ kind: 'error', severity: 'error', message: 'user-b-only' })

    const storedA = [...storage.values.values()].join('\n')
    expect(storedA).not.toContain('abcdefghijklmnop')
    expect(storedA).not.toContain('/tmp/pim')
    expect(storedA).not.toContain('must-not-persist')
    expect(storedA).not.toContain('plain-token-secret')
    expect(queueA.getSnapshot().pendingCount).toBe(1)
    expect(queueB.getSnapshot().pendingCount).toBe(1)

    await queueA.flush()
    await queueB.flush()
    expect(requests).toHaveLength(2)
    expect(requests[0]!.deviceId).not.toBe(requests[1]!.deviceId)
    expect(requests[0]!.events[0]!.message).not.toContain('abcdefghijklmnop')
    expect(requests[1]!.events[0]!.message).toBe('user-b-only')
  })

  it('does not let an in-flight account flush contaminate a newly configured account', async () => {
    const storage = new MemoryStorage()
    const requests: DiagnosticEventUploadRequest[] = []
    let resolveFirst: ((result: DiagnosticUploadResult) => void) | undefined
    const queue = new DiagnosticQueue()
    queue.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      send: async (request) => {
        requests.push(request)
        if (requests.length === 1) {
          return new Promise<DiagnosticUploadResult>((resolve) => {
            resolveFirst = resolve
          })
        }
        return accepted(request)
      },
    })
    queue.enqueue({ kind: 'error', severity: 'error', message: 'user-a-in-flight' })
    const firstFlush = queue.flush()
    await Promise.resolve()

    queue.configure({
      ownerScope: 'server-a|user-b',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      send: async (request) => {
        requests.push(request)
        return accepted(request)
      },
    })
    queue.enqueue({ kind: 'error', severity: 'error', message: 'user-b-pending' })
    resolveFirst?.(accepted(requests[0]!))
    await firstFlush

    expect(queue.getSnapshot().pendingCount).toBe(1)
    expect(queue.getSnapshot().sentCount).toBe(0)
    await queue.flush()
    expect(queue.getSnapshot().pendingCount).toBe(0)
    expect(queue.getSnapshot().sentCount).toBe(1)
    expect(requests[1]!.events[0]!.message).toBe('user-b-pending')
  })

  it('caps the persistent queue and reports dropped events', () => {
    const storage = new MemoryStorage()
    const queue = new DiagnosticQueue()
    queue.configure({
      ownerScope: 'server-a|user-a',
      appVersion: '0.1.0',
      storage,
      autoFlush: false,
      send: async (request) => accepted(request),
    })

    for (let index = 0; index < 105; index += 1) {
      queue.enqueue({ kind: 'error', severity: 'error', message: `event-${index}` })
    }

    expect(queue.getSnapshot().pendingCount).toBe(100)
    expect(queue.getSnapshot().droppedCount).toBe(5)
    expect(storage.values.size).toBeGreaterThanOrEqual(2)
  })
})
