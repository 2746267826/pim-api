import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'

export type ObjectUrlKind = 'thumbnail' | 'player'

interface CacheEntry {
  kind: ObjectUrlKind
  url: string
  lastUsed: number
}

const limits: Record<ObjectUrlKind, number> = {
  thumbnail: 120,
  player: 8,
}

const entries = new Map<string, CacheEntry>()
const listeners = new Set<() => void>()
let epoch = 0

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
    .filter(([, entry]) => entry.kind === kind)
    .sort((left, right) => left[1].lastUsed - right[1].lastUsed)
  const overflow = matching.length - limits[kind]
  if (overflow <= 0) return false
  let evicted = false
  for (const [key, entry] of matching.filter(([key]) => key !== keepKey).slice(0, overflow)) {
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

/** 清理缩略图或播放器对象 URL；返回实际撤销的条目数。重复调用返回 0。 */
export function clearObjectUrlCache(kind?: ObjectUrlKind): number {
  let cleared = 0
  for (const [key, entry] of [...entries.entries()]) {
    if (kind && entry.kind !== kind) continue
    revoke(entry.url)
    entries.delete(key)
    cleared += 1
  }
  if (cleared > 0) notify()
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
    setState({ url: null, error: null })
    void loadRef.current(controller.signal)
      .then((blob) => {
        if (!active) return
        const url = putCachedObjectUrl(kind, key, blob)
        setState({ url, error: null })
      })
      .catch((error: unknown) => {
        if (!active) return
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
