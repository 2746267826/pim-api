// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NativeNotesPage } from './native-notes-page'
import { buildMaterialBundle, type NoteMaterialUpload, type NoteMaterialVersion } from './material-model'
import { exportAndroidNotePackage } from './material-package'

const nativeApi = vi.hoisted(() => ({
  limits: vi.fn(),
  sources: vi.fn(),
  version: vi.fn(),
  begin: vi.fn(),
  uploadPart: vi.fn(),
  complete: vi.fn(),
  promote: vi.fn(),
  fileBytes: vi.fn(),
}))
const quickApi = vi.hoisted(() => ({
  list: vi.fn(),
  get: vi.fn(),
}))
vi.mock('./api', () => ({
  bindNativeNotesApi: () => nativeApi,
}))
vi.mock('@/features/quick-notes/api', () => ({ quickNotesApi: quickApi }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({
    user: {
      id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      username: 'synthetic',
      displayName: '合成用户',
      role: 'user',
    },
  }),
}))

const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'
const conflictVersionId = '99999999-9999-4999-8999-999999999999'
const blockId = '33333333-3333-4333-8333-333333333333'
const source = {
  noteId,
  versionId,
  title: '课堂笔记',
  createdAt: '2026-11-01T10:00:00Z',
  isCurrent: true,
  hasConflict: false,
  recordings: 0,
  totalSamples: 0,
  sampleRate: 16000,
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

let client: QueryClient

function page() {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  const router = createMemoryRouter([
    {
      path: '/native-notes',
      element:
      <QueryClientProvider client={client}>
        <NativeNotesPage />
      </QueryClientProvider>,
    },
    { path: '/native-notes/processing', element: <p>处理页</p> },
    { path: '/quick-notes', element: <p>旧记录页</p> },
  ], { initialEntries: ['/native-notes'] })
  return render(<RouterProvider router={router} />)
}

beforeEach(() => {
  vi.resetAllMocks()
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => 'blob:synthetic-note-image') },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
  nativeApi.sources.mockResolvedValue({ total: 1, offset: 0, items: [source] })
  nativeApi.version.mockImplementation(async (id: string) => id === conflictVersionId ? conflictMaterial : material)
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
  nativeApi.uploadPart.mockResolvedValue('ok')
  nativeApi.complete.mockImplementation(async (id: string) => ({
    ...material.upload,
    versionId: id,
    phase: 'Uploaded',
    isCurrent: true,
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
  client.clear()
  vi.restoreAllMocks()
})

async function consentToStorage() {
  await userEvent.click(screen.getByLabelText('确认保存到当前服务器'))
}

describe('NativeNotesPage', () => {
  it('shows the note library and reopens the exact fixed material version', async () => {
    page()
    expect(await screen.findByText('课堂笔记')).toBeTruthy()
    expect(screen.getByText('纯文字/图片')).toBeTruthy()

    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    expect(await screen.findByRole('heading', { name: '编辑固定材料' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('课堂笔记')
    expect((screen.getByLabelText('正文块 1') as HTMLTextAreaElement).value).toContain('保留原文')
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
    await screen.findByText('旧闪念兼容')
    expect(screen.getByText(/不会自动合并、覆盖或删除旧记录/)).toBeTruthy()
    expect(quickApi.get).not.toHaveBeenCalled()

    await userEvent.click(await screen.findByRole('button', { name: '纯文本副本' }))
    await waitFor(() => expect(quickApi.get).toHaveBeenCalledWith('44444444-4444-4444-8444-444444444444'))
    expect(await screen.findByRole('heading', { name: '新建固定材料' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('旧标题')
    expect((screen.getByLabelText('正文块 1') as HTMLTextAreaElement).value).toBe('旧标题\n旧内容 保留')
    expect(nativeApi.begin).not.toHaveBeenCalled()
  })

  it('resets editor identity when a copied legacy note replaces another new draft', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '尚未保存的第一份草稿')
    await userEvent.click(await screen.findByRole('button', { name: '纯文本副本' }))

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存'))
    expect(await screen.findByRole('heading', { name: '新建固定材料' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('旧标题')
    expect((screen.getByLabelText('正文块 1') as HTMLTextAreaElement).value).toBe('旧标题\n旧内容 保留')
  })

  it('opens a conflict candidate without silently promoting it to the current version', async () => {
    nativeApi.sources.mockResolvedValue({
      total: 2,
      offset: 0,
      items: [
        source,
        {
          ...source,
          versionId: conflictVersionId,
          title: '冲突候选笔记',
          createdAt: '2026-11-01T10:05:00Z',
          isCurrent: false,
          hasConflict: true,
        },
      ],
    })
    page()
    await userEvent.click(await screen.findByText('课堂笔记').then((node) => node.closest('button')!))
    await userEvent.click(await screen.findByRole('button', { name: /打开版本 .*冲突候选/ }))

    await waitFor(() => expect(nativeApi.version).toHaveBeenCalledWith(conflictVersionId, expect.any(AbortSignal)))
    expect((await screen.findByLabelText('笔记标题') as HTMLInputElement).value).toBe('冲突候选笔记')
    expect(screen.getByText(/当前打开的不是笔记的选用版本/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '设为当前版本' })).toBeTruthy()
    expect(nativeApi.promote).not.toHaveBeenCalled()
  })

  it('publishes a new noteId through Begin, binary parts, and complete without generated-contract changes', async () => {
    page()
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    expect(await screen.findByRole('heading', { name: '新建固定材料' })).toBeTruthy()
    await userEvent.type(screen.getByLabelText('笔记标题'), '合成新笔记')
    await userEvent.clear(screen.getByLabelText('正文块 1'))
    await userEvent.type(screen.getByLabelText('正文块 1'), '正文与照片走同一固定版本')
    expect((screen.getByRole('button', { name: '保存新版本' }) as HTMLButtonElement).disabled).toBe(true)
    await consentToStorage()
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))

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
    await userEvent.clear(screen.getByLabelText('正文块 1'))
    await userEvent.type(screen.getByLabelText('正文块 1'), '照片与正文一起冻结')
    await consentToStorage()
    const photo = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3])], 'board.png', { type: 'image/png' })
    await userEvent.upload(screen.getByLabelText('添加照片'), photo)
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))

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
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))

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

    expect(await screen.findByRole('heading', { name: '新建固定材料' })).toBeTruthy()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('从 Android 导入')
    expect((screen.getByLabelText('正文块 1') as HTMLTextAreaElement).value).toBe('混排正文')
    expect(screen.getByAltText('正文照片 2').getAttribute('src')).toBe('blob:synthetic-note-image')
    expect(screen.getByAltText('手写卡片 3').getAttribute('src')).toBe('blob:synthetic-note-image')
    expect(await screen.findByText(/尚未上传；保存时仍需明确确认服务器存储/)).toBeTruthy()
    expect((screen.getByLabelText('确认保存到当前服务器') as HTMLInputElement).checked).toBe(false)
    expect(nativeApi.begin).not.toHaveBeenCalled()

    await consentToStorage()
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))
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
    expect(screen.getByRole('heading', { name: '新建固定材料' })).toBeTruthy()
  })

  it('blocks route navigation while dirty and keeps the editor when cancelled', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '路由切换前先确认')

    await userEvent.click(screen.getByRole('button', { name: /处理与生成/ }))
    await waitFor(() => expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存')))
    expect(screen.queryByText('处理页')).toBeNull()
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('路由切换前先确认')
  })

  it('keeps failed-save content dirty so the next switch requires confirmation', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    nativeApi.begin.mockRejectedValueOnce(new Error('合成网络失败'))
    page()
    await screen.findByText('课堂笔记')
    await userEvent.click(screen.getByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '保存失败仍在内存')
    await consentToStorage()
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))
    expect(await screen.findByText(/合成网络失败/)).toBeTruthy()

    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('当前编辑内容尚未保存'))
    expect((screen.getByLabelText('笔记标题') as HTMLInputElement).value).toBe('保存失败仍在内存')
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
    await userEvent.click(screen.getByRole('button', { name: '保存新版本' }))
    await waitFor(() => expect(nativeApi.begin).toHaveBeenCalledOnce())

    const body = nativeApi.begin.mock.calls[0]![0] as { versionId: string; noteId: string }
    await userEvent.click(screen.getByText('课堂笔记').closest('button')!)
    await screen.findByRole('heading', { name: '编辑固定材料' })
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
