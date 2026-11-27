// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  createStoredDraft,
  editStoredDraft,
  noteDerivedDraftStorageKey,
  persistStoredDraft,
  type NoteDerivedDraftScope,
} from '@/features/ai/note-derived/draft-storage'
import { clearObjectUrlCache, putCachedObjectUrl } from '@/lib/object-url-cache'
import { registerUnsavedDraft, resetUnsavedDraftsForTests } from '@/lib/unsaved-drafts'
import { StorageCachePage } from './storage-cache-page'

vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))

const authState = vi.hoisted(() => ({
  user: { id: '11111111-1111-4111-8111-111111111111', role: 'admin' } as { id: string; role: 'admin' | 'user' } | null,
}))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => authState }))

const scope: NoteDerivedDraftScope = {
  ownerId: '11111111-1111-4111-8111-111111111111',
  serverBinding: 'https://pim-a.example',
  artifactId: '22222222-2222-4222-8222-222222222222',
  sourceVersionId: '33333333-3333-4333-8333-333333333333',
  kind: 'CompleteRecord',
}

let client: QueryClient
let nextUrl = 0

function seedDirtyAiDraft(draftScope: NoteDerivedDraftScope = scope): string {
  const clean = persistStoredDraft(createStoredDraft(draftScope, '已保存正文', 'head-1'))
  expect(clean.ok).toBe(true)
  if (!clean.ok) throw new Error('failed to seed clean draft')
  const edited = editStoredDraft(clean.value, '尚未保存的人工正文')
  expect(edited.ok).toBe(true)
  if (!edited.ok) throw new Error('failed to edit draft')
  expect(persistStoredDraft(edited.value).ok).toBe(true)
  const key = noteDerivedDraftStorageKey(draftScope)
  if (!key) throw new Error('missing draft key')
  return key
}

function renderPage() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['q1'], { value: 1 })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <StorageCachePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  authState.user = { id: scope.ownerId, role: 'admin' }
  localStorage.clear()
  resetUnsavedDraftsForTests()
  nextUrl = 0
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => `blob:test-${++nextUrl}`) },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
  Object.defineProperties(HTMLElement.prototype, {
    hasPointerCapture: { configurable: true, value: () => false },
    setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined },
    scrollIntoView: { configurable: true, value: () => undefined },
  })
})

afterEach(() => {
  cleanup()
  client?.clear()
  clearObjectUrlCache()
  resetUnsavedDraftsForTests()
  localStorage.clear()
  vi.restoreAllMocks()
})

describe('存储与缓存页面', () => {
  it('清理前列出未保存编辑与 AI 草稿，确认后才清理，清理后回执保留保护项', async () => {
    const user = userEvent.setup()
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    localStorage.setItem('pim.accessToken', 'secret-token')
    localStorage.setItem('pim.transferHistory', JSON.stringify([{ id: 'h1', fileName: 'a.txt', fileSize: 1, targetPath: '/', fullPath: '/a.txt', status: 'done', startedAt: 1, finishedAt: 2 }]))
    const draftKey = seedDirtyAiDraft()
    registerUnsavedDraft({
      id: 'quick-note:draft',
      kind: 'quick-note',
      title: '未保存闪念',
      detail: '正文仍在弹窗中',
      saveHint: '在弹窗底部保存',
    })
    putCachedObjectUrl('thumbnail', 'thumb-1', new Blob(['thumb']))
    putCachedObjectUrl('player', 'player-1', new Blob(['player']))

    renderPage()

    expect(screen.getByText('受保护内容')).toBeTruthy()
    expect(screen.getByText(/当前服务器：/)).toBeTruthy()
    expect(screen.getByText('未保存编辑')).toBeTruthy()
    expect(screen.getByText('AI 草稿')).toBeTruthy()
    expect(screen.getAllByText('去保存').length).toBeGreaterThan(0)

    await user.click(screen.getByRole('button', { name: '清理本机缓存' }))
    const confirm = await screen.findByRole('button', { name: '只清理临时缓存' })
    expect((confirm as HTMLButtonElement).disabled).toBe(true)

    await user.click(screen.getByRole('checkbox', { name: '确认保留未保存编辑与 AI 草稿' }))
    await waitFor(() => {
      expect((screen.getByRole('button', { name: '只清理临时缓存' }) as HTMLButtonElement).disabled).toBe(false)
    })
    await user.click(screen.getByRole('button', { name: '只清理临时缓存' }))

    expect(await screen.findByText('上次清理回执')).toBeTruthy()
    expect(screen.getByText(/清理时保留未保存编辑\/草稿 1 项、AI 草稿 1 份/)).toBeTruthy()
    expect(screen.getByText(/清理时服务器：https:\/\/pim-a\.example/)).toBeTruthy()
    expect(client.getQueryCache().getAll()).toHaveLength(0)
    expect(localStorage.getItem('pim.transferHistory')).toBeNull()
    expect(localStorage.getItem('pim.accessToken')).toBe('secret-token')
    expect(localStorage.getItem('pim.apiBase')).toBe('https://pim-a.example')
    expect(localStorage.getItem(draftKey)).not.toBeNull()
  }, 30_000)

  it('跨服务器 AI 草稿标记为其他服务器且不提供错误的保存入口', () => {
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    seedDirtyAiDraft({ ...scope, serverBinding: 'https://pim-b.example', artifactId: '44444444-4444-4444-8444-444444444444' })

    renderPage()

    expect(screen.getByText('其他服务器')).toBeTruthy()
    expect(screen.getByText(/草稿服务器：https:\/\/pim-b\.example/)).toBeTruthy()
    expect(screen.queryByText('去保存')).toBeNull()
  })

  it('同一服务器切换账号后不显示其他 owner 的 AI 草稿和保存入口', () => {
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    const otherOwnerDraftKey = seedDirtyAiDraft()
    authState.user = { id: '99999999-9999-4999-8999-999999999999', role: 'admin' }

    renderPage()

    expect(screen.queryByText('AI 草稿')).toBeNull()
    expect(screen.queryByText('去保存')).toBeNull()
    expect(screen.getByText('没有检测到页面内未保存编辑或本机 AI 草稿。')).toBeTruthy()
    expect(localStorage.getItem(otherOwnerDraftKey)).not.toBeNull()
  })

  it('普通账号的 AI 草稿只提供处理页入口，不暴露历史版本链接', () => {
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    seedDirtyAiDraft()
    authState.user = { id: scope.ownerId, role: 'user' }

    renderPage()

    expect(screen.getByRole('link', { name: '去处理页核对' }).getAttribute('href')).toBe('/native-notes/processing')
    expect(screen.queryByText('去保存')).toBeNull()
    expect(screen.queryByText(/来源版本/)).toBeNull()
  })
})
