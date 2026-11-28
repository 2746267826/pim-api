import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'

export type ObjectUrlKind = 'thumbnail' | 'player'

interface CacheEntry {
  kind: ObjectUrlKind
  url: string
  lastUsed: number
  /** clear 时仍有挂载消费者，标记为待清理；最后一个消费者释放后再撤销。 */
  pendingClear?: boolean
}

const limits: Record<ObjectUrlKind, number> = {
  thumbnail: 120,
  player: 8,
}

const entries = new Map<string, CacheEntry>()
const activeConsumers = new Map<string, number>()
const listeners = new Set<() => void>()
let epoch = 0
let clearRevision = 0

function consumerKey(kind: ObjectUrlKind, key: string): string {
  return `${kind}\u0000${key}`
}

function isInUse(kind: ObjectUrlKind, key: string): boolean {
  return (activeConsumers.get(consumerKey(kind, key)) ?? 0) > 0
}

function release(kind: ObjectUrlKind, key: string) {
  const id = consumerKey(kind, key)
  const count = activeConsumers.get(id) ?? 0
  if (count > 1) {
    activeConsumers.set(id, count - 1)
    return
  }
  activeConsumers.delete(id)
  const entry = entries.get(key)
  if (!entry || entry.kind !== kind) return
  if (entry.pendingClear) {
    revoke(entry.url)
    entries.delete(key)
    notify()
    return
  }
  if (trim(kind, '')) notify()
}

function notify() {
  epoch += 1
  for (const listener of listeners) listener()
}

function revoke(url: string) {
  try {
    URL.revokeObjectURL(url)
  } catch {
    return
  }
}

function trim(kind: ObjectUrlKind, keepKey: string): boolean {
  const matching = [...entries.entries()]
    .filter(([key, entry]) => entry.kind === kind && key !== keepKey && !isInUse(kind, key))
    .sort((left, right) => left[1].lastUsed - right[1].lastUsed)
  const total = [...entries.values()].filter((entry) => entry.kind === kind).length
  const overflow = total - limits[kind]
  if (overflow <= 0) return false
  let evicted = false
  for (const [key, entry] of matching.slice(0, overflow)) {
    revoke(entry.url)
    entries.delete(key)
    evicted = true
  }
  return evicted
}

export function getCachedObjectUrl(kind: ObjectUrlKind, key: string): string | null {
  const entry = entries.get(key)
  if (!entry || entry.kind !== kind) return null
  entry.lastUsed = Date.now()
  return entry.url
}

export function putCachedObjectUrl(kind: ObjectUrlKind, key: string, blob: Blob): string {
  const existing = entries.get(key)
  if (existing && existing.kind === kind) {
    existing.lastUsed = Date.now()
    return existing.url
  }
  if (existing) {
    revoke(existing.url)
    entries.delete(key)
  }
  const url = URL.createObjectURL(blob)
  entries.set(key, { kind, url, lastUsed: Date.now() })
  if (trim(kind, key)) notify()
  return url
}

function putCachedObjectUrlAtRevision(kind: ObjectUrlKind, key: string, blob: Blob, revision: number): string | null {
  if (revision !== clearRevision) return null
  return putCachedObjectUrl(kind, key, blob)
}

export function objectUrlCacheRevision(): number {
  return clearRevision
}

/**
 * 清理缩略图或播放器对象 URL；返回实际撤销的条目数。重复调用返回 0。
 * 仍被挂载组件/播放器使用的条目会保留并标记为待清理，等最后一个消费者释放后
 * 再由 release() 撤销，避免正在显示的内容变成永久空白。
 * 即使当前没有可立即撤销的条目，也要推进清理代次并通知订阅者：
 * 否则清理前已在途的读取会在稍后完成并把对象 URL 重新放回缓存。
 */
export function clearObjectUrlCache(kind?: ObjectUrlKind): number {
  clearRevision += 1
  let cleared = 0
  for (const [key, entry] of [...entries.entries()]) {
    if (kind && entry.kind !== kind) continue
    if (isInUse(entry.kind, key)) {
      entry.pendingClear = true
      continue
    }
    revoke(entry.url)
    entries.delete(key)
    cleared += 1
  }
  notify()
  return cleared
}

export function objectUrlCacheCounts(): Record<ObjectUrlKind, number> {
  const counts: Record<ObjectUrlKind, number> = { thumbnail: 0, player: 0 }
  for (const entry of entries.values()) counts[entry.kind] += 1
  return counts
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

function snapshot(): number {
  return epoch
}

/** 缓存被清理后触发重新渲染，让仍挂载的缩略图/播放器按需重新读取。 */
export function useObjectUrlCacheEpoch(): number {
  return useSyncExternalStore(subscribe, snapshot, snapshot)
}

export interface CachedBlobUrlState {
  url: string | null
  error: unknown | null
  reload: () => void
}

/**
 * 有界对象 URL 缓存。只有明确归入“缩略图/播放器临时缓存”的读取才应使用它；
 * 笔记材料、草稿图片和用户附件必须继续由各自组件持有，不能被缓存清理撤销。
 */
export function useCachedBlobUrl(
  kind: ObjectUrlKind,
  key: string,
  load: (signal: AbortSignal) => Promise<Blob>,
  enabled = true,
): CachedBlobUrlState {
  const cacheEpoch = useObjectUrlCacheEpoch()
  const loadRef = useRef(load)
  useEffect(() => {
    loadRef.current = load
  }, [load])
  const [reloadToken, setReloadToken] = useState(0)
  const [state, setState] = useState<{ url: string | null; error: unknown | null }>(() => ({
    url: enabled ? getCachedObjectUrl(kind, key) : null,
    error: null,
  }))

  useEffect(() => {
    if (!enabled) return
    const id = consumerKey(kind, key)
    activeConsumers.set(id, (activeConsumers.get(id) ?? 0) + 1)
    return () => release(kind, key)
  }, [kind, key, enabled])

  useEffect(() => {
    if (!enabled) {
      setState({ url: null, error: null })
      return
    }
    const cached = getCachedObjectUrl(kind, key)
    if (cached) {
      setState({ url: cached, error: null })
      return
    }
    const controller = new AbortController()
    let active = true
    const revision = objectUrlCacheRevision()
    setState({ url: null, error: null })
    void loadRef.current(controller.signal)
      .then((blob) => {
        if (!active || objectUrlCacheRevision() !== revision) return
        const url = putCachedObjectUrlAtRevision(kind, key, blob, revision)
        if (!url) return
        setState({ url, error: null })
      })
      .catch((error: unknown) => {
        if (!active || objectUrlCacheRevision() !== revision) return
        setState({ url: null, error })
      })
    return () => {
      active = false
      controller.abort()
    }
  }, [kind, key, enabled, cacheEpoch, reloadToken])

  const reload = useCallback(() => setReloadToken((value) => value + 1), [])
  return { ...state, reload }
}
