// @vitest-environment jsdom
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  clearObjectUrlCache,
  getCachedObjectUrl,
  objectUrlCacheCounts,
  putCachedObjectUrl,
  useCachedBlobUrl,
  useObjectUrlCacheEpoch,
} from './object-url-cache'

let nextUrl = 0

beforeEach(() => {
  nextUrl = 0
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => `blob:test-${++nextUrl}`) },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
  clearObjectUrlCache()
})

afterEach(() => {
  cleanup()
  clearObjectUrlCache()
  vi.restoreAllMocks()
})

describe('object URL 临时缓存', () => {
  it('按缩略图/播放器分区缓存，清理幂等且不重复撤销', () => {
    const thumbnail = putCachedObjectUrl('thumbnail', 'thumb-1', new Blob(['thumb']))
    const player = putCachedObjectUrl('player', 'player-1', new Blob(['player']))

    expect(getCachedObjectUrl('thumbnail', 'thumb-1')).toBe(thumbnail)
    expect(getCachedObjectUrl('player', 'player-1')).toBe(player)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 1, player: 1 })

    expect(clearObjectUrlCache('thumbnail')).toBe(1)
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(thumbnail)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 1 })

    expect(clearObjectUrlCache('thumbnail')).toBe(0)
    expect(clearObjectUrlCache()).toBe(1)
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
  })

  it('有界淘汰会通知仍挂载的组件重新加载', () => {
    const epoch = renderHook(() => useObjectUrlCacheEpoch())
    const initial = epoch.result.current

    act(() => {
      for (let index = 0; index < 121; index += 1) {
        putCachedObjectUrl('thumbnail', `thumb-${index}`, new Blob([String(index)]))
      }
    })

    expect(objectUrlCacheCounts().thumbnail).toBe(120)
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(1)
    expect(epoch.result.current).toBeGreaterThan(initial)
  })

  it('清理跳过仍挂载的缩略图与播放器消费者，释放归零后才撤销', async () => {
    const thumbnailLoad = vi.fn(async () => new Blob(['thumbnail']))
    const playerLoad = vi.fn(async () => new Blob(['player']))
    const thumbnail = renderHook(() => useCachedBlobUrl('thumbnail', 'active-thumbnail', thumbnailLoad))
    const player = renderHook(() => useCachedBlobUrl('player', 'active-player', playerLoad))

    await waitFor(() => expect(thumbnail.result.current.url).not.toBeNull())
    await waitFor(() => expect(player.result.current.url).not.toBeNull())
    const thumbnailUrl = thumbnail.result.current.url!
    const playerUrl = player.result.current.url!

    act(() => {
      expect(clearObjectUrlCache('thumbnail')).toBe(0)
      expect(clearObjectUrlCache('player')).toBe(0)
    })
    expect(URL.revokeObjectURL).not.toHaveBeenCalled()
    expect(getCachedObjectUrl('thumbnail', 'active-thumbnail')).toBe(thumbnailUrl)
    expect(getCachedObjectUrl('player', 'active-player')).toBe(playerUrl)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 1, player: 1 })
    expect(thumbnailLoad).toHaveBeenCalledTimes(1)
    expect(playerLoad).toHaveBeenCalledTimes(1)

    act(() => thumbnail.unmount())
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(thumbnailUrl)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 1 })

    act(() => player.unmount())
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(playerUrl)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
  })

  it('清理后释放的 URL 不留在缓存，重新挂载会重新读取', async () => {
    const load = vi.fn(async () => new Blob(['reload']))
    const first = renderHook(() => useCachedBlobUrl('thumbnail', 'reload-1', load))

    await waitFor(() => expect(first.result.current.url).not.toBeNull())
    const staleUrl = first.result.current.url!
    expect(load).toHaveBeenCalledTimes(1)

    act(() => {
      expect(clearObjectUrlCache()).toBe(0)
    })
    act(() => first.unmount())
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(staleUrl)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })

    const second = renderHook(() => useCachedBlobUrl('thumbnail', 'reload-1', load))
    await waitFor(() => expect(second.result.current.url).not.toBeNull())
    expect(second.result.current.url).not.toBe(staleUrl)
    expect(load).toHaveBeenCalledTimes(2)
  })

  it('同一 URL 有多个消费者时，最后一个释放后才撤销', async () => {
    const load = vi.fn(async () => new Blob(['shared']))
    const first = renderHook(() => useCachedBlobUrl('thumbnail', 'shared-thumb', load))

    await waitFor(() => expect(first.result.current.url).not.toBeNull())
    const url = first.result.current.url!
    expect(load).toHaveBeenCalledTimes(1)

    const second = renderHook(() => useCachedBlobUrl('thumbnail', 'shared-thumb', load))
    await waitFor(() => expect(second.result.current.url).toBe(url))
    expect(load).toHaveBeenCalledTimes(1)

    act(() => {
      expect(clearObjectUrlCache('thumbnail')).toBe(0)
    })
    expect(URL.revokeObjectURL).not.toHaveBeenCalled()

    act(() => first.unmount())
    expect(URL.revokeObjectURL).not.toHaveBeenCalled()
    expect(getCachedObjectUrl('thumbnail', 'shared-thumb')).toBe(url)

    act(() => second.unmount())
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(url)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
  })

  it('清理会失效清理前启动的读取，晚到结果不会重新放回缓存', async () => {
    const pending: Array<(blob: Blob) => void> = []
    const load = vi.fn(() => new Promise<Blob>((resolve) => pending.push(resolve)))
    const hook = renderHook(() => useCachedBlobUrl('thumbnail', 'late-load', load))

    await waitFor(() => expect(load).toHaveBeenCalledTimes(1))
    act(() => {
      expect(clearObjectUrlCache()).toBe(0)
    })
    await waitFor(() => expect(load).toHaveBeenCalledTimes(2))

    await act(async () => {
      pending[0]?.(new Blob(['stale']))
      await Promise.resolve()
    })
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
    expect(hook.result.current.url).toBeNull()

    await act(async () => {
      pending[1]?.(new Blob(['fresh']))
      await Promise.resolve()
    })
    await waitFor(() => expect(hook.result.current.url).toBe('blob:test-1'))
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 1, player: 0 })
  })

  it('淘汰不会撤销仍挂载消费者正在使用的 URL', async () => {
    const load = vi.fn(async () => new Blob(['keep']))
    const hook = renderHook(() => useCachedBlobUrl('thumbnail', 'active-keep', load))

    await waitFor(() => expect(hook.result.current.url).toBe('blob:test-1'))
    act(() => {
      for (let index = 0; index < 120; index += 1) {
        putCachedObjectUrl('thumbnail', `other-${index}`, new Blob([String(index)]))
      }
    })

    expect(getCachedObjectUrl('thumbnail', 'active-keep')).toBe('blob:test-1')
    expect(URL.revokeObjectURL).not.toHaveBeenCalledWith('blob:test-1')
    expect(objectUrlCacheCounts().thumbnail).toBe(120)
  })
})
