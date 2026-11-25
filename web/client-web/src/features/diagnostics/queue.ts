import { ApiError } from '@/api/client'
import { getApiBase } from '@/lib/apiBase'
import { STORAGE_KEYS, getString, remove, setString } from '@/lib/storage'
import { diagnosticsApi } from './api'
import {
  DIAGNOSTIC_KINDS,
  DIAGNOSTIC_SEVERITIES,
  type DiagnosticEventUploadRequest,
  type DiagnosticEventUploadItem,
  type DiagnosticKind,
  type DiagnosticPlatform,
  type DiagnosticSeverity,
  type DiagnosticUploadResult,
} from './types'
import { redactDiagnosticAttributes, redactDiagnosticText, truncateDiagnosticText } from './redaction'

const MAX_QUEUE_EVENTS = 100
const MAX_QUEUE_BYTES = 512 * 1024
const MAX_BATCH_EVENTS = 20
const MAX_BATCH_BYTES = 180 * 1024
const DEFAULT_BASE_BACKOFF_MS = 2_000
const DEFAULT_MAX_BACKOFF_MS = 5 * 60_000
const FLUSH_INTERVAL_MS = 30_000
const PERMANENT_DROP_STATUSES = new Set([400, 413, 415, 422])

export interface StoredDiagnosticEvent extends DiagnosticEventUploadItem {
  attemptCount: number
  nextAttemptAtMs: number
}

interface StoredQueue {
  version: 1
  events: StoredDiagnosticEvent[]
  sentCount: number
  droppedCount: number
}

export interface DiagnosticQueueSnapshot {
  configured: boolean
  pendingCount: number
  sentCount: number
  droppedCount: number
  flushing: boolean
  nextAttemptAtMs: number | null
  lastError: string | null
}

export interface DiagnosticQueueStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
}

export type DiagnosticBatchSender = (
  request: DiagnosticEventUploadRequest,
  signal?: AbortSignal,
) => Promise<DiagnosticUploadResult>

export interface ConfigureDiagnosticQueueOptions {
  ownerScope: string
  appVersion: string
  deviceId?: string
  osVersion?: string
  storage?: DiagnosticQueueStorage
  send?: DiagnosticBatchSender
  now?: () => number
  baseBackoffMs?: number
  maxBackoffMs?: number
  autoFlush?: boolean
}

export interface EnqueueDiagnosticInput {
  kind: DiagnosticKind
  severity: DiagnosticSeverity
  message: string
  stackTrace?: string | null
  attributes?: Record<string, string>
}

const browserStorage: DiagnosticQueueStorage = {
  getItem: getString,
  setItem: setString,
  removeItem: remove,
}

function queueKey(ownerScope: string): string {
  return `${STORAGE_KEYS.diagnosticQueuePrefix}${encodeURIComponent(ownerScope)}`
}

function deviceKey(ownerScope: string): string {
  return `${STORAGE_KEYS.diagnosticDevicePrefix}${encodeURIComponent(ownerScope)}`
}

function newId(prefix: string): string {
  const uuid = globalThis.crypto?.randomUUID?.()
  return uuid
    ? `${prefix}-${uuid}`
    : `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

function byteLength(value: unknown): number {
  return new TextEncoder().encode(JSON.stringify(value)).byteLength
}

function isKind(value: string): value is DiagnosticKind {
  return (DIAGNOSTIC_KINDS as readonly string[]).includes(value)
}

function isSeverity(value: string): value is DiagnosticSeverity {
  return (DIAGNOSTIC_SEVERITIES as readonly string[]).includes(value)
}

function isStringRecord(value: unknown): value is Record<string, string> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  return Object.values(value).every((item) => typeof item === 'string')
}

function isValidDeviceId(value: string): boolean {
  return value.length > 0
    && value.length <= 128
    && /^[A-Za-z0-9._:-]+$/.test(value)
}

function normalizeStoredEvent(value: unknown): StoredDiagnosticEvent | null {
  if (!value || typeof value !== 'object') return null
  const event = value as Partial<StoredDiagnosticEvent>
  const clientEventId = event.clientEventId
  const kind = event.kind
  const severity = event.severity
  const message = event.message
  const occurredAtUtc = event.occurredAtUtc
  const appVersion = event.appVersion
  const osVersion = event.osVersion
  const stackTrace = event.stackTrace
  const attributes = event.attributes
  const nextAttemptAtMs = event.nextAttemptAtMs
  const attemptCount = event.attemptCount
  if (typeof clientEventId !== 'string'
    || typeof kind !== 'string'
    || typeof severity !== 'string'
    || typeof message !== 'string'
    || typeof occurredAtUtc !== 'string'
    || typeof appVersion !== 'string'
    || typeof osVersion !== 'string'
    || (stackTrace !== undefined && stackTrace !== null && typeof stackTrace !== 'string')
    || (attributes !== undefined && !isStringRecord(attributes))) {
    return null
  }
  if (!isKind(kind) || !isSeverity(severity)) {
    return null
  }
  if (!clientEventId || clientEventId.length > 128 || Number.isNaN(Date.parse(occurredAtUtc))) {
    return null
  }
  if (typeof nextAttemptAtMs !== 'number'
    || !Number.isFinite(nextAttemptAtMs)
    || typeof attemptCount !== 'number'
    || !Number.isFinite(attemptCount)) {
    return null
  }

  const safeMessage = redactDiagnosticText(message, 2_000)
  const safeStackTrace = redactDiagnosticText(stackTrace ?? '', 12_000)
  if (!safeMessage && !safeStackTrace) return null

  return {
    clientEventId: truncateDiagnosticText(clientEventId, 128),
    kind,
    severity,
    occurredAtUtc,
    appVersion: truncateDiagnosticText(appVersion, 64),
    osVersion: truncateDiagnosticText(osVersion, 128),
    message: safeMessage || '(无消息)',
    stackTrace: safeStackTrace || null,
    attributes: redactDiagnosticAttributes(attributes),
    attemptCount: Math.max(0, Math.floor(attemptCount)),
    nextAttemptAtMs: Math.max(0, nextAttemptAtMs),
  }
}

function loadQueue(storage: DiagnosticQueueStorage, key: string): StoredQueue {
  const raw = storage.getItem(key)
  if (!raw) return { version: 1, events: [], sentCount: 0, droppedCount: 0 }
  try {
    const parsed = JSON.parse(raw) as Partial<StoredQueue>
    if (parsed.version !== 1 || !Array.isArray(parsed.events)) throw new Error('invalid queue')
    const events = parsed.events
      .map(normalizeStoredEvent)
      .filter((event): event is StoredDiagnosticEvent => event !== null)
    return {
      version: 1,
      events,
      sentCount: Number.isFinite(parsed.sentCount) ? Math.max(0, parsed.sentCount ?? 0) : 0,
      droppedCount: Number.isFinite(parsed.droppedCount) ? Math.max(0, parsed.droppedCount ?? 0) : 0,
    }
  } catch {
    storage.removeItem(key)
    return { version: 1, events: [], sentCount: 0, droppedCount: 0 }
  }
}

function toUploadItem(event: StoredDiagnosticEvent): DiagnosticEventUploadItem {
  return {
    clientEventId: event.clientEventId,
    kind: event.kind,
    severity: event.severity,
    occurredAtUtc: event.occurredAtUtc,
    appVersion: event.appVersion,
    osVersion: event.osVersion,
    message: event.message,
    stackTrace: event.stackTrace,
    attributes: event.attributes,
  }
}

export class DiagnosticQueue {
  private storage: DiagnosticQueueStorage = browserStorage
  private send: DiagnosticBatchSender = diagnosticsApi.uploadBatch
  private now: () => number = Date.now
  private baseBackoffMs = DEFAULT_BASE_BACKOFF_MS
  private maxBackoffMs = DEFAULT_MAX_BACKOFF_MS
  private autoFlush = true
  private ownerScope: string | null = null
  private deviceId = ''
  private appVersion = ''
  private osVersion = ''
  private queue: StoredQueue = { version: 1, events: [], sentCount: 0, droppedCount: 0 }
  private flushing = false
  private flushPromise: Promise<void> | null = null
  private generation = 0
  private timer: ReturnType<typeof setTimeout> | null = null
  private interval: ReturnType<typeof setInterval> | null = null
  private lastError: string | null = null
  private listeners = new Set<() => void>()
  private snapshot: DiagnosticQueueSnapshot = this.buildSnapshot()

  configure(options: ConfigureDiagnosticQueueOptions): void {
    this.generation += 1
    this.flushPromise = null
    this.flushing = false
    this.storage = options.storage ?? browserStorage
    this.send = options.send ?? diagnosticsApi.uploadBatch
    this.now = options.now ?? Date.now
    this.baseBackoffMs = options.baseBackoffMs ?? DEFAULT_BASE_BACKOFF_MS
    this.maxBackoffMs = options.maxBackoffMs ?? DEFAULT_MAX_BACKOFF_MS
    this.autoFlush = options.autoFlush ?? true
    this.ownerScope = options.ownerScope
    this.appVersion = options.appVersion
    this.osVersion = options.osVersion
      ?? (typeof navigator === 'undefined' ? 'web' : navigator.userAgent.slice(0, 128))
    this.queue = loadQueue(this.storage, queueKey(options.ownerScope))
    this.enforceCaps()
    this.persist()
    this.lastError = null
    this.deviceId = options.deviceId && isValidDeviceId(options.deviceId)
      ? options.deviceId
      : this.loadOrCreateDeviceId(options.ownerScope)
    this.publish()
    if (this.autoFlush) void this.flush()
  }

  detach(): void {
    this.generation += 1
    this.flushPromise = null
    this.flushing = false
    this.ownerScope = null
    this.queue = { version: 1, events: [], sentCount: 0, droppedCount: 0 }
    this.lastError = null
    this.publish()
  }

  getSnapshot = (): DiagnosticQueueSnapshot => this.snapshot

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  enqueue(input: EnqueueDiagnosticInput): StoredDiagnosticEvent | null {
    if (!this.ownerScope) return null
    const message = redactDiagnosticText(input.message, 2_000)
    const stackTrace = redactDiagnosticText(input.stackTrace ?? '', 12_000)
    if (!message && !stackTrace) return null

    const event: StoredDiagnosticEvent = {
      clientEventId: newId('web'),
      kind: isKind(input.kind) ? input.kind : 'error',
      severity: isSeverity(input.severity) ? input.severity : 'error',
      occurredAtUtc: new Date(this.now()).toISOString(),
      appVersion: truncateDiagnosticText(this.appVersion, 64),
      osVersion: truncateDiagnosticText(this.osVersion, 128),
      message: message || '(无消息)',
      stackTrace: stackTrace || null,
      attributes: redactDiagnosticAttributes(input.attributes),
      attemptCount: 0,
      nextAttemptAtMs: 0,
    }

    this.queue.events.push(event)
    this.enforceCaps()
    this.persist()
    this.publish()
    if (this.autoFlush) void this.flush()
    return event
  }

  clearPending(): void {
    if (!this.ownerScope) return
    this.queue.droppedCount += this.queue.events.length
    this.queue.events = []
    this.lastError = null
    this.persist()
    this.publish()
  }

  async flush(signal?: AbortSignal): Promise<void> {
    if (this.flushPromise) return this.flushPromise
    if (!this.ownerScope || this.queue.events.length === 0) return

    const generation = this.generation
    const run = this.runFlush(signal, generation)
    this.flushPromise = run
    try {
      await run
    } finally {
      if (this.flushPromise === run) {
        this.flushPromise = null
      }
      if (generation === this.generation) {
        this.scheduleNext()
      }
    }
  }

  start(): () => void {
    if (typeof window === 'undefined') return () => undefined
    const online = () => void this.flush()
    window.addEventListener('online', online)
    this.interval = setInterval(() => void this.flush(), FLUSH_INTERVAL_MS)
    void this.flush()
    return () => {
      window.removeEventListener('online', online)
      if (this.interval) clearInterval(this.interval)
      this.interval = null
      if (this.timer) clearTimeout(this.timer)
      this.timer = null
    }
  }

  private async runFlush(signal: AbortSignal | undefined, generation: number): Promise<void> {
    const now = this.now()
    const batch: StoredDiagnosticEvent[] = []
    let bytes = 0
    for (const event of this.queue.events) {
      if (event.nextAttemptAtMs > now) continue
      const eventBytes = byteLength(toUploadItem(event))
      if (batch.length >= MAX_BATCH_EVENTS || (batch.length > 0 && bytes + eventBytes > MAX_BATCH_BYTES)) {
        break
      }
      batch.push(event)
      bytes += eventBytes
    }
    if (batch.length === 0) return

    this.flushing = true
    this.publish()
    const request: DiagnosticEventUploadRequest = {
      deviceId: this.deviceId,
      platform: 'web' satisfies DiagnosticPlatform,
      clientBatchId: newId('web-batch'),
      events: batch.map(toUploadItem),
    }

    try {
      const result = await this.send(request, signal)
      if (generation !== this.generation) return
      this.acceptResult(batch, result)
      this.lastError = null
    } catch (error) {
      if (generation !== this.generation) return
      if (error instanceof DOMException && error.name === 'AbortError') {
        return
      }
      if (error instanceof Error && error.name === 'AbortError') {
        return
      }
      if (error instanceof ApiError && error.status >= 400 && error.status < 500
        && PERMANENT_DROP_STATUSES.has(error.status)) {
        this.dropEvents(batch)
        this.lastError = error.message.slice(0, 300)
      } else {
        const retryAfter = error instanceof ApiError ? error.retryAfterMs : null
        this.backoff(batch, retryAfter)
        this.lastError = error instanceof Error ? error.message.slice(0, 300) : '诊断上报失败'
      }
    } finally {
      if (generation === this.generation) {
        this.flushing = false
        this.persist()
        this.publish()
      }
    }
  }

  private acceptResult(batch: StoredDiagnosticEvent[], result: DiagnosticUploadResult): void {
    const outcomes = new Map((result.items ?? []).map((item) => [item.clientEventId, item]))
    const removeIds = new Set<string>()
    let unresolved = 0
    for (const event of batch) {
      const outcome = outcomes.get(event.clientEventId)
      if (!outcome) {
        unresolved += 1
        continue
      }
      if (outcome.outcome === 'accepted' || outcome.outcome === 'skipped') {
        removeIds.add(event.clientEventId)
        this.queue.sentCount += 1
      } else {
        removeIds.add(event.clientEventId)
        this.queue.droppedCount += 1
      }
    }
    this.queue.events = this.queue.events.filter((event) => !removeIds.has(event.clientEventId))
    if (unresolved > 0) {
      this.backoff(batch.filter((event) => !removeIds.has(event.clientEventId)), null)
    }
  }

  private dropEvents(batch: StoredDiagnosticEvent[]): void {
    const ids = new Set(batch.map((event) => event.clientEventId))
    this.queue.events = this.queue.events.filter((event) => !ids.has(event.clientEventId))
    this.queue.droppedCount += ids.size
  }

  private backoff(batch: StoredDiagnosticEvent[], retryAfterMs: number | null): void {
    const byId = new Map(batch.map((event) => [event.clientEventId, event]))
    for (const event of this.queue.events) {
      const pending = byId.get(event.clientEventId)
      if (!pending) continue
      event.attemptCount += 1
      const delay = retryAfterMs
        ?? Math.min(this.maxBackoffMs, this.baseBackoffMs * 2 ** Math.min(8, event.attemptCount - 1))
      event.nextAttemptAtMs = this.now() + Math.max(0, delay)
    }
  }

  private enforceCaps(): void {
    while (this.queue.events.length > MAX_QUEUE_EVENTS) {
      this.queue.events.shift()
      this.queue.droppedCount += 1
    }
    while (this.queue.events.length > 1 && byteLength(this.queue.events) > MAX_QUEUE_BYTES) {
      this.queue.events.shift()
      this.queue.droppedCount += 1
    }
  }

  private persist(): void {
    if (!this.ownerScope) return
    const key = queueKey(this.ownerScope)
    try {
      this.storage.setItem(key, JSON.stringify(this.queue))
    } catch {
      const before = this.queue.events.length
      const keep = Math.max(1, Math.floor(before / 2))
      this.queue.events = this.queue.events.slice(-keep)
      this.queue.droppedCount += before - this.queue.events.length
      try {
        this.storage.setItem(key, JSON.stringify(this.queue))
      } catch {
        this.queue.droppedCount += this.queue.events.length
        this.queue.events = []
      }
    }
  }

  private scheduleNext(): void {
    if (!this.autoFlush || this.queue.events.length === 0 || typeof window === 'undefined') return
    if (this.timer) clearTimeout(this.timer)
    const due = Math.min(...this.queue.events.map((event) => event.nextAttemptAtMs))
    const delay = Math.max(0, due - this.now())
    this.timer = setTimeout(() => void this.flush(), Math.min(FLUSH_INTERVAL_MS, delay))
  }

  private loadOrCreateDeviceId(ownerScope: string): string {
    const key = deviceKey(ownerScope)
    const existing = this.storage.getItem(key)
    if (existing && isValidDeviceId(existing)) {
      return existing
    }
    const created = newId('web-device')
    try {
      this.storage.setItem(key, created)
    } catch {
      // 存储不可用时仍使用本次会话内的设备标识。
    }
    return created
  }

  private buildSnapshot(): DiagnosticQueueSnapshot {
    return {
      configured: this.ownerScope !== null,
      pendingCount: this.queue.events.length,
      sentCount: this.queue.sentCount,
      droppedCount: this.queue.droppedCount,
      flushing: this.flushing,
      nextAttemptAtMs: this.queue.events.length === 0
        ? null
        : Math.min(...this.queue.events.map((event) => event.nextAttemptAtMs)),
      lastError: this.lastError,
    }
  }

  private publish(): void {
    this.snapshot = this.buildSnapshot()
    for (const listener of this.listeners) listener()
  }
}

export const diagnosticQueue = new DiagnosticQueue()

export function configureDiagnosticQueue(options: Partial<ConfigureDiagnosticQueueOptions> = {}): void {
  diagnosticQueue.configure({
    ...options,
    ownerScope: options.ownerScope ?? `browser|${getApiBase()}|anonymous`,
    appVersion: options.appVersion ?? 'unknown',
  })
}

export function reportWebError(error: unknown, componentStack?: string): StoredDiagnosticEvent | null {
  const message = error instanceof Error ? error.message : String(error)
  const stack = error instanceof Error ? error.stack : undefined
  return diagnosticQueue.enqueue({
    kind: error instanceof Error && error.name === 'ChunkLoadError' ? 'crash' : 'error',
    severity: 'error',
    message,
    stackTrace: [stack, componentStack].filter(Boolean).join('\n'),
    attributes: {
      route: typeof window === 'undefined' ? '' : window.location.pathname,
      source: 'react-error-boundary',
    },
  })
}
