// @vitest-environment jsdom
import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { NoteDerivedDraftScope } from '@/features/ai/note-derived/draft-storage'

const scope: NoteDerivedDraftScope = {
  ownerId: '11111111-1111-4111-8111-111111111111',
  serverBinding: 'https://pim-a.example',
  artifactId: '22222222-2222-4222-8222-222222222222',
  sourceVersionId: '33333333-3333-4333-8333-333333333333',
  kind: 'CompleteRecord',
}

const transferHistory = [{
  id: 'history-1',
  fileName: 'a.txt',
  fileSize: 1,
  targetPath: '/',
  fullPath: '/a.txt',
  status: 'done' as const,
  startedAt: 1,
  finishedAt: 2,
}]

let nextUrl = 0
let queryClient: QueryClient

beforeEach(() => {
  vi.resetModules()
  localStorage.clear()
  nextUrl = 0
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => `blob:test-${++nextUrl}`) },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
})

afterEach(() => {
  queryClient?.clear()
  vi.restoreAllMocks()
})

async function loadModules() {
  const [cleanup, objectUrls, drafts, unsaved, upload] = await Promise.all([
    import('./cache-cleanup'),
    import('@/lib/object-url-cache'),
    import('@/features/ai/note-derived/draft-storage'),
    import('@/lib/unsaved-drafts'),
    import('@/features/files/upload-engine'),
  ])
  return { ...cleanup, objectUrls, drafts, unsaved, uploadEngine: upload.uploadEngine }
}

describe('Web 临时缓存清理', () => {
  it('只清理查询、缩略图、播放器和传输历史，保留草稿、令牌与服务器配置', async () => {
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    localStorage.setItem('pim.accessToken', 'secret-token')
    localStorage.setItem('pim.refreshToken', 'secret-refresh')
    localStorage.setItem('pim.transferHistory', JSON.stringify(transferHistory))

    const modules = await loadModules()
    const clean = modules.drafts.persistStoredDraft(modules.drafts.createStoredDraft(scope, '已保存正文', 'head-1'))
    expect(clean.ok).toBe(true)
    if (!clean.ok) return
    const edited = modules.drafts.editStoredDraft(clean.value, '尚未保存的人工正文')
    expect(edited.ok).toBe(true)
    if (!edited.ok) return
    expect(modules.drafts.persistStoredDraft(edited.value).ok).toBe(true)
    const draftKey = modules.drafts.noteDerivedDraftStorageKey(scope)
    expect(draftKey).not.toBeNull()

    modules.unsaved.registerUnsavedDraft({
      id: 'quick-note:draft',
      kind: 'quick-note',
      title: '未保存闪念',
      detail: '正文仍在弹窗中',
      saveHint: '在弹窗底部保存',
    })
    modules.objectUrls.putCachedObjectUrl('thumbnail', 'thumb-1', new Blob(['thumb']))
    modules.objectUrls.putCachedObjectUrl('player', 'player-1', new Blob(['player']))

    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['q1'], { value: 1 })
    queryClient.setQueryData(['q2'], { value: 2 })
    queryClient.getMutationCache().build(queryClient, { mutationKey: ['save'], mutationFn: async () => 'ok' })

    const before = modules.snapshotTemporaryCache(queryClient)
    expect(before).toMatchObject({
      serverBinding: 'https://pim-a.example',
      queryEntries: 2,
      thumbnailEntries: 1,
      playerEntries: 1,
      transferHistoryEntries: 1,
    })
    expect(before.unsavedDrafts).toHaveLength(1)
    expect(before.aiDrafts).toHaveLength(1)

    const result = await modules.clearTemporaryWebCache(queryClient)
    expect(result).toMatchObject({
      serverBinding: 'https://pim-a.example',
      queryEntries: 2,
      thumbnailEntries: 1,
      playerEntries: 1,
      transferHistoryEntries: 1,
      preservedUnsavedDrafts: 1,
      preservedAiDrafts: 1,
    })

    expect(queryClient.getQueryCache().getAll()).toHaveLength(0)
    expect(queryClient.getMutationCache().getAll()).toHaveLength(1)
    expect(modules.objectUrls.objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
    expect(localStorage.getItem('pim.transferHistory')).toBeNull()
    expect(localStorage.getItem('pim.accessToken')).toBe('secret-token')
    expect(localStorage.getItem('pim.refreshToken')).toBe('secret-refresh')
    expect(localStorage.getItem('pim.apiBase')).toBe('https://pim-a.example')
    expect(draftKey ? localStorage.getItem(draftKey) : null).not.toBeNull()

    const after = modules.snapshotTemporaryCache(queryClient)
    expect(after.unsavedDrafts).toHaveLength(1)
    expect(after.aiDrafts).toHaveLength(1)
    expect(after.aiDrafts[0]?.binding.sourceVersionId).toBe(scope.sourceVersionId)

    const second = await modules.clearTemporaryWebCache(queryClient)
    expect(second).toMatchObject({
      queryEntries: 0,
      thumbnailEntries: 0,
      playerEntries: 0,
      transferHistoryEntries: 0,
      preservedUnsavedDrafts: 1,
      preservedAiDrafts: 1,
    })
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2)

    modules.unsaved.resetUnsavedDraftsForTests()
  }, 30_000)
})
