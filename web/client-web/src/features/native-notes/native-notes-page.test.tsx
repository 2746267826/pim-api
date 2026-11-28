// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider, useNavigate } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { navGroupsForRole } from '@/app/nav'
import { ApiError } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { NativeNotesPage } from './native-notes-page'
import { buildMaterialBundle, type NoteMaterialUpload, type NoteMaterialVersion } from './material-model'
import { exportAndroidNotePackage } from './material-package'

const nativeApi = vi.hoisted(() => ({
  limits: vi.fn(),
  notes: vi.fn(),
  current: vi.fn(),
  versions: vi.fn(),
  version: vi.fn(),
  begin: vi.fn(),
  clone: vi.fn(),
  uploadPart: vi.fn(),
  complete: vi.fn(),
  promote: vi.fn(),
  fileBytes: vi.fn(),
  downloadBundle: vi.fn(),
}))
const quickApi = vi.hoisted(() => ({
  list: vi.fn(),
  get: vi.fn(),
}))
const authState = vi.hoisted(() => ({
  userId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  role: 'admin' as 'admin' | 'user',
}))
vi.mock('./api', () => ({
  bindNativeNotesApi: () => nativeApi,
}))
vi.mock('@/features/quick-notes/api', () => ({ quickNotesApi: quickApi }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({
    user: {
      id: authState.userId,
      username: 'synthetic',
      displayName: '合成用户',
      role: authState.role,
    },
  }),
}))

const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'
const conflictVersionId = '99999999-9999-4999-8999-999999999999'
const historyVersionId = '77777777-7777-4777-8777-777777777777'
const blockId = '33333333-3333-4333-8333-333333333333'
const source = {
  noteId,
  title: '课堂笔记',
  currentVersionId: versionId,
  hasConflict: false,
}
const inputJson = JSON.stringify({
  schemaVersion: 1,
  createdAtMillis: 1,
  id: versionId,
  note: {
    schemaVersion: 1,
    id: noteId,
    revision: 0,
    title: '课堂笔记',
    createdAtMillis: 1,
    blocks: [{ id: blockId, kind: 'markdown', text: '# 第一课\n\n保留原文。' }],
  },
  recordings: [],
})
const material: NoteMaterialVersion = {
  inputJson,
  manifestJson: JSON.stringify({
    schemaVersion: 1,
    versionId,
    noteId,
    inputSha256: 'a'.repeat(64),
    revision: 0,
    files: [],
  }),
  upload: {
    versionId,
    noteId,
    baseVersionId: null,
    currentVersionId: versionId,
    phase: 'Uploaded',
    createdAt: '2026-11-01T10:00:00Z',
    expiresAt: '2026-11-01T11:00:00Z',
    isCurrent: true,
    hasConflict: false,
    limits: {
      partSizeBytes: 1024 * 1024,
      maxMetadataCharacters: 8_388_608,
      maxVersionBytes: 64 * 1024 * 1024 * 1024,
      maxUserReservedBytes: 64 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      maxPendingVersions: 10,
      maxVersionsPerUser: 1000,
      uploadExpiryHours: 1,
    },
    files: [],
  },
}
const conflictInput = JSON.parse(inputJson) as {
  id: string
  note: { title: string }
}
conflictInput.id = conflictVersionId
conflictInput.note.title = '冲突候选笔记'
const conflictManifest = JSON.parse(material.manifestJson) as { versionId: string }
conflictManifest.versionId = conflictVersionId
const conflictMaterial: NoteMaterialVersion = {
  ...material,
  inputJson: JSON.stringify(conflictInput),
  manifestJson: JSON.stringify(conflictManifest),
  upload: {
    ...material.upload,
    versionId: conflictVersionId,
    currentVersionId: versionId,
    isCurrent: false,
    hasConflict: true,
  },
}
const historyInput = JSON.parse(inputJson) as {
  id: string
  note: { title: string }
}
historyInput.id = historyVersionId
historyInput.note.title = '历史版本笔记'
const historyManifest = JSON.parse(material.manifestJson) as { versionId: string }
historyManifest.versionId = historyVersionId
const historyMaterial: NoteMaterialVersion = {
  ...material,
  inputJson: JSON.stringify(historyInput),
  manifestJson: JSON.stringify(historyManifest),
  upload: {
    ...material.upload,
    versionId: historyVersionId,
    currentVersionId: versionId,
    isCurrent: false,
    hasConflict: false,
  },
}

function duplicateBlockMaterial(): NoteMaterialVersion {
  const input = JSON.parse(inputJson) as {
    note: {
      blocks: Array<{ id: string; kind: string; text: string }>
    }
  }
  const first = input.note.blocks[0]!
  input.note.blocks = [first, { ...first }]
  return { ...material, inputJson: JSON.stringify(input) }
}

let client: QueryClient
let testRouter: ReturnType<typeof createMemoryRouter>

function ProcessingPage() {
  const navigate = useNavigate()
  const query = new URLSearchParams(testRouter.state.location.search)
  return <div>
    <p>处理页</p>
    <p>{query.get('noteId')}</p>
    <p>{query.get('versionId')}</p>
    <p>{query.get('action')}</p>
    <button type="button" onClick={() => navigate('/native-notes')}>返回笔记库</button>
  </div>
}

function page(initialEntry = '/native-notes') {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  testRouter = createMemoryRouter([
    {
      path: '/native-notes',
      element:
      <QueryClientProvider client={client}>
        <NativeNotesPage />
      </QueryClientProvider>,
    },
    { path: '/native-notes/processing', element: <ProcessingPage /> },
    { path: '/quick-notes', element: <p>旧记录页</p> },
  ], { initialEntries: [initialEntry] })
  return render(<RouterProvider router={testRouter} />)
}

beforeEach(() => {
  vi.resetAllMocks()
  authState.userId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  authState.role = 'admin'
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => 'blob:synthetic-note-image') },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
  nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [source] })
  nativeApi.current.mockResolvedValue({ ...material, upload: material.upload })
  nativeApi.versions.mockResolvedValue([material.upload])
  nativeApi.version.mockImplementation(async (id: string) =>
    id === conflictVersionId ? conflictMaterial : id === historyVersionId ? historyMaterial : material)
  nativeApi.begin.mockImplementation(async (body: {
    versionId: string
    noteId: string
    baseVersionId: string | null
    manifestJson: string
  }) => {
    const manifest = JSON.parse(body.manifestJson) as {
      files: Array<{ path: string; kind: string; sha256: string; sizeBytes: number }>
    }
    return {
      ...material.upload,
      versionId: body.versionId,
      noteId: body.noteId,
      baseVersionId: body.baseVersionId,
      currentVersionId: null,
      phase: 'Uploading',
      isCurrent: false,
      files: manifest.files.map((file, index) => ({
        ...file,
        index,
        partCount: Math.ceil(file.sizeBytes / (1024 * 1024)),
        uploadedParts: [],
      })),
    } satisfies NoteMaterialUpload
  })
  nativeApi.clone.mockImplementation(async (_sourceVersionId: string, body: {
    material: { versionId: string; noteId: string; baseVersionId: string | null; manifestJson: string }
  }) => nativeApi.begin(body.material))
  nativeApi.uploadPart.mockResolvedValue('ok')
  nativeApi.complete.mockImplementation(async (id: string) => ({
    ...material.upload,
    versionId: id,
    phase: 'Uploaded',
    isCurrent: true,
  }))
  nativeApi.promote.mockImplementation(async (id: string) => ({
    ...material.upload,
    versionId: id,
    currentVersionId: id,
    isCurrent: true,
    hasConflict: false,
  }))
  quickApi.list.mockResolvedValue({
    items: [{
      id: '44444444-4444-4444-8444-444444444444',
      contentPreview: '旧标题\n旧内容',
      status: 'inbox',
      source: 'web',
      attachmentCount: 0,
      attachments: null,
      createdAt: '2026-10-01T10:00:00Z',
      updatedAt: '2026-10-01T10:00:00Z',
      archivedAt: null,
    }],
    totalCount: 1,
    page: 1,
    pageSize: 20,
    totalPages: 1,
  })
  quickApi.get.mockResolvedValue({
    id: '44444444-4444-4444-8444-444444444444',
    contentPreview: '旧标题\n旧内容',
    contentMarkdown: '<h1>旧标题</h1><p>旧内容 <strong>保留</strong></p>',
    metadataJson: '{}',
    status: 'inbox',
    source: 'web',
    attachmentCount: 0,
    attachments: null,
    createdAt: '2026-10-01T10:00:00Z',
    updatedAt: '2026-10-01T10:00:00Z',
    archivedAt: null,
  })
})

afterEach(() => {
  cleanup()
  client?.clear()
  setApiBase('')
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

async function consentToStorage() {
  await userEvent.click(screen.getByLabelText('确认保存到当前服务器'))
}

async function typeBody(text: string) {
  const editor = await screen.findByLabelText('笔记正文')
  await userEvent.click(editor)
  await userEvent.keyboard(text)
}

describe('NativeNotesPage', () => {
  it('keeps notes as the sole collection entry and hides admin governance from ordinary users', () => {
    const collection = navGroupsForRole('user').find((group) => group.label === '收集')
    expect(collection?.items).toContainEqual(expect.objectContaining({ to: '/native-notes', label: '笔记' }))
    expect(collection?.items.some((item) => item.to === '/quick-notes')).toBe(false)
    const ordinaryGovernance = navGroupsForRole('user').find((group) => group.label === '治理')
    expect(ordinaryGovernance?.items.some((item) => item.to === '/data-center')).toBe(false)
    const adminGovernance = navGroupsForRole('admin').find((group) => group.label === '治理')
    expect(adminGovernance?.items).toContainEqual(expect.objectContaining({ to: '/data-center', label: '数据中心' }))
  })

  it('shows a compact first-load failure with exact reason and in-place retry', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    nativeApi.notes.mockRejectedValueOnce(new Error('数据库暂时不可用'))
    page()

    expect(await screen.findByText('笔记库读取失败')).toBeTruthy()
    expect(screen.getByText('数据库暂时不可用')).toBeTruthy()
    nativeApi.notes.mockResolvedValueOnce({ total: 1, offset: 0, items: [source] })
    await userEvent.click(screen.getByRole('button', { name: '就地重试' }))

    expect(await screen.findByText('课堂笔记')).toBeTruthy()
  })

  it('shows a stable initial loading surface before the first note page arrives', async () => {
    let resolveNotes!: (value: { total: number; offset: number; items: typeof source[] }) => void
    nativeApi.notes.mockReturnValueOnce(new Promise((resolve) => {
      resolveNotes = resolve
    }))
    page()

    expect(await screen.findByLabelText('正在读取笔记')).toBeTruthy()
    expect(screen.getByText('正在读取笔记…')).toBeTruthy()
    resolveNotes({ total: 1, offset: 0, items: [source] })
    expect(await screen.findByText('课堂笔记')).toBeTruthy()
  })

  it('keeps the loaded list and scroll surface after a refresh failure, then retries in place', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    page()
    await screen.findByText('课堂笔记')
    nativeApi.notes.mockRejectedValueOnce(new Error('刷新连接超时'))

    await userEvent.click(screen.getByRole('button', { name: '刷新笔记库' }))
    expect(await screen.findByText('更新失败')).toBeTruthy()
    expect(screen.getByText('刷新连接超时')).toBeTruthy()
    expect(screen.getByText('课堂笔记')).toBeTruthy()
    expect(screen.getByText('列表与浏览位置保持不变。')).toBeTruthy()
    expect(consoleError).toHaveBeenCalledWith(
      '[native-notes] 笔记列表读取失败',
      expect.objectContaining({ message: '刷新连接超时' }),
    )

    nativeApi.notes.mockResolvedValueOnce({ total: 1, offset: 0, items: [source] })
    await userEvent.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(screen.queryByText('更新失败')).toBeNull())
    expect(screen.getByText('课堂笔记')).toBeTruthy()
  })

  it('uses the confirmed empty-library and empty-search language while keeping search editable', async () => {
    nativeApi.notes.mockResolvedValueOnce({ total: 0, offset: 0, items: [] })
    page()
    expect(await screen.findByText('还没有笔记')).toBeTruthy()
    expect(screen.getByRole('button', { name: '新建笔记' })).toBeTruthy()

    nativeApi.notes.mockResolvedValueOnce({ total: 1, offset: 0, items: [source] })
    await userEvent.click(screen.getByRole('button', { name: '刷新笔记库' }))
    await screen.findByText('课堂笔记')
    const search = screen.getByLabelText('搜索笔记标题')
    await userEvent.type(search, '不存在的标题')
    expect(await screen.findByText('没有匹配的笔记')).toBeTruthy()
    expect((search as HTMLInputElement).value).toBe('不存在的标题')
    await userEvent.click(screen.getAllByRole('button', { name: '清空搜索' })[0]!)
    expect(await screen.findByText('课堂笔记')).toBeTruthy()
  })

  it('uses one continuous body editor, prioritizes it on narrow screens and omits the library headline while editing', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: '笔记库' })).toBeNull()
    expect(screen.queryByText('PIM 笔记')).toBeNull()
    expect(screen.getAllByLabelText('笔记正文')).toHaveLength(1)
    expect(screen.queryByLabelText('正文块 1')).toBeNull()
    const workspace = screen.getByTestId('note-workspace')
    expect(workspace.getAttribute('data-mode')).toBe('editing')
    expect(workspace.className).toContain('xl:grid-cols-[18rem_minmax(0,1fr)]')
    expect(screen.getByTestId('note-library-pane').className).toContain('hidden')
    expect(screen.getByRole('button', { name: '返回笔记列表' })).toBeTruthy()
    const versionHeading = screen.getByRole('heading', { name: '版本与保存编号' })
    expect(screen.getByLabelText('笔记正文').compareDocumentPosition(versionHeading) &
      Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('keeps the format toolbar below a wrapped sticky editor header on narrow screens', async () => {
    let measuredHeaderHeight = 96
    let resizeCallback: ResizeObserverCallback | undefined
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: ResizeObserverCallback) {
        resizeCallback = callback
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    })
    const originalBounds = HTMLElement.prototype.getBoundingClientRect
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.dataset.testid === 'note-editor-sticky-header') {
        return {
          ...originalBounds.call(this),
          height: measuredHeaderHeight,
          bottom: measuredHeaderHeight,
        }
      }
      return originalBounds.call(this)
    })

    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    const header = await screen.findByTestId('note-editor-sticky-header')
    const toolbar = screen.getByTestId('note-format-toolbar')
    const stickyRoot = header.parentElement as HTMLElement

    expect(header.style.top).toBe('var(--note-sticky-top, 0px)')
    expect(toolbar.style.top).toContain('var(--note-sticky-header-height')
    await waitFor(() =>
      expect(stickyRoot.style.getPropertyValue('--note-sticky-header-height')).toBe('96px'))

    measuredHeaderHeight = 148
    act(() => resizeCallback?.([], {} as ResizeObserver))
    await waitFor(() =>
      expect(stickyRoot.style.getPropertyValue('--note-sticky-header-height')).toBe('148px'))
    expect(toolbar.style.top).not.toBe(header.style.top)
  })

  it('loads a saved note without marking the untouched editor as dirty', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByLabelText('笔记正文')).toBeTruthy()
    expect(screen.queryByText('有未保存更改')).toBeNull()
    expect(screen.getByText('已从服务器载入')).toBeTruthy()
  })

  it('retries a failed current-note read in place and then opens the continuous editor', async () => {
    nativeApi.current.mockRejectedValueOnce(new ApiError('当前笔记暂时不可读取', 50301, 503))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    const editorPane = screen.getByTestId('note-editor-pane')
    await within(editorPane).findByRole('button', { name: '就地重试' })
    expect(within(editorPane).getAllByText('当前版本读取失败')).toHaveLength(1)
    expect(within(editorPane).getAllByText('当前笔记暂时不可读取').length).toBeGreaterThan(0)
    await userEvent.click(within(editorPane).getByRole('button', { name: '就地重试' }))

    expect(await within(editorPane).findByLabelText('笔记正文')).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
  })

  it('requires the server-storage confirmation before ending and then saves the continuous body', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('结束前写入的连续正文')

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText('结束这次记录？')).toBeTruthy()
    await userEvent.click(dialog.getByRole('button', { name: '结束并保存' }))
    expect(await dialog.findByText(/结束前需要先确认保存到当前服务器/)).toBeTruthy()
    expect(nativeApi.complete).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(await screen.findByLabelText('笔记正文')).toBeTruthy()

    await userEvent.click(dialog.getByRole('button', { name: '继续编辑' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await consentToStorage()
    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const nextDialog = within(await screen.findByRole('dialog'))
    await userEvent.click(nextDialog.getByRole('button', { name: '结束并保存' }))
    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    expect(await screen.findByText(/本次记录已结束/)).toBeTruthy()
    expect(testRouter.state.location.search).toBe('')
  })

  it('ends an already saved note without creating an empty upload', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/服务器上的当前版本会保持不变/)).toBeTruthy()
    await userEvent.click(dialog.getByRole('button', { name: '结束' }))

    expect(nativeApi.begin).not.toHaveBeenCalled()
    expect(nativeApi.clone).not.toHaveBeenCalled()
    expect(await screen.findByText(/没有检测到需要提交的改动/)).toBeTruthy()
    expect(testRouter.state.location.search).toBe('')
  })

  it('ends a new note with no body without requiring consent or an empty-title upload', async () => {
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await screen.findByRole('heading', { name: '新建笔记' })
    await userEvent.type(screen.getByLabelText('笔记标题'), '只有标题，还没有正文')

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/当前没有可保存的正文，不会在服务器创建新笔记/)).toBeTruthy()
    await userEvent.click(dialog.getByRole('button', { name: '结束' }))

    expect(nativeApi.begin).not.toHaveBeenCalled()
    expect(nativeApi.clone).not.toHaveBeenCalled()
    expect(await screen.findByText(/当前没有可保存的正文，未在服务器创建笔记/)).toBeTruthy()
    expect(testRouter.state.location.search).toBe('')
  })

  it('keeps the draft when the end confirmation is cancelled or closed', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('取消结束也不能丢失草稿')

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    let dialog = within(await screen.findByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: '继续编辑' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('笔记正文').textContent).toContain('取消结束也不能丢失草稿')

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    dialog = within(await screen.findByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: '关闭' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('笔记正文').textContent).toContain('取消结束也不能丢失草稿')
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
    expect(nativeApi.clone).not.toHaveBeenCalled()
  })

  it('keeps the dirty note and allows retrying the same end submission after failure', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('结束保存失败仍保留')
    await consentToStorage()
    nativeApi.clone.mockRejectedValueOnce(new Error('结束保存网络失败'))

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: '结束并保存' }))
    expect(await screen.findByText(/结束保存网络失败/)).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
    expect((screen.getByLabelText('笔记正文')).textContent).toContain('结束保存失败仍保留')
    expect(screen.getAllByText('有未保存更改').length).toBeGreaterThan(0)
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(await dialog.findByText(/结束保存网络失败/)).toBeTruthy()

    await userEvent.click(dialog.getByRole('button', { name: '结束并保存' }))
    await waitFor(() => expect(nativeApi.clone).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(testRouter.state.location.search).toBe(''))
    expect(await screen.findByText(/本次记录已结束/)).toBeTruthy()
  })

  it('reports duplicate block identities in the end confirmation and preserves the draft', async () => {
    const duplicate = duplicateBlockMaterial()
    nativeApi.current.mockResolvedValue(duplicate)
    nativeApi.version.mockResolvedValue(duplicate)
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('重复标识错误也必须保留这段草稿')
    await consentToStorage()

    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: '结束并保存' }))

    expect(await dialog.findByText(/正文块标识重复/)).toBeTruthy()
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(nativeApi.begin).not.toHaveBeenCalled()
    expect(nativeApi.clone).not.toHaveBeenCalled()
    expect(screen.getByLabelText('笔记正文').textContent).toContain('重复标识错误也必须保留这段草稿')

    await userEvent.click(dialog.getByRole('button', { name: '继续编辑' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByText(/正文块标识重复/)).toBeTruthy()
    expect(screen.getByLabelText('笔记正文').textContent).toContain('重复标识错误也必须保留这段草稿')
  })

  it('keeps the end confirmation and frozen submission when Escape is pressed during submission', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('提交中不能因 Esc 丢失冻结状态')
    await consentToStorage()

    let rejectClone!: (reason?: unknown) => void
    nativeApi.clone.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((_resolve, reject) => {
      rejectClone = reject
    }))
    await userEvent.click(screen.getByRole('button', { name: '结束' }))
    const dialog = within(await screen.findByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: '结束并保存' }))
    await waitFor(() => expect(nativeApi.clone).toHaveBeenCalledOnce())

    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(screen.queryByRole('button', { name: '关闭' })).toBeNull()
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
    expect(screen.getByLabelText('笔记正文').textContent).toContain('提交中不能因 Esc 丢失冻结状态')

    rejectClone(new Error('结束提交网络失败'))
    expect(await screen.findByText(/结束提交网络失败/)).toBeTruthy()
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(screen.getByLabelText('笔记正文').textContent).toContain('提交中不能因 Esc 丢失冻结状态')
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: '结束并保存' })).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
  })

  it('keeps daily note actions primary and labels offline restore as optional', async () => {
    page()
    await screen.findByText('课堂笔记')
    expect(screen.getByRole('button', { name: /新建笔记/ })).toBeTruthy()
    expect(screen.getByRole('textbox', { name: '搜索笔记标题' })).toBeTruthy()
    const more = screen.getByText(/更多：旧记录与导入/).closest('details')
    expect(more?.open).toBe(false)

    await userEvent.click(screen.getByText(/更多：旧记录与导入/))
    expect(more?.open).toBe(true)
    expect(screen.getByText('旧快速记录')).toBeTruthy()
    expect(screen.getByRole('heading', { name: '从 Android 导入' })).toBeTruthy()
    expect(screen.getByText(/云端笔记会直接显示在笔记列表中，无需导入/)).toBeTruthy()
    expect(screen.getByText(/仅用于可选的 Android 文件交换或备份恢复/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '选择笔记包' })).toBeTruthy()
    expect(screen.getByText(/不会自动合并、覆盖或删除旧记录/)).toBeTruthy()
  })

  it('does not block reading when upload limits or processing configuration are unavailable', async () => {
    nativeApi.limits.mockRejectedValue(new Error('未配置处理目标'))
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [source] })
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).disabled).toBe(false)
    expect(screen.getByRole('button', { name: '下载完整原始材料包' }).hasAttribute('disabled')).toBe(false)
    expect(screen.getByRole('button', { name: '整理笔记' }).hasAttribute('disabled')).toBe(false)
    expect(screen.queryByRole('button', { name: '转写录音' })).toBeNull()
  })

  it('opens transcription before organization for recordings and keeps the exact source version', async () => {
    nativeApi.current.mockResolvedValue({ ...material, inputJson: JSON.stringify({ ...JSON.parse(inputJson), recordings: [{ id: 'recording' }] }) })
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: '转写录音' }))

    expect(screen.getByText('处理页')).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?noteId=${noteId}&versionId=${versionId}&action=transcribe`)
  })

  it('does not expose a version id when ordinary users open processing', async () => {
    authState.role = 'user'
    nativeApi.current.mockResolvedValue({ ...material, inputJson: JSON.stringify({ ...JSON.parse(inputJson), recordings: [{ id: 'recording' }] }) })
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: '转写录音' }))

    expect(screen.getByText('处理页')).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?noteId=${noteId}&action=transcribe`)
  })

  it('opens organization directly for text and image notes', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('button', { name: '整理笔记' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: '转写录音' })).toBeNull()
  })

  it('opens a direct note URL after current loads even while the library is still pending', async () => {
    let releaseNotes!: (value: { total: number; offset: number; items: typeof source[] }) => void
    nativeApi.notes.mockReturnValue(new Promise((resolve) => { releaseNotes = resolve }))
    page(`/native-notes?note=${noteId}`)

    expect(await screen.findByRole('button', { name: '整理笔记' })).toBeTruthy()
    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()

    releaseNotes({ total: 1, offset: 0, items: [source] })
    await waitFor(() => expect(screen.getByText('课堂笔记')).toBeTruthy())
  })

  it('saves first, then opens processing for the exact note and version', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: '整理笔记' }))
    expect(screen.getByText('处理页')).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?noteId=${noteId}&versionId=${versionId}&action=organize`)
    expect(sessionStorage.getItem(`pim:native-notes:return-version:${authState.userId}:${window.location.origin}`)).toBe(noteId)
    await userEvent.click(screen.getByRole('button', { name: '返回笔记库' }))
    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('课堂笔记')
  })

  it('does not send a dirty draft to processing before the user saves it', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.clear(await screen.findByLabelText('笔记标题'))
    await userEvent.type(screen.getByLabelText('笔记标题'), '尚未保存的标题')
    const organize = await screen.findByRole('button', { name: '整理笔记' })
    expect((organize as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(organize)
    expect(testRouter.state.location.pathname).toBe('/native-notes')
    expect(confirm).not.toHaveBeenCalled()
  })

  it('允许含录音的固定版本下载原始包，但不启用重建导出', async () => {
    nativeApi.downloadBundle.mockResolvedValue({
      blob: new Blob([new Uint8Array([1, 2, 3])], { type: 'application/zip' }),
      filename: `${versionId}.pimnote.zip`,
    })
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    page()
    await userEvent.click((await screen.findByText('课堂笔记')).closest('button')!)
    const downloadButton = await screen.findByRole('button', { name: '下载完整原始材料包' })
    expect(downloadButton.hasAttribute('disabled')).toBe(false)
    expect(screen.getByText(/128 MiB/)).toBeTruthy()

    await userEvent.click(downloadButton)
    await waitFor(() => expect(nativeApi.downloadBundle).toHaveBeenCalledWith(versionId, expect.any(AbortSignal)))
    await waitFor(() => expect(anchorClick).toHaveBeenCalledOnce())
    expect(URL.createObjectURL).toHaveBeenCalledWith(expect.objectContaining({ type: 'application/zip' }))
  })

  it('服务器切换后丢弃晚到的旧下载，不创建 URL 或触发下载', async () => {
    let resolveDownload!: (value: { blob: Blob; filename: string }) => void
    nativeApi.downloadBundle.mockImplementationOnce(() => new Promise((resolve) => { resolveDownload = resolve }))
    const createUrl = vi.mocked(URL.createObjectURL)
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    page()
    await userEvent.click((await screen.findByText('课堂笔记')).closest('button')!)
    await userEvent.click(await screen.findByRole('button', { name: '下载完整原始材料包' }))
    await waitFor(() => expect(nativeApi.downloadBundle).toHaveBeenCalledOnce())
    const oldSignal = nativeApi.downloadBundle.mock.calls[0]?.[1] as AbortSignal
    const createUrlCalls = createUrl.mock.calls.length

    setApiBase('https://new-server.synthetic.invalid')
    await waitFor(() => expect(oldSignal.aborted).toBe(true))
    resolveDownload({
      blob: new Blob([new Uint8Array([1])], { type: 'application/zip' }),
      filename: `${versionId}.pimnote.zip`,
    })
    await Promise.resolve()

    expect(createUrl).toHaveBeenCalledTimes(createUrlCalls)
    expect(anchorClick).not.toHaveBeenCalled()
  })

  it('账号切换后丢弃晚到的旧下载，不创建 URL 或触发下载', async () => {
    let resolveDownload!: (value: { blob: Blob; filename: string }) => void
    nativeApi.downloadBundle.mockImplementationOnce(() => new Promise((resolve) => { resolveDownload = resolve }))
    const createUrl = vi.mocked(URL.createObjectURL)
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    const view = page()
    await userEvent.click((await screen.findByText('课堂笔记')).closest('button')!)
    await userEvent.click(await screen.findByRole('button', { name: '下载完整原始材料包' }))
    await waitFor(() => expect(nativeApi.downloadBundle).toHaveBeenCalledOnce())
    const oldSignal = nativeApi.downloadBundle.mock.calls[0]?.[1] as AbortSignal
    const createUrlCalls = createUrl.mock.calls.length

    authState.userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    const nextRouter = createMemoryRouter([
      {
        path: '/native-notes',
        element: <QueryClientProvider client={client}><NativeNotesPage /></QueryClientProvider>,
      },
    ], { initialEntries: ['/native-notes'] })
    view.rerender(<RouterProvider router={nextRouter} />)
    await waitFor(() => expect(oldSignal.aborted).toBe(true))
    resolveDownload({
      blob: new Blob([new Uint8Array([1])], { type: 'application/zip' }),
      filename: `${versionId}.pimnote.zip`,
    })
    await Promise.resolve()

    expect(createUrl).toHaveBeenCalledTimes(createUrlCalls)
    expect(anchorClick).not.toHaveBeenCalled()
  })

  it('下载失败时保留本机编辑稿并明确不生成文件', async () => {
    nativeApi.downloadBundle.mockRejectedValueOnce(new Error('响应 MIME 不匹配'))
    page()
    await userEvent.click((await screen.findByText('课堂笔记')).closest('button')!)
    const title = await screen.findByLabelText('笔记标题') as HTMLInputElement
    await userEvent.clear(title)
    await userEvent.type(title, '本机稿保留')
    await userEvent.click(await screen.findByRole('button', { name: '下载完整原始材料包' }))

    expect(await screen.findByText(/本机草稿保持不变/)).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('本机稿保留')
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })

  it('shows the note library and reopens the exact fixed material version', async () => {
    page()
    expect(await screen.findByText('课堂笔记')).toBeTruthy()

    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('课堂笔记')
    expect((await screen.findByLabelText('笔记正文')).textContent).toContain('保留原文')
    expect(screen.getByText('当前载入版本 ID')).toBeTruthy()
    expect(screen.getByText('本次保存编号')).toBeTruthy()
    expect(screen.getByText('本次保存来源版本')).toBeTruthy()
    expect(nativeApi.version).toHaveBeenCalledWith(versionId, expect.any(AbortSignal))
    expect(client.getQueryData([
      'native-note-material',
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      window.location.origin,
      versionId,
    ])).toEqual(material)
    expect(client.getQueryCache().find({
      exact: true,
      queryKey: ['native-note-material', versionId],
    })).toBeUndefined()
  })

  it('creates a legacy quick-note copy only after an explicit user action', async () => {
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByText(/更多：旧记录与导入/))
    expect(screen.getByText(/不会自动合并、覆盖或删除旧记录/)).toBeTruthy()
    expect(quickApi.get).not.toHaveBeenCalled()

    await userEvent.click(await screen.findByRole('button', { name: '纯文本副本' }))
    await waitFor(() => expect(quickApi.get).toHaveBeenCalledWith('44444444-4444-4444-8444-444444444444'))
    expect(await screen.findByRole('heading', { name: '新建笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('旧标题')
    const body = (await screen.findByLabelText('笔记正文')).textContent ?? ''
    expect(body).toContain('旧标题')
    expect(body).toContain('旧内容 保留')
    expect(nativeApi.begin).not.toHaveBeenCalled()
  })

  it('resets editor identity when a copied legacy note replaces another new draft', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '尚未保存的第一份草稿')
    await userEvent.click(await screen.findByRole('button', { name: '纯文本副本' }))

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存'))
    expect(await screen.findByRole('heading', { name: '新建笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('旧标题')
    const body = (await screen.findByLabelText('笔记正文')).textContent ?? ''
    expect(body).toContain('旧标题')
    expect(body).toContain('旧内容 保留')
  })

  it('shows both conflict sides for admins and only promotes after an explicit choice', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('heading', { name: '版本与保存编号' })).toBeTruthy()
    expect(await screen.findByRole('heading', { name: '发现内容冲突' })).toBeTruthy()
    expect(screen.getAllByText(versionId).length).toBeGreaterThan(0)
    expect(screen.getAllByText(conflictVersionId).length).toBeGreaterThan(0)
    expect(screen.getByText('服务器当前内容')).toBeTruthy()
    expect(screen.getByText('另一份已保存内容')).toBeTruthy()
    expect(screen.getByText(/冲突候选笔记/)).toBeTruthy()
    expect(nativeApi.promote).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledWith(conflictVersionId, versionId, expect.any(AbortSignal)))
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
  })

  it('hides version history and internal save identifiers from ordinary users', async () => {
    authState.role = 'user'
    page()
    expect(await screen.findByText('已同步')).toBeTruthy()
    expect(screen.queryByText('当前版')).toBeNull()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect(screen.getByText('无未保存更改')).toBeTruthy()
    expect(screen.queryByRole('heading', { name: '版本与保存编号' })).toBeNull()
    expect(screen.queryByText('版本历史')).toBeNull()
    expect(screen.queryByText('当前载入版本 ID')).toBeNull()
    expect(screen.queryByText('本次保存编号')).toBeNull()
    expect(screen.queryByText('本次保存来源版本')).toBeNull()
    expect(screen.queryByText(versionId)).toBeNull()
    expect(screen.queryByRole('button', { name: '下载完整原始材料包' })).toBeNull()
  })

  it('does not let ordinary users open an arbitrary saved version by URL', async () => {
    authState.role = 'user'
    page(`/native-notes?note=${noteId}&version=${historyVersionId}&versionId=${historyVersionId}`)

    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    await waitFor(() => expect(testRouter.state.location.search).toBe(`?note=${noteId}`))
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('课堂笔记')
    expect(screen.queryByText('历史版本笔记')).toBeNull()
    expect(screen.queryByText(historyVersionId)).toBeNull()
  })

  it('shows ordinary conflict content without exposing internal version codes', async () => {
    authState.role = 'user'
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByRole('heading', { name: '发现内容冲突' })).toBeTruthy()
    expect(screen.getByText('服务器当前内容')).toBeTruthy()
    expect(screen.getByText('另一份已保存内容')).toBeTruthy()
    expect(screen.getByText(/冲突候选笔记/)).toBeTruthy()
    expect(screen.queryByText(versionId)).toBeNull()
    expect(screen.queryByText(conflictVersionId)).toBeNull()
    expect(screen.queryByText('版本 ID')).toBeNull()
    expect(screen.queryByText(/版本历史/)).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledWith(conflictVersionId, versionId, expect.any(AbortSignal)))
    expect((await screen.findAllByText(/已采用这份内容/)).length).toBeGreaterThan(0)
    expect(screen.queryByText(/版本历史/)).toBeNull()
  })

  it('lets a user cancel a conflict without changing the current version', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    await screen.findByRole('heading', { name: '发现内容冲突' })
    await userEvent.click(screen.getByRole('button', { name: '保留当前内容' }))

    expect(nativeApi.promote).not.toHaveBeenCalled()
    expect(await screen.findByText(/还有 1 个冲突候选待处理/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '查看冲突' })).toBeTruthy()
    await userEvent.click(screen.getByRole('button', { name: '查看冲突' }))
    expect(await screen.findByRole('heading', { name: '发现内容冲突' })).toBeTruthy()
  })

  it('refreshes both sides and explains a stale version choice instead of pretending success', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    nativeApi.promote.mockRejectedValue(new ApiError('服务器当前版本已变化', 40901, 409))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    await screen.findByRole('heading', { name: '发现内容冲突' })
    const currentCalls = nativeApi.current.mock.calls.length
    const versionCalls = nativeApi.versions.mock.calls.length
    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))

    expect(await screen.findByText(/服务器拒绝了本次版本选择；已重新读取当前内容和冲突候选/)).toBeTruthy()
    await waitFor(() => expect(nativeApi.current.mock.calls.length).toBeGreaterThan(currentCalls))
    await waitFor(() => expect(nativeApi.versions.mock.calls.length).toBeGreaterThan(versionCalls))
    expect(screen.getByRole('heading', { name: '发现内容冲突' })).toBeTruthy()
    expect(screen.queryByText(/已采用所选内容/)).toBeNull()
  })

  it('hides stale conflict choices when only part of the 409 recovery can be refreshed', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    nativeApi.promote.mockRejectedValue(new ApiError('服务器当前版本已变化', 40901, 409))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    nativeApi.current.mockRejectedValueOnce(new ApiError('当前版本刷新失败', 50301, 503))
    nativeApi.versions.mockRejectedValueOnce(new ApiError('版本历史刷新失败', 50302, 503))
    nativeApi.version.mockRejectedValueOnce(new ApiError('候选材料刷新失败', 50303, 503))
    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))

    expect(await screen.findByText(/未能重新读取当前版本、版本历史、冲突候选材料/)).toBeTruthy()
    expect(screen.queryByRole('heading', { name: '发现内容冲突' })).toBeNull()
    expect(screen.queryByText(/已重新读取当前内容和冲突候选/)).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: '重新读取并核对' }))
    expect(await screen.findByRole('heading', { name: '发现内容冲突' })).toBeTruthy()
  })

  it('shows ordinary users a visible retry when the first conflict-state read fails', async () => {
    authState.role = 'user'
    nativeApi.versions.mockRejectedValueOnce(new Error('冲突列表读取失败'))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByText('冲突状态读取失败')).toBeTruthy()
    expect(screen.getByText(/冲突列表读取失败/)).toBeTruthy()
    expect(screen.queryByText(versionId)).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '重新读取并核对' }))
    await waitFor(() => expect(nativeApi.versions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText('冲突状态读取失败')).toBeNull())
  })

  it('does not claim the current version is unchanged for an unconfirmed promote failure', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    nativeApi.promote.mockRejectedValue(new ApiError('网关在确认前超时', 50401, 504))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })
    const currentCalls = nativeApi.current.mock.calls.length
    const versionCalls = nativeApi.versions.mock.calls.length

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))

    expect(await screen.findByText(/未确认本次选择是否成功，请刷新核对后再选择/)).toBeTruthy()
    await waitFor(() => expect(nativeApi.current.mock.calls.length).toBeGreaterThan(currentCalls))
    await waitFor(() => expect(nativeApi.versions.mock.calls.length).toBeGreaterThan(versionCalls))
    expect(screen.getByText(/已重新读取当前版本和冲突候选/)).toBeTruthy()
    expect(screen.queryByText(/当前版本没有改变/)).toBeNull()
    expect(screen.queryByText(/已采用所选内容/)).toBeNull()
  })

  it('disables cancellation while a promote request is in flight', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    let resolvePromote!: (value: NoteMaterialUpload) => void
    nativeApi.promote.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((resolve) => {
      resolvePromote = resolve
    }))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledOnce())
    const keepCurrent = screen.getByRole('button', { name: '保留当前内容' }) as HTMLButtonElement
    const later = screen.getByRole('button', { name: '稍后处理' }) as HTMLButtonElement
    expect(keepCurrent.disabled).toBe(true)
    expect(later.disabled).toBe(true)
    await userEvent.click(keepCurrent)
    await userEvent.click(later)
    expect(screen.queryByText(/已保留服务器当前版本/)).toBeNull()

    resolvePromote({
      ...material.upload,
      versionId: conflictVersionId,
      currentVersionId: conflictVersionId,
      isCurrent: true,
      hasConflict: false,
    })
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
    expect(screen.queryByText(/已保留服务器当前版本/)).toBeNull()
  })

  it('keeps the current note selected while a promote request is in flight', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    let resolvePromote!: (value: NoteMaterialUpload) => void
    nativeApi.promote.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((resolve) => {
      resolvePromote = resolve
    }))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })
    const initialSearch = testRouter.state.location.search

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledOnce())
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))

    expect(await screen.findByText(/正在提交版本选择/)).toBeTruthy()
    expect(testRouter.state.location.search).toBe(initialSearch)
    expect(screen.queryByRole('heading', { name: '新建笔记' })).toBeNull()

    resolvePromote({
      ...material.upload,
      versionId: conflictVersionId,
      currentVersionId: conflictVersionId,
      isCurrent: true,
      hasConflict: false,
    })
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
  })

  it('aborts an in-flight promote and ignores its late success after the account changes', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    let resolvePromote!: (value: NoteMaterialUpload) => void
    nativeApi.promote.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((resolve) => {
      resolvePromote = resolve
    }))
    const view = page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledOnce())
    const signal = nativeApi.promote.mock.calls[0]?.[2] as AbortSignal

    await testRouter.navigate('/native-notes')
    authState.userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    view.rerender(<RouterProvider key={authState.userId} router={testRouter} />)
    await waitFor(() => expect(signal.aborted).toBe(true))
    expect(await screen.findByText('课堂笔记')).toBeTruthy()

    resolvePromote({
      ...material.upload,
      versionId: conflictVersionId,
      currentVersionId: conflictVersionId,
      isCurrent: true,
      hasConflict: false,
    })
    await Promise.resolve()
    await Promise.resolve()

    expect(testRouter.state.location.search).toBe('')
    expect(screen.queryByText(/已采用所选内容/)).toBeNull()
    expect(screen.getByText('课堂笔记')).toBeTruthy()
  })

  it('keeps candidate adoption disabled until its safe summary is loaded', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    let resolveCandidate!: (value: NoteMaterialVersion) => void
    nativeApi.version.mockImplementation((id: string) => id === conflictVersionId
      ? new Promise<NoteMaterialVersion>((resolve) => { resolveCandidate = resolve })
      : Promise.resolve(material))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    const adopt = screen.getByRole('button', { name: '采用这份内容' }) as HTMLButtonElement
    expect(adopt.disabled).toBe(true)
    expect(screen.getByText('另一份内容读取中，暂不能采用。')).toBeTruthy()
    expect(nativeApi.promote).not.toHaveBeenCalled()

    resolveCandidate(conflictMaterial)
    await waitFor(() => expect((screen.getByRole('button', { name: '采用这份内容' }) as HTMLButtonElement).disabled).toBe(false))
    expect(screen.getByText('冲突候选笔记')).toBeTruthy()
  })

  it('keeps candidate adoption disabled when its summary cannot be read', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    nativeApi.version.mockImplementation((id: string) => id === conflictVersionId
      ? Promise.reject(new ApiError('候选材料读取失败', 50304, 503))
      : Promise.resolve(material))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByText('候选材料读取失败')).toBeTruthy()
    expect((screen.getByRole('button', { name: '采用这份内容' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.getByText(/内容暂未读取，暂不能采用/)).toBeTruthy()
    expect(nativeApi.promote).not.toHaveBeenCalled()
  })

  it('opens a conflict candidate for reading without silently promoting it to the current version', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*冲突候选/ }))

    await waitFor(() => expect(nativeApi.version).toHaveBeenCalledWith(conflictVersionId, expect.any(AbortSignal)))
    expect((await screen.findByLabelText('笔记标题') as HTMLInputElement).value).toBe('冲突候选笔记')
    expect(screen.getByText(/这是一份历史版本，仅供查看/)).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).disabled).toBe(true)
    expect((screen.getByLabelText('确认保存到当前服务器') as HTMLInputElement).disabled).toBe(true)
    expect(screen.getByRole('button', { name: '明确设为当前版本' })).toBeTruthy()
    expect(nativeApi.promote).not.toHaveBeenCalled()
  })

  it('promotes a history version through the page handler and refreshes shared version state', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload, historyMaterial.upload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*历史版/ }))

    const promoteButton = await screen.findByRole('button', { name: '明确设为当前版本' })
    const currentCalls = nativeApi.current.mock.calls.length
    const versionCalls = nativeApi.versions.mock.calls.length
    await userEvent.click(promoteButton)

    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledWith(
      historyVersionId,
      versionId,
      expect.any(AbortSignal),
    ))
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
    await waitFor(() => expect(nativeApi.current.mock.calls.length).toBeGreaterThan(currentCalls))
    await waitFor(() => expect(nativeApi.versions.mock.calls.length).toBeGreaterThan(versionCalls))
    expect(testRouter.state.location.search).toBe(`?note=${noteId}`)
  })

  it('refreshes shared version state and explains a stale history promotion', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload, historyMaterial.upload])
    nativeApi.promote.mockRejectedValue(new ApiError('服务器当前版本已变化', 40901, 409))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*历史版/ }))

    const currentCalls = nativeApi.current.mock.calls.length
    const versionCalls = nativeApi.versions.mock.calls.length
    await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))

    expect(await screen.findByText(/服务器拒绝了本次版本选择；已重新读取当前内容和冲突候选/)).toBeTruthy()
    await waitFor(() => expect(nativeApi.current.mock.calls.length).toBeGreaterThan(currentCalls))
    await waitFor(() => expect(nativeApi.versions.mock.calls.length).toBeGreaterThan(versionCalls))
    expect(screen.queryByText(/当前选用版本没有改变/)).toBeNull()
    expect(screen.queryByText(/已明确将此版本设为当前版本/)).toBeNull()
  })

  it('does not claim a history promotion changed anything when the result is unknown', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload, historyMaterial.upload])
    nativeApi.promote.mockRejectedValue(new ApiError('网关在确认前超时', 50401, 504))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*历史版/ }))

    await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))

    expect(await screen.findByText(/未确认本次选择是否成功，请刷新核对后再选择/)).toBeTruthy()
    expect(screen.queryByText(/当前选用版本没有改变/)).toBeNull()
    expect(screen.queryByText(/已明确将此版本设为当前版本/)).toBeNull()
  })

  it('disables the conflict entry while a history promotion is in flight', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload, historyMaterial.upload])
    let resolvePromote!: (value: NoteMaterialUpload) => void
    nativeApi.promote.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((resolve) => {
      resolvePromote = resolve
    }))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*历史版/ }))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    const adopt = screen.getByRole('button', { name: '采用这份内容' }) as HTMLButtonElement
    await waitFor(() => expect(adopt.disabled).toBe(false))
    await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledOnce())
    expect(adopt.disabled).toBe(true)
    await userEvent.click(adopt)
    expect(nativeApi.promote).toHaveBeenCalledOnce()

    resolvePromote({
      ...material.upload,
      versionId: historyVersionId,
      currentVersionId: historyVersionId,
      isCurrent: true,
      hasConflict: false,
    })
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
  })

  it('disables the history entry while a conflict promotion is in flight', async () => {
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, hasConflict: true }] })
    nativeApi.versions.mockResolvedValue([material.upload, conflictMaterial.upload, historyMaterial.upload])
    let resolvePromote!: (value: NoteMaterialUpload) => void
    nativeApi.promote.mockImplementationOnce(() => new Promise<NoteMaterialUpload>((resolve) => {
      resolvePromote = resolve
    }))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /查看版本 .*历史版/ }))
    await screen.findByRole('heading', { name: '发现内容冲突' })

    const adopt = screen.getByRole('button', { name: '采用这份内容' }) as HTMLButtonElement
    await waitFor(() => expect(adopt.disabled).toBe(false))
    await userEvent.click(screen.getByRole('button', { name: '采用这份内容' }))
    await waitFor(() => expect(nativeApi.promote).toHaveBeenCalledOnce())
    const promoteFromHistory = screen.getByRole('button', { name: '明确设为当前版本' }) as HTMLButtonElement
    expect(promoteFromHistory.disabled).toBe(true)
    await userEvent.click(promoteFromHistory)
    expect(nativeApi.promote).toHaveBeenCalledOnce()

    resolvePromote({
      ...material.upload,
      versionId: conflictVersionId,
      currentVersionId: conflictVersionId,
      isCurrent: true,
      hasConflict: false,
    })
    expect((await screen.findAllByText(/已采用所选内容/)).length).toBeGreaterThan(0)
  })

  it('shows version history read errors without hiding the editable current version', async () => {
    nativeApi.versions.mockRejectedValue(new ApiError('当前账号无权读取此笔记', 40301, 403))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByText('版本历史读取失败')).toBeTruthy()
    expect(screen.getByText(/当前账号无权读取此笔记/)).toBeTruthy()
    expect(screen.getByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).disabled).toBe(false)
  })

  it('loads version history when the first current-version read fails', async () => {
    const historyUpload: NoteMaterialUpload = {
      ...material.upload,
      versionId: '77777777-7777-4777-8777-777777777777',
      isCurrent: false,
    }
    nativeApi.current.mockRejectedValueOnce(new ApiError('当前版本首次读取失败', 50301, 503))
    nativeApi.versions.mockResolvedValue([material.upload, historyUpload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect((await screen.findAllByText('当前版本读取失败')).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/当前版本首次读取失败/).length).toBeGreaterThan(0)
    expect(await screen.findByRole('list', { name: '版本历史列表' })).toBeTruthy()
    expect(screen.getByText('历史版')).toBeTruthy()
    expect(screen.queryByText('版本历史读取失败')).toBeNull()
  })

  it('distinguishes pending, failed and truly empty history when current fails first', async () => {
    nativeApi.current.mockRejectedValueOnce(new ApiError('当前版本首次读取失败', 50301, 503))
    let resolveVersions!: (value: NoteMaterialUpload[]) => void
    nativeApi.versions.mockImplementationOnce(() => new Promise((resolve) => {
      resolveVersions = resolve
    }))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    await waitFor(() => expect(nativeApi.versions).toHaveBeenCalledOnce())
    expect((await screen.findAllByText('当前版本读取失败')).length).toBeGreaterThan(0)
    expect(screen.queryByText('还没有历史版本。')).toBeNull()
    expect(screen.queryByText('版本历史读取失败')).toBeNull()

    resolveVersions([])
    expect(await screen.findByText('还没有历史版本。')).toBeTruthy()
    expect(screen.getByText('0 个版本 · 0 个冲突候选')).toBeTruthy()
  })

  it('shows history read failure rather than an empty list when current fails first', async () => {
    nativeApi.current.mockRejectedValueOnce(new ApiError('当前版本首次读取失败', 50301, 503))
    nativeApi.versions.mockRejectedValueOnce(new ApiError('历史版本首次读取失败', 50302, 503))
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect((await screen.findAllByText('当前版本读取失败')).length).toBeGreaterThan(0)
    expect(await screen.findByText('版本历史读取失败')).toBeTruthy()
    expect(screen.getByText(/历史版本首次读取失败/)).toBeTruthy()
    expect(screen.queryByText('还没有历史版本。')).toBeNull()
  })

  it('keeps the loaded version history visible when refreshing the current version fails', async () => {
    const historyUpload: NoteMaterialUpload = {
      ...material.upload,
      versionId: '77777777-7777-4777-8777-777777777777',
      isCurrent: false,
    }
    nativeApi.versions.mockResolvedValue([material.upload, historyUpload])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    expect(await screen.findByText('历史版')).toBeTruthy()
    const title = screen.getByLabelText('笔记标题') as HTMLInputElement
    await userEvent.clear(title)
    await userEvent.type(title, '刷新失败仍保留')
    await typeBody('当前版本刷新失败也不能替换草稿')
    const versionCalls = nativeApi.versions.mock.calls.length

    nativeApi.current.mockRejectedValueOnce(new ApiError('当前版本暂时不可读取', 50301, 503))
    await userEvent.click(screen.getByRole('button', { name: '刷新版本历史' }))

    expect((await screen.findAllByText('当前版本读取失败')).length).toBeGreaterThan(0)
    await waitFor(() => expect(nativeApi.versions.mock.calls.length).toBeGreaterThan(versionCalls))
    expect(screen.getByRole('list', { name: '版本历史列表' })).toBeTruthy()
    expect(screen.getByText('历史版')).toBeTruthy()
    expect(screen.queryByText('版本历史读取失败')).toBeNull()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('刷新失败仍保留')
    expect(screen.getByLabelText('笔记正文').textContent).toContain('当前版本刷新失败也不能替换草稿')
    expect(screen.getAllByText('有未保存更改').length).toBeGreaterThan(0)

    await userEvent.click(screen.getByRole('button', { name: '重新读取当前版本' }))
    await waitFor(() => expect(screen.queryByText('当前版本读取失败')).toBeNull())
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('刷新失败仍保留')
    expect(screen.getByLabelText('笔记正文').textContent).toContain('当前版本刷新失败也不能替换草稿')
  })

  it('warns without replacing a dirty draft when the server current version advances', async () => {
    const nextMaterial: NoteMaterialVersion = {
      ...conflictMaterial,
      upload: {
        ...conflictMaterial.upload,
        currentVersionId: conflictVersionId,
        isCurrent: true,
        hasConflict: false,
      },
    }
    nativeApi.version.mockImplementation(async (id: string) =>
      id === conflictVersionId ? nextMaterial : material)
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    const title = await screen.findByLabelText('笔记标题') as HTMLInputElement
    expect(title.value).toBe('课堂笔记')
    await waitFor(() => expect(nativeApi.versions).toHaveBeenCalledOnce())
    await userEvent.clear(title)
    await userEvent.type(title, '本机未保存草稿')
    await typeBody('服务器版本更新也不能静默换稿')

    nativeApi.current.mockResolvedValueOnce(nextMaterial)
    nativeApi.versions.mockResolvedValueOnce([nextMaterial.upload])
    await userEvent.click(screen.getByRole('button', { name: '刷新版本历史' }))

    expect(await screen.findByText('服务器当前版本已更新')).toBeTruthy()
    expect(screen.getByText(/没有自动替换/)).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('本机未保存草稿')
    expect(screen.getByLabelText('笔记正文').textContent).toContain('服务器版本更新也不能静默换稿')
    expect(screen.getAllByText('有未保存更改').length).toBeGreaterThan(0)

    await userEvent.click(screen.getByRole('button', { name: '载入服务器最新版本' }))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('替换本页未保存草稿'))
    expect(await screen.findByDisplayValue('冲突候选笔记')).toBeTruthy()
    expect(screen.queryByText('服务器当前版本已更新')).toBeNull()
  })

  it('shows an explicit empty version history instead of a blank panel', async () => {
    nativeApi.versions.mockResolvedValue([])
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))

    expect(await screen.findByText('还没有历史版本。')).toBeTruthy()
    expect(screen.getByText('0 个版本 · 0 个冲突候选')).toBeTruthy()
  })

  it('publishes a new noteId through Begin, binary parts, and complete without generated-contract changes', async () => {
    page()
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    expect(await screen.findByRole('heading', { name: '新建笔记' })).toBeTruthy()
    await userEvent.type(screen.getByLabelText('笔记标题'), '合成新笔记')
    await typeBody('正文与照片走同一固定版本')
    expect((screen.getAllByRole('button', { name: '保存笔记' })[0]! as HTMLButtonElement).disabled).toBe(true)
    await consentToStorage()
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)

    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    expect(nativeApi.begin).toHaveBeenCalledOnce()
    const body = nativeApi.begin.mock.calls[0]![0]
    expect(body.baseVersionId).toBeNull()
    expect(body.storageConsent).toBe(true)
    expect(body.noteId).not.toBe(noteId)
    expect(JSON.parse(body.inputJson).note).toMatchObject({
      id: body.noteId,
      title: '合成新笔记',
      blocks: [{ kind: 'markdown', text: '正文与照片走同一固定版本' }],
    })
    expect(nativeApi.uploadPart).toHaveBeenCalled()
    expect(nativeApi.complete).toHaveBeenCalledWith(body.versionId, expect.any(AbortSignal))
  })

  it('publishes a selected photo in the same frozen manifest as the Markdown body', async () => {
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '混合材料笔记')
    await typeBody('照片与正文一起冻结')
    await consentToStorage()
    const photo = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3])], 'board.png', { type: 'image/png' })
    await userEvent.upload(screen.getByLabelText('添加照片'), photo)
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)

    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    const body = nativeApi.begin.mock.calls[0]![0]
    const manifest = JSON.parse(body.manifestJson) as {
      files: Array<{ kind: string; blockId?: string; sha256: string }>
    }
    const photoFile = manifest.files.find((file) => file.kind === 'Photo')
    expect(photoFile).toMatchObject({
      kind: 'Photo',
      blockId: expect.any(String),
      sha256: expect.stringMatching(/^[0-9a-f]{64}$/),
    })
    expect(JSON.parse(body.inputJson).note.blocks).toContainEqual(expect.objectContaining({
      kind: 'photo',
      assetId: expect.any(String),
      sha256: photoFile?.sha256,
    }))
  })

  it('publishes authored handwriting vectors and a matching preview in one fixed version', async () => {
    const context = {
      arc: vi.fn(), beginPath: vi.fn(), fill: vi.fn(), fillRect: vi.fn(),
      lineTo: vi.fn(), moveTo: vi.fn(), stroke: vi.fn(),
    }
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback) => {
      callback(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1])], { type: 'image/png' }))
    })
    page()
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '笔迹往返')
    await consentToStorage()
    await userEvent.click(screen.getByRole('button', { name: '添加手写卡片' }))

    const canvas = screen.getByTestId('ink-canvas')
    fireEvent.pointerDown(canvas, { pointerId: 1, pointerType: 'pen', isPrimary: true, clientX: 20, clientY: 30, pressure: 0.5 })
    fireEvent.pointerMove(canvas, { pointerId: 1, pointerType: 'pen', isPrimary: true, clientX: 80, clientY: 90, pressure: 0.8 })
    fireEvent.pointerUp(canvas, { pointerId: 1, pointerType: 'pen', isPrimary: true })
    await userEvent.click(screen.getByRole('button', { name: '保存手写' }))
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)

    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    const body = nativeApi.begin.mock.calls[0]![0]
    const vector = JSON.parse(body.inputJson).note.blocks.find((block: { kind: string }) => block.kind === 'ink')
    const preview = JSON.parse(body.manifestJson).files.find((file: { kind: string }) => file.kind === 'InkPreview')
    expect(vector.ink).toMatchObject({
      width: 320,
      height: 240,
      strokes: [{ points: [{ x: 20, y: 30 }, { x: 80, y: 90 }] }],
    })
    expect(preview).toMatchObject({ kind: 'InkPreview', blockId: vector.id, sizeBytes: 5 })
    expect(nativeApi.uploadPart).toHaveBeenCalled()
  })

  it('does not apply a late preview save to a newly opened ink block', async () => {
    const preview = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1])], { type: 'image/png' })
    let deferNextPreview = false
    let finishDeferredPreview: ((blob: Blob | null) => void) | undefined
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
      arc: vi.fn(), beginPath: vi.fn(), fill: vi.fn(), fillRect: vi.fn(),
      lineTo: vi.fn(), moveTo: vi.fn(), stroke: vi.fn(),
    } as unknown as CanvasRenderingContext2D)
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback) => {
      if (deferNextPreview) {
        deferNextPreview = false
        finishDeferredPreview = callback
        return
      }
      callback(preview)
    })
    page()
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))

    async function addInkStroke(x: number) {
      await userEvent.click(screen.getByRole('button', { name: '添加手写卡片' }))
      const canvas = screen.getByTestId('ink-canvas')
      fireEvent.pointerDown(canvas, { pointerId: x, pointerType: 'pen', isPrimary: true, clientX: x, clientY: 20, pressure: 0.5 })
      fireEvent.pointerMove(canvas, { pointerId: x, pointerType: 'pen', isPrimary: true, clientX: x + 20, clientY: 40, pressure: 0.8 })
      fireEvent.pointerUp(canvas, { pointerId: x, pointerType: 'pen', isPrimary: true })
      await userEvent.click(screen.getByRole('button', { name: '保存手写' }))
    }

    await addInkStroke(20)
    await addInkStroke(60)
    const editButtons = screen.getAllByRole('button', { name: /编辑手写卡片/ })
    expect(editButtons).toHaveLength(2)

    await userEvent.click(editButtons[0]!)
    const firstCanvas = screen.getByTestId('ink-canvas')
    fireEvent.pointerDown(firstCanvas, { pointerId: 90, pointerType: 'pen', isPrimary: true, clientX: 30, clientY: 60, pressure: 0.5 })
    fireEvent.pointerMove(firstCanvas, { pointerId: 90, pointerType: 'pen', isPrimary: true, clientX: 50, clientY: 80, pressure: 0.8 })
    fireEvent.pointerUp(firstCanvas, { pointerId: 90, pointerType: 'pen', isPrimary: true })
    deferNextPreview = true
    await userEvent.click(screen.getByRole('button', { name: '保存手写' }))
    await waitFor(() => expect(finishDeferredPreview).toBeTypeOf('function'))

    await userEvent.click(screen.getByRole('button', { name: '取消' }))
    await userEvent.click(screen.getAllByRole('button', { name: /编辑手写卡片/ })[1]!)
    finishDeferredPreview!(preview)
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(screen.getByTestId('ink-canvas-editor')).toBeTruthy()

    await userEvent.click(screen.getByRole('button', { name: '取消' }))
    await userEvent.click(screen.getAllByRole('button', { name: /编辑手写卡片/ })[1]!)
    expect(screen.getByTestId('ink-canvas').querySelectorAll('path')).toHaveLength(1)
  })

  it('imports an Android package into a local draft and still requires server-storage consent', async () => {
    const packageBundle = await buildMaterialBundle({
      versionId: '88888888-8888-4888-8888-888888888888',
      noteId: '99999999-9999-4999-8999-999999999999',
      baseVersionId: null,
      revision: 4,
      title: '从 Android 导入',
      noteCreatedAtMillis: 10,
      createdAtMillis: 20,
      blocks: [
        { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', kind: 'markdown', text: '混排正文' },
        {
          id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
          kind: 'photo',
          assetId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
          mimeType: 'image/png',
          bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        },
        {
          id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
          kind: 'ink',
          ink: { width: 320, height: 240, strokes: [{ width: 2, points: [{ x: 12, y: 18, pressure: 0.7 }] }] },
          bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        },
      ],
    })
    const archive = await exportAndroidNotePackage(packageBundle)
    page()
    const zipFile = new File([archive as BlobPart], 'android-note.zip', { type: 'application/zip' })
    await userEvent.upload(screen.getByLabelText('选择 Android 笔记包'), zipFile)

    expect(await screen.findByRole('heading', { name: '新建笔记' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('从 Android 导入')
    expect((await screen.findByLabelText('笔记正文')).textContent).toContain('混排正文')
    expect(screen.getByAltText('正文照片').getAttribute('src')).toBe('blob:synthetic-note-image')
    expect(screen.getByAltText('手写卡片').getAttribute('src')).toBe('blob:synthetic-note-image')
    expect(await screen.findByText(/尚未上传；保存时仍需明确确认服务器存储/)).toBeTruthy()
    expect((screen.getByLabelText('确认保存到当前服务器') as HTMLInputElement).checked).toBe(false)
    expect(nativeApi.begin).not.toHaveBeenCalled()

    await consentToStorage()
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    const body = nativeApi.begin.mock.calls[0]![0]
    expect(body.baseVersionId).toBeNull()
    expect(JSON.parse(body.inputJson).note.blocks.map((block: { kind: string }) => block.kind)).toEqual(['markdown', 'photo', 'ink'])
  })

  it('keeps the dirty draft when a note switch is cancelled and warns before unload', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '不可静默丢弃')

    const unload = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)

    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存'))
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('不可静默丢弃')
    expect(screen.getByRole('heading', { name: '新建笔记' })).toBeTruthy()
  })

  it('blocks route navigation while dirty and keeps the editor when cancelled', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '路由切换前先确认')

    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await waitFor(() => expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存')))
    expect(screen.queryByText('处理页')).toBeNull()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('路由切换前先确认')
  })

  it('guards route changes during ink input and keeps completed ink edits dirty after closing', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.click(screen.getByRole('button', { name: '添加手写卡片' }))

    fireEvent.pointerDown(screen.getByTestId('ink-canvas'), {
      pointerId: 14,
      pointerType: 'pen',
      isPrimary: true,
      clientX: 18,
      clientY: 22,
      pressure: 0.6,
    })
    expect((screen.getAllByRole('button', { name: '保存笔记' })[0]! as HTMLButtonElement).disabled).toBe(true)

    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await waitFor(() => expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存')))
    expect(screen.queryByText('处理页')).toBeNull()

    fireEvent.pointerMove(screen.getByTestId('ink-canvas'), {
      pointerId: 14,
      pointerType: 'pen',
      isPrimary: true,
      clientX: 60,
      clientY: 72,
      pressure: 0.8,
    })
    fireEvent.pointerUp(screen.getByTestId('ink-canvas'), { pointerId: 14, pointerType: 'pen', isPrimary: true })
    await userEvent.click(screen.getByRole('button', { name: '取消' }))
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await waitFor(() => expect(confirm).toHaveBeenCalledTimes(2))
    expect(screen.queryByText('处理页')).toBeNull()
    expect(screen.getByRole('heading', { name: '新建笔记' })).toBeTruthy()
  })

  it('keeps failed-save content dirty so the next switch requires confirmation', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    nativeApi.begin.mockRejectedValueOnce(new Error('合成网络失败'))
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '保存失败仍在内存')
    await consentToStorage()
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    expect(await screen.findByText(/合成网络失败/)).toBeTruthy()

    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存'))
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('保存失败仍在内存')
  })

  it('re-enables retry of the same frozen submission after a save failure', async () => {
    nativeApi.begin.mockRejectedValueOnce(new Error('合成网络失败'))
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '保存失败后重试')
    await typeBody('保存失败后仍可重试同一提交')
    await consentToStorage()
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    expect(await screen.findByText(/合成网络失败/)).toBeTruthy()

    const retry = screen.getAllByRole('button', { name: '重试同一提交' })[0] as HTMLButtonElement
    expect(retry.disabled).toBe(false)
    await userEvent.click(retry)
    await waitFor(() => expect(nativeApi.begin).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    expect(await screen.findByText(/已保存/)).toBeTruthy()
  })

  it('keeps the frozen submission, draft and material node when version refresh fails', async () => {
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await typeBody('冻结提交与材料节点都要保留')
    const photo = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3])], 'refresh.png', { type: 'image/png' })
    await userEvent.upload(screen.getByLabelText('添加照片'), photo)
    expect(await screen.findByAltText('正文照片')).toBeTruthy()
    await consentToStorage()

    nativeApi.clone.mockRejectedValueOnce(new Error('首次固定提交失败'))
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    expect(await screen.findByText(/首次固定提交失败/)).toBeTruthy()
    expect(screen.getAllByRole('button', { name: '重试同一提交' }).length).toBeGreaterThan(0)

    nativeApi.current.mockRejectedValueOnce(new Error('刷新当前版本失败'))
    nativeApi.version.mockRejectedValueOnce(new Error('刷新材料失败'))
    await userEvent.click(screen.getByRole('button', { name: '刷新版本历史' }))

    expect(await screen.findByText('笔记刷新失败，当前草稿已保留')).toBeTruthy()
    expect(screen.getByText(/刷新材料失败/)).toBeTruthy()
    expect(screen.getByLabelText('笔记正文').textContent).toContain('冻结提交与材料节点都要保留')
    expect(screen.getByAltText('正文照片')).toBeTruthy()
    expect(screen.getAllByRole('button', { name: '重试同一提交' }).length).toBeGreaterThan(0)

    nativeApi.version.mockResolvedValueOnce(material)
    await userEvent.click(screen.getByRole('button', { name: '就地重试' }))
    await waitFor(() => expect(screen.queryByText('笔记刷新失败，当前草稿已保留')).toBeNull())
    expect(screen.getByAltText('正文照片')).toBeTruthy()
    expect(screen.getAllByRole('button', { name: '重试同一提交' }).length).toBeGreaterThan(0)
  })

  it('aborts the previous upload closure before it can send later parts after a switch', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    let resolveBegin!: (value: NoteMaterialUpload) => void
    let beginSignal: AbortSignal | undefined
    nativeApi.begin.mockImplementationOnce((_body: { versionId: string; noteId: string }, signal?: AbortSignal) => {
      beginSignal = signal
      return new Promise<NoteMaterialUpload>((resolve) => {
        resolveBegin = resolve
      })
    })
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '切换前正在上传')
    await consentToStorage()
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    await waitFor(() => expect(nativeApi.begin).toHaveBeenCalledOnce())

    const body = nativeApi.begin.mock.calls[0]![0] as { versionId: string; noteId: string }
    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    await screen.findByRole('heading', { name: '继续写这篇笔记' })
    await waitFor(() => expect(beginSignal?.aborted).toBe(true))

    resolveBegin({
      ...material.upload,
      versionId: body.versionId,
      noteId: body.noteId,
      phase: 'Uploading',
      files: [],
    })
    await waitFor(() => expect(nativeApi.uploadPart).not.toHaveBeenCalled())
    expect(nativeApi.complete).not.toHaveBeenCalled()
  })
})
