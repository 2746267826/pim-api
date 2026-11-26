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
import { resetUnsavedDraftsForTests, useUnsavedDraft } from '@/lib/unsaved-drafts'
import { StorageCacheLauncher } from './storage-cache-dialog'

vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))

const authState = vi.hoisted(() => ({
  user: { id: '11111111-1111-4111-8111-111111111111' } as { id: string } | null,
}))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => authState }))

let client: QueryClient

const ownerADraftScope: NoteDerivedDraftScope = {
  ownerId: '11111111-1111-4111-8111-111111111111',
  serverBinding: 'https://pim-a.example',
  artifactId: '22222222-2222-4222-8222-222222222222',
  sourceVersionId: '33333333-3333-4333-8333-333333333333',
  kind: 'CompleteRecord',
}

function DirtyEditor() {
  useUnsavedDraft(true, {
    id: 'note-text:note-1',
    kind: 'note',
    title: '笔记「课堂笔记」',
    detail: '标题或正文修改仍在当前页面内存中，尚未保存到服务器。',
    saveHint: '返回笔记页后点击“保存笔记”。',
    saveHref: '/native-notes?note=note-1',
  })
  return <div>editor-mounted</div>
}

function renderLauncher() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['q1'], { value: 1 })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <DirtyEditor />
        <StorageCacheLauncher />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  authState.user = { id: ownerADraftScope.ownerId }
  localStorage.clear()
  resetUnsavedDraftsForTests()
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
  resetUnsavedDraftsForTests()
  localStorage.clear()
  vi.restoreAllMocks()
})

describe('常驻存储与缓存入口', () => {
  it('编辑器保持挂载时仍能列出未保存草稿，并要求确认后才清理', async () => {
    const user = userEvent.setup()
    localStorage.setItem('pim.accessToken', 'secret-token')
    renderLauncher()

    await user.click(screen.getByRole('button', { name: '存储与缓存' }))
    expect(await screen.findByText('笔记「课堂笔记」')).toBeTruthy()
    const confirm = screen.getByRole('button', { name: '只清理临时缓存' }) as HTMLButtonElement
    expect(confirm.disabled).toBe(true)

    await user.click(screen.getByRole('checkbox', { name: '确认保留未保存编辑与 AI 草稿' }))
    await waitFor(() => expect(confirm.disabled).toBe(false))
    await user.click(confirm)

    expect(await screen.findByText('上次清理回执')).toBeTruthy()
    expect(screen.getByText('editor-mounted')).toBeTruthy()
    expect(client.getQueryCache().getAll()).toHaveLength(0)
    expect(localStorage.getItem('pim.accessToken')).toBe('secret-token')
  }, 30_000)

  it('同一服务器切换到其他账号后，弹窗不显示前一个账号的草稿或保存入口', async () => {
    const user = userEvent.setup()
    localStorage.setItem('pim.apiBase', 'https://pim-a.example')
    const clean = persistStoredDraft(createStoredDraft(ownerADraftScope, 'A 的已保存正文', 'head-a'))
    expect(clean.ok).toBe(true)
    if (!clean.ok) throw new Error('failed to seed draft')
    const edited = editStoredDraft(clean.value, 'A 的未保存正文')
    expect(edited.ok).toBe(true)
    if (!edited.ok) throw new Error('failed to edit draft')
    expect(persistStoredDraft(edited.value).ok).toBe(true)
    const draftKey = noteDerivedDraftStorageKey(ownerADraftScope)
    if (!draftKey) throw new Error('missing draft key')

    authState.user = { id: '99999999-9999-4999-8999-999999999999' }
    client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <StorageCacheLauncher />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    await user.click(screen.getByRole('button', { name: '存储与缓存' }))
    expect(await screen.findByText('没有检测到当前账号的未保存编辑或本机 AI 草稿。')).toBeTruthy()
    expect(screen.queryByText('AI 草稿')).toBeNull()
    expect(screen.queryByText('去保存')).toBeNull()
    expect(localStorage.getItem(draftKey)).not.toBeNull()
  })
})
