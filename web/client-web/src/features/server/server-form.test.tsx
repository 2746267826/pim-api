// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  createStoredDraft,
  editStoredDraft,
  noteDerivedDraftStorageKey,
  persistStoredDraft,
  type NoteDerivedDraftScope,
} from '@/features/ai/note-derived/draft-storage'
import { clearObjectUrlCache, objectUrlCacheCounts, putCachedObjectUrl } from '@/lib/object-url-cache'
import { listUnsavedDrafts, registerUnsavedDraft, resetUnsavedDraftsForTests } from '@/lib/unsaved-drafts'
import { STORAGE_KEYS, remove } from '@/lib/storage'
import { ServerForm } from './server-form'

vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn() }))

const scope: NoteDerivedDraftScope = {
  ownerId: '11111111-1111-4111-8111-111111111111',
  serverBinding: 'https://pim-a.example',
  artifactId: '22222222-2222-4222-8222-222222222222',
  sourceVersionId: '33333333-3333-4333-8333-333333333333',
  kind: 'CompleteRecord',
}

let client: QueryClient

function seedDirtyAiDraft(): string {
  const clean = persistStoredDraft(createStoredDraft(scope, '已保存正文', 'head-1'))
  expect(clean.ok).toBe(true)
  if (!clean.ok) throw new Error('failed to seed clean draft')
  const edited = editStoredDraft(clean.value, '尚未保存的人工正文')
  expect(edited.ok).toBe(true)
  if (!edited.ok) throw new Error('failed to edit draft')
  expect(persistStoredDraft(edited.value).ok).toBe(true)
  const key = noteDerivedDraftStorageKey(scope)
  if (!key) throw new Error('missing draft key')
  return key
}

function renderForm() {
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <ServerForm mode="settings" />
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  localStorage.clear()
  resetUnsavedDraftsForTests()
  clearObjectUrlCache()
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => `blob:server-switch-${Math.random()}`) },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
})

afterEach(() => {
  cleanup()
  client?.clear()
  resetUnsavedDraftsForTests()
  clearObjectUrlCache()
  localStorage.clear()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('服务器切换', () => {
  it('清理上一服务器的临时缓存并要求重新登录，同时保留 AI 草稿和未保存编辑', async () => {
    const user = userEvent.setup()
    localStorage.setItem(STORAGE_KEYS.apiBase, 'https://pim-a.example')
    localStorage.setItem(STORAGE_KEYS.accessToken, 'old-access')
    localStorage.setItem(STORAGE_KEYS.refreshToken, 'old-refresh')
    localStorage.setItem(STORAGE_KEYS.transferHistory, JSON.stringify([{ id: 'h1', fileName: 'a.txt', fileSize: 1, targetPath: '/', fullPath: '/a.txt', status: 'done', startedAt: 1, finishedAt: 2 }]))
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

    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(['files'], [{ id: 'f1' }])

    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/settings/server']}>
          <Routes>
            <Route path="/settings/server" element={<ServerForm mode="settings" />} />
            <Route path="/settings" element={<p>settings-arrived</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    const input = screen.getByLabelText('API 地址')
    await user.clear(input)
    await user.type(input, 'https://pim-b.example')
    await user.click(screen.getByRole('button', { name: '保存并重新登录' }))

    expect(await screen.findByText('settings-arrived')).toBeTruthy()
    expect(localStorage.getItem(STORAGE_KEYS.apiBase)).toBe('https://pim-b.example')
    expect(localStorage.getItem(STORAGE_KEYS.accessToken)).toBeNull()
    expect(localStorage.getItem(STORAGE_KEYS.refreshToken)).toBeNull()
    expect(localStorage.getItem(STORAGE_KEYS.transferHistory)).toBeNull()
    expect(client.getQueryCache().getAll()).toHaveLength(0)
    expect(objectUrlCacheCounts()).toEqual({ thumbnail: 0, player: 0 })
    expect(localStorage.getItem(draftKey)).not.toBeNull()
    expect(listUnsavedDrafts()).toHaveLength(1)
  }, 30_000)
})

describe('服务器连接表单传输门禁（Q04）', () => {
  beforeEach(() => {
    remove(STORAGE_KEYS.apiBase)
    remove(STORAGE_KEYS.apiBaseHistory)
  })

  it('远程 HTTP 在测试连接与保存前即被禁用并显示原因', async () => {
    renderForm()

    const input = screen.getByLabelText('API 地址')
    fireEvent.change(input, { target: { value: 'http://notes.example.com:5858' } })

    expect(screen.getByRole('alert').textContent).toContain('远程服务器必须使用 HTTPS')
    expect((screen.getByRole('button', { name: '测试连接' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '保存并重新登录' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('同源默认与 HTTPS 地址仍可测试和保存', async () => {
    renderForm()

    expect((screen.getByRole('button', { name: '测试连接' }) as HTMLButtonElement).disabled).toBe(false)

    const input = screen.getByLabelText('API 地址')
    fireEvent.change(input, { target: { value: 'https://notes.example.com' } })

    expect((screen.getByRole('button', { name: '测试连接' }) as HTMLButtonElement).disabled).toBe(false)
    expect((screen.getByRole('button', { name: '保存并重新登录' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('HTTPS 连接失败时提示核对证书链、域名与有效期', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    renderForm()

    fireEvent.change(screen.getByLabelText('API 地址'), { target: { value: 'https://notes.example.com' } })
    fireEvent.click(screen.getByRole('button', { name: '测试连接' }))

    const alert = await screen.findByText(/HTTPS 连接被浏览器阻止/)
    expect(alert.textContent).toContain('证书链')
    expect(alert.textContent).toContain('域名')
    expect(alert.textContent).toContain('有效期')
  })
})
