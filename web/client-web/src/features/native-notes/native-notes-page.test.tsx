// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NativeNotesPage } from './native-notes-page'
import type { NoteMaterialUpload, NoteMaterialVersion } from './material-model'

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
vi.mock('./api', () => ({ nativeNotesApi: nativeApi }))
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
  return render(
    <MemoryRouter initialEntries={['/native-notes']}>
      <QueryClientProvider client={client}>
        <NativeNotesPage />
      </QueryClientProvider>
    </MemoryRouter>,
  )
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
})

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
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '尚未保存的第一份草稿')
    await userEvent.click(await screen.findByRole('button', { name: '纯文本副本' }))

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
    expect(nativeApi.complete).toHaveBeenCalledWith(body.versionId)
  })

  it('publishes a selected photo in the same frozen manifest as the Markdown body', async () => {
    page()
    await userEvent.click(await screen.findByRole('button', { name: /新建笔记/ }))
    await userEvent.type(screen.getByLabelText('笔记标题'), '混合材料笔记')
    await userEvent.clear(screen.getByLabelText('正文块 1'))
    await userEvent.type(screen.getByLabelText('正文块 1'), '照片与正文一起冻结')
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
})
