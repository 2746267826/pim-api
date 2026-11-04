// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { NAV_GROUPS } from '@/app/nav'
import { NativeNotesPage } from './native-notes-page'
import type { NoteMaterialVersion, NativeNotePage, NativeNoteSummary } from './api'

const auth = vi.hoisted(() => ({ user: { id: 'owner-a', role: 'user' } }))
const notes = vi.hoisted(() => ({ list: vi.fn(), current: vi.fn() }))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => auth }))
vi.mock('./api', () => ({ nativeNotesApi: notes }))

const noteA = '11111111-1111-4111-8111-111111111111'
const versionA = '22222222-2222-4222-8222-222222222222'
const maliciousText = '<img src=x onerror="alert(1)"> 原稿正文'

function summary(noteId: string, title: string, currentVersionId = versionA): NativeNoteSummary {
  return { noteId, title, currentVersionId, hasConflict: false }
}
function page(items: NativeNoteSummary[], total = items.length, offset = 0): NativeNotePage {
  return { items, total, offset }
}
function material(title = '安全原稿', body = '来源正文'): NoteMaterialVersion {
  const noteId = noteA
  const versionId = versionA
  return {
    inputJson: JSON.stringify({
      schemaVersion: 1,
      id: versionId,
      note: { schemaVersion: 1, id: noteId, title, revision: 1, blocks: [{ id: 'block-1', kind: 'markdown', text: body }] },
    }),
    manifestJson: JSON.stringify({ schemaVersion: 1, versionId, noteId, files: [{ path: 'note.json' }] }),
    upload: {
      versionId, noteId, baseVersionId: null, currentVersionId: versionId, phase: 'Uploaded',
      createdAt: '2026-11-01T12:00:00Z', expiresAt: '2026-12-01T12:00:00Z', isCurrent: true, hasConflict: false,
      limits: {
        partSizeBytes: 1_048_576, maxMetadataCharacters: 1000, maxVersionBytes: 10_000_000,
        maxUserReservedBytes: 20_000_000, maxFiles: 20, maxPendingVersions: 3, maxVersionsPerUser: 100, uploadExpiryHours: 24,
      },
      files: [{ index: 0, path: 'note.json', kind: 'Document', sha256: 'a'.repeat(64), sizeBytes: 1024, partCount: 1, uploadedParts: [0] }],
    },
  }
}

let client: QueryClient
function app(path = '/native-notes/library') {
  return <QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/native-notes/library" element={<NativeNotesPage />} />
        <Route path="/native-notes/library/:noteId" element={<NativeNotesPage />} />
      </Routes>
    </MemoryRouter>
  </QueryClientProvider>
}
function renderPage(path?: string) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(app(path))
}

beforeEach(() => {
  vi.resetAllMocks()
  auth.user.id = 'owner-a'
  notes.list.mockResolvedValue(page([summary(noteA, '安全原稿')]))
  notes.current.mockResolvedValue(material())
})
afterEach(() => { cleanup(); client.clear() })

it('adds a distinct, navigable original-material entry while keeping derived processing', async () => {
  const collection = NAV_GROUPS.find((group) => group.label === '收集')
  expect(collection?.items).toContainEqual(expect.objectContaining({ to: '/native-notes/library', label: '笔记原稿' }))
  expect(collection?.items).toContainEqual(expect.objectContaining({ to: '/native-notes', label: '笔记处理' }))

  renderPage()
  const link = await screen.findByRole('link', { name: /安全原稿/ })
  await userEvent.click(link)
  expect(await screen.findByRole('heading', { name: '安全原稿' })).toBeTruthy()
  expect(screen.getByText('来源正文')).toBeTruthy()
  expect(screen.getByText('note.json')).toBeTruthy()
  const sourceDetails = screen.getByText('查看材料来源信息').closest('details')
  expect(sourceDetails?.open).toBe(false)
  expect(screen.getByText(/已同步当前原稿，页面只读/)).toBeTruthy()
  await userEvent.click(screen.getByText('查看材料来源信息'))
  expect(sourceDetails?.open).toBe(true)
  expect(screen.getByText('笔记 ID')).toBeTruthy()
  expect(screen.getByText(noteA)).toBeTruthy()
  expect(screen.getByText('当前版本 ID')).toBeTruthy()
  expect(screen.getByText(versionA)).toBeTruthy()
})

it('renders source HTML as text rather than creating active elements', async () => {
  notes.current.mockResolvedValue(material('只读测试', maliciousText))
  const view = renderPage(`/native-notes/library/${noteA}`)
  expect(await screen.findByText(maliciousText)).toBeTruthy()
  expect(view.container.querySelector('img')).toBeNull()
  expect(screen.getByText(/查看原始输入 JSON/)).toBeTruthy()
})

it('paginates through the server result and disables navigation at both ends', async () => {
  const nextNote = '33333333-3333-4333-8333-333333333333'
  notes.list.mockImplementation((offset: number) => Promise.resolve(offset === 0
    ? page([summary(noteA, '第一页笔记')], 21, 0)
    : page([summary(nextNote, '第二页笔记', '44444444-4444-4444-8444-444444444444')], 21, 20)))
  renderPage()

  expect(await screen.findByText('第一页笔记')).toBeTruthy()
  expect(screen.getByText('显示 1–1 篇，共 21 篇')).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: /下一页/ }))
  expect(await screen.findByText('第二页笔记')).toBeTruthy()
  expect(notes.list).toHaveBeenCalledWith(20, expect.any(AbortSignal))
  expect(screen.getByRole('button', { name: /上一页/ }).hasAttribute('disabled')).toBe(false)
  expect(screen.getByRole('button', { name: /下一页/ }).hasAttribute('disabled')).toBe(true)
})

it('keeps a way back when a later page becomes empty after the list changes', async () => {
  notes.list.mockImplementation((offset: number) => Promise.resolve(offset === 0
    ? page([summary(noteA, '仍然存在的笔记')], 21, 0)
    : page([], 1, 20)))
  renderPage()
  expect(await screen.findByText('仍然存在的笔记')).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: /下一页/ }))
  expect(await screen.findByText('这一页没有笔记')).toBeTruthy()
  expect(screen.getByText('列表可能已更新，请返回上一页继续查看。')).toBeTruthy()
  const previous = screen.getByRole('button', { name: /上一页/ })
  expect(previous.hasAttribute('disabled')).toBe(false)
  await userEvent.click(previous)
  expect(await screen.findByText('仍然存在的笔记')).toBeTruthy()
})


it('shows and retries a list-load error', async () => {
  notes.list.mockRejectedValueOnce(new Error('列表暂时不可用')).mockResolvedValueOnce(page([summary(noteA, '恢复后的笔记')]))
  renderPage()

  expect(await screen.findByText('笔记列表加载失败')).toBeTruthy()
  expect(screen.getByText('列表暂时不可用')).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: /重试加载/ }))
  expect(await screen.findByText('恢复后的笔记')).toBeTruthy()
  expect(notes.list).toHaveBeenCalledTimes(2)
})

it('shows and retries a current-material detail error', async () => {
  notes.current.mockRejectedValueOnce(new Error('当前版本不存在')).mockResolvedValueOnce(material('恢复读取'))
  renderPage(`/native-notes/library/${noteA}`)

  expect(await screen.findByText('当前原稿读取失败')).toBeTruthy()
  expect(screen.getByText('当前版本不存在')).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: /重试读取/ }))
  expect(await screen.findByRole('heading', { name: '恢复读取' })).toBeTruthy()
  expect(notes.current).toHaveBeenCalledTimes(2)
})

it('isolates outstanding list requests when the authenticated account changes', async () => {
  let resolveOld: ((result: NativeNotePage) => void) | undefined
  let oldSignal: AbortSignal | undefined
  notes.list.mockImplementationOnce((_offset: number, signal: AbortSignal) => {
    oldSignal = signal
    return new Promise<NativeNotePage>((resolve) => { resolveOld = resolve })
  }).mockResolvedValueOnce(page([summary('55555555-5555-4555-8555-555555555555', '新账号笔记')]))

  const view = renderPage()
  await waitFor(() => expect(notes.list).toHaveBeenCalledTimes(1))
  auth.user.id = 'owner-b'
  view.rerender(app())

  expect(await screen.findByText('新账号笔记')).toBeTruthy()
  await waitFor(() => expect(oldSignal?.aborted).toBe(true))
  resolveOld?.(page([summary(noteA, '旧账号不应显示')]))
  await waitFor(() => expect(screen.queryByText('旧账号不应显示')).toBeNull())
  expect(screen.getByText('新账号笔记')).toBeTruthy()
})
