/// <reference types="node" />
// @vitest-environment jsdom
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { setApiBase } from '@/lib/apiBase'
import { NativeNotesPage } from './native-notes-page'
import {
  buildMaterialBundle,
  hydrateParsedPhotos,
  parseMaterialVersion,
  sha256Hex,
  type NoteMaterialUpload,
  type NoteMaterialVersion,
} from './material-model'
import { extractJsonValue } from './raw-json'

const nativeApi = vi.hoisted(() => ({
  limits: vi.fn(), notes: vi.fn(), current: vi.fn(), versions: vi.fn(), version: vi.fn(),
  begin: vi.fn(), clone: vi.fn(), uploadPart: vi.fn(), complete: vi.fn(), promote: vi.fn(),
  fileBytes: vi.fn(), downloadBundle: vi.fn(),
}))
const quickApi = vi.hoisted(() => ({ list: vi.fn(), get: vi.fn() }))

vi.mock('./api', () => ({ bindNativeNotesApi: () => nativeApi }))
vi.mock('@/features/quick-notes/api', () => ({ quickNotesApi: quickApi }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', username: 'synthetic', displayName: '合成用户', role: 'user' } }),
}))

const fixtureDirectory = path.resolve(process.cwd(), '../../tests/Pim.UnitTests/Fixtures/notes/mixed-material-v1')
const expectedInputSha256 = '67d62efc9eda1ce06283c8417f424a5d7a4e70a43370bdd574793d1be246930b'
const expectedManifestSha256 = '31389e894159dc249501cbb7aa65605e6efda58f05482406b7e593cda7adb5d8'

interface GoldenInput {
  id: string
  note: { id: string; title: string; revision: number; createdAtMillis: number; blocks: Array<Record<string, unknown>> }
  recordings: unknown[]
}

interface GoldenManifestFile {
  path: string
  kind: string
  sha256: string
  sizeBytes: number
  blockId: string | null
  recordingId: string | null
  segment: number | null
  chunkIndex: number | null
  samples: number | null
}

interface GoldenManifest {
  versionId: string
  noteId: string
  revision: number
  inputSha256: string
  files: GoldenManifestFile[]
}

interface GoldenFixture {
  material: NoteMaterialVersion
  input: GoldenInput
  manifest: GoldenManifest
  files: GoldenManifestFile[]
  bytesByPath: Map<string, Uint8Array>
}

async function readAndroidGolden(): Promise<GoldenFixture> {
  const inputBytes = new Uint8Array(readFileSync(path.join(fixtureDirectory, 'input.json')))
  const manifestBytes = new Uint8Array(readFileSync(path.join(fixtureDirectory, 'manifest.json')))
  const inputJson = new TextDecoder().decode(inputBytes)
  const manifestJson = new TextDecoder().decode(manifestBytes)
  const input = JSON.parse(inputJson) as GoldenInput
  const manifest = JSON.parse(manifestJson) as GoldenManifest
  const bytesByPath = new Map<string, Uint8Array>()

  expect(inputBytes.byteLength).toBe(1196)
  expect(manifestBytes.byteLength).toBe(2182)
  expect(await sha256Hex(inputBytes)).toBe(expectedInputSha256)
  expect(await sha256Hex(manifestBytes)).toBe(expectedManifestSha256)
  expect(manifest.inputSha256).toBe(expectedInputSha256)
  expect(manifest.files).toHaveLength(7)
  for (const file of manifest.files) {
    const bytes = new Uint8Array(readFileSync(path.join(fixtureDirectory, file.path)))
    bytesByPath.set(file.path, bytes)
    expect(bytes.byteLength).toBe(file.sizeBytes)
    expect(await sha256Hex(bytes)).toBe(file.sha256)
  }

  const limits = {
    partSizeBytes: 1024, maxMetadataCharacters: 8_388_608, maxVersionBytes: 64 * 1024 * 1024 * 1024,
    maxUserReservedBytes: 64 * 1024 * 1024 * 1024, maxFiles: 100_000, maxPendingVersions: 10,
    maxVersionsPerUser: 1000, uploadExpiryHours: 1,
  }
  const upload: NoteMaterialUpload = {
    versionId: manifest.versionId, noteId: manifest.noteId, baseVersionId: null,
    currentVersionId: manifest.versionId, phase: 'Uploaded', createdAt: '2026-11-01T10:00:00Z',
    expiresAt: '2026-11-01T11:00:00Z', isCurrent: true, hasConflict: false, limits,
    files: manifest.files.map((file, index) => ({
      index, path: file.path, kind: file.kind, sha256: file.sha256, sizeBytes: file.sizeBytes,
      partCount: Math.ceil(file.sizeBytes / limits.partSizeBytes),
      uploadedParts: Array.from({ length: Math.ceil(file.sizeBytes / limits.partSizeBytes) }, (_, part) => part),
    })),
  }
  return { material: { inputJson, manifestJson, upload }, input, manifest, files: manifest.files, bytesByPath }
}

function renderPage(client: QueryClient) {
  const router = createMemoryRouter([{
    path: '/native-notes',
    element: <QueryClientProvider client={client}><NativeNotesPage /></QueryClientProvider>,
  }], { initialEntries: ['/native-notes'] })
  return render(<RouterProvider router={router} />)
}

describe('Android golden consumer canonical native-note reading', () => {
  let fixture: GoldenFixture
  let cloneReceipt: NoteMaterialUpload | undefined

  beforeEach(async () => {
    vi.resetAllMocks()
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: vi.fn(() => 'blob:golden-preview') })
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() })
    fixture = await readAndroidGolden()
    cloneReceipt = undefined
    const { input, material, files, bytesByPath } = fixture
    const noteId = input.note.id
    const versionId = input.id
    nativeApi.limits.mockResolvedValue(material.upload.limits)
    nativeApi.notes.mockResolvedValue({ total: 1, offset: 0, items: [{ noteId, title: input.note.title, currentVersionId: versionId, hasConflict: false }] })
    nativeApi.current.mockResolvedValue(material)
    nativeApi.versions.mockResolvedValue([material.upload])
    nativeApi.version.mockResolvedValue(material)
    nativeApi.fileBytes.mockImplementation(async (_sourceVersionId: string, index: number) => {
      const file = files[index]
      if (!file || (file.kind !== 'Photo' && file.kind !== 'InkPreview')) throw new Error(`unexpected canonical material read ${file?.path ?? index}`)
      return bytesByPath.get(file.path)
    })
    nativeApi.clone.mockImplementation(async (sourceVersionId: string, request: {
      material: { versionId: string; noteId: string; baseVersionId: string | null; inputJson: string; manifestJson: string }
    }) => {
      const nextInput = JSON.parse(request.material.inputJson) as { id: string; note: { id: string } }
      const nextManifest = JSON.parse(request.material.manifestJson) as { files: GoldenManifestFile[] }
      const partSize = material.upload.limits.partSizeBytes
      cloneReceipt = {
        ...material.upload, versionId: nextInput.id, noteId: nextInput.note.id,
        baseVersionId: request.material.baseVersionId, currentVersionId: sourceVersionId,
        phase: 'Uploading', isCurrent: false,
        files: nextManifest.files.map((file, index) => {
          const partCount = Math.ceil(file.sizeBytes / partSize)
          return {
            index, path: file.path, kind: file.kind, sha256: file.sha256, sizeBytes: file.sizeBytes, partCount,
            uploadedParts: file.kind === 'Document' ? [] : Array.from({ length: partCount }, (_, part) => part),
          }
        }),
      }
      return cloneReceipt
    })
    nativeApi.uploadPart.mockImplementation(async (_versionId: string, fileIndex: number, partIndex: number) => {
      const file = cloneReceipt?.files[fileIndex]
      if (!file) throw new Error(`unexpected upload file ${fileIndex}`)
      file.uploadedParts = [...new Set([...file.uploadedParts, partIndex])]
      return 'ok'
    })
    nativeApi.complete.mockImplementation(async (completedVersionId: string) => {
      if (!cloneReceipt || cloneReceipt.versionId !== completedVersionId || cloneReceipt.files.some((file) => file.uploadedParts.length !== file.partCount)) {
        throw new Error('golden consumer complete received incomplete clone receipt')
      }
      return { ...cloneReceipt, phase: 'Uploaded', isCurrent: true, currentVersionId: completedVersionId }
    })
    quickApi.list.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 })
  })

  afterEach(() => {
    cleanup()
    setApiBase('')
    vi.restoreAllMocks()
  })

  it('reads Android golden current/version and preserves block order, attachment hashes, and recordings through clone', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } })
    renderPage(client)
    const noteId = fixture.input.note.id
    const versionId = fixture.input.id
    await userEvent.click((await screen.findByText(fixture.input.note.title)).closest('button')!)
    expect(nativeApi.current).toHaveBeenCalledWith(noteId, expect.any(AbortSignal))
    expect(await screen.findByRole('heading', { name: '继续写这篇笔记' })).toBeTruthy()
    expect(nativeApi.version).toHaveBeenCalledWith(versionId, expect.any(AbortSignal))
    await waitFor(() => expect(nativeApi.fileBytes).toHaveBeenCalledTimes(2))
    expect(nativeApi.fileBytes).toHaveBeenNthCalledWith(1, versionId, 2, expect.any(AbortSignal))
    expect(nativeApi.fileBytes).toHaveBeenNthCalledWith(2, versionId, 3, expect.any(AbortSignal))
    expect(nativeApi.downloadBundle).not.toHaveBeenCalled()
    expect(quickApi.get).not.toHaveBeenCalled()

    await userEvent.clear(screen.getByLabelText('笔记标题'))
    await userEvent.type(screen.getByLabelText('笔记标题'), `${fixture.input.note.title}续写`)
    await userEvent.click(screen.getByLabelText('确认保存到当前服务器'))
    await userEvent.click(screen.getAllByRole('button', { name: '保存笔记' })[0]!)
    await waitFor(() => expect(nativeApi.complete).toHaveBeenCalledOnce())
    expect(nativeApi.clone).toHaveBeenCalledOnce()
    expect(nativeApi.uploadPart).toHaveBeenCalled()
    expect(await screen.findByText(/新版本已完整保存/)).toBeTruthy()

    const [sourceVersionId, request] = nativeApi.clone.mock.calls[0] as [string, {
      expectedCurrentVersionId: string
      material: { baseVersionId: string; inputJson: string; manifestJson: string }
    }]
    expect(sourceVersionId).toBe(versionId)
    expect(request.expectedCurrentVersionId).toBe(versionId)
    expect(request.material.baseVersionId).toBe(versionId)
    const nextInput = JSON.parse(request.material.inputJson) as GoldenInput
    expect(nextInput.recordings).toEqual(fixture.input.recordings)
    expect(nextInput.note.blocks.map((block) => block.id)).toEqual(fixture.input.note.blocks.map((block) => block.id))
    const expectedPhoto = fixture.input.note.blocks.find((block) => block.kind === 'photo')!
    expect(nextInput.note.blocks.find((block) => block.kind === 'photo')).toMatchObject({
      id: expectedPhoto.id, assetId: expectedPhoto.assetId, sha256: expectedPhoto.sha256,
    })

    const nextManifest = JSON.parse(request.material.manifestJson) as { files: GoldenManifestFile[] }
    const nextInkBlock = nextInput.note.blocks.find((block) => block.kind === 'ink') as {
      id: string
      ink: Record<string, unknown>
    }
    const nextInkIndex = nextInput.note.blocks.findIndex((block) => block.kind === 'ink')
    const nextInkRaw = extractJsonValue(request.material.inputJson, ['note', 'blocks', nextInkIndex, 'ink'])
    const nextInkHash = await sha256Hex(new TextEncoder().encode(nextInkRaw))
    const nextInkPath = `assets/${nextInkBlock.id}-${nextInkHash}.png`
    const oldInkPreview = fixture.manifest.files.find((file) => file.kind === 'InkPreview')!
    expect(nextManifest.files).not.toContainEqual(expect.objectContaining({ path: oldInkPreview.path }))
    expect(nextManifest.files).toContainEqual(expect.objectContaining({
      path: nextInkPath,
      kind: 'InkPreview',
      blockId: nextInkBlock.id,
      sha256: oldInkPreview.sha256,
      sizeBytes: oldInkPreview.sizeBytes,
    }))
    for (const expected of fixture.manifest.files.filter((file) =>
      file.kind !== 'Document' && file.kind !== 'Markdown' && file.kind !== 'InkPreview')) {
      expect(nextManifest.files).toContainEqual(expect.objectContaining({
        path: expected.path, kind: expected.kind, sha256: expected.sha256, sizeBytes: expected.sizeBytes,
        ...(expected.blockId ? { blockId: expected.blockId } : {}),
        ...(expected.recordingId ? { recordingId: expected.recordingId } : {}),
        ...(expected.segment !== null ? { segment: expected.segment } : {}),
        ...(expected.chunkIndex !== null ? { chunkIndex: expected.chunkIndex } : {}),
        ...(expected.samples !== null ? { samples: expected.samples } : {}),
      }))
    }
  })

  it('builds and exports a deterministic Web continuation from the Android golden', async () => {
    const parsed = parseMaterialVersion(fixture.material)
    const hydratedBlocks = await hydrateParsedPhotos(parsed, fixture.material, async (_versionId, index) => {
      const file = fixture.files[index]!
      return fixture.bytesByPath.get(file.path)!
    })
    const markdown = hydratedBlocks.find((block) => block.kind === 'markdown')!
    const next = await buildMaterialBundle({
      versionId: '99999999-9999-4999-8999-999999999999',
      noteId: fixture.input.note.id,
      baseVersionId: fixture.input.id,
      revision: 8,
      title: `${fixture.input.note.title}续写`,
      noteCreatedAtMillis: fixture.input.note.createdAtMillis,
      createdAtMillis: 1735689603000,
      blocks: hydratedBlocks.map((block) => block === markdown && block.kind === 'markdown'
        ? { ...block, text: `${block.text}\nWeb continuation` }
        : block),
      sourceInputJson: fixture.material.inputJson,
      preservedRecordings: parsed.recordingEntries,
      preservedFiles: parsed.manifestFiles,
      preserveSourceFiles: true,
    })

    expect(next.revision).toBe(8)
    expect(next.baseVersionId).toBe(fixture.input.id)
    const nextInput = JSON.parse(next.inputJson) as GoldenInput
    expect(nextInput.recordings).toEqual(fixture.input.recordings)
    expect(next.inputJson).toContain('320.0')
    expect(next.inputJson).toContain('12.0')
    const nextInkBlock = nextInput.note.blocks.find((block) => block.kind === 'ink') as {
      id: string
      ink: {
        width: number
        height: number
        strokeIds: string[]
        strokeStyles: Array<Record<string, unknown>>
        strokes: Array<Record<string, unknown>>
      }
    }
    expect(nextInkBlock.ink).toMatchObject({
      width: 320,
      height: 240,
      strokeStyles: [{ color: '#263b85', width: 2.5 }],
      strokes: [{
        color: '#263b85',
        width: 2.5,
        points: [
          { x: 12, y: 18, pressure: 1 },
          { x: 96, y: 84, pressure: 1 },
          { x: 180, y: 30, pressure: 1 },
        ],
      }],
    })
    expect(nextInkBlock.ink.strokeIds).toEqual([expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )])
    const nextInkIndex = nextInput.note.blocks.findIndex((block) => block.kind === 'ink')
    const nextInkRaw = extractJsonValue(next.inputJson, ['note', 'blocks', nextInkIndex, 'ink'])
    const normalizedInkHash = await sha256Hex(new TextEncoder().encode(nextInkRaw))
    const normalizedInkPath = `assets/${nextInkBlock.id}-${normalizedInkHash}.png`
    const oldInkPreview = fixture.files.find((file) => file.kind === 'InkPreview')!

    const nextManifest = JSON.parse(next.manifestJson) as GoldenManifest
    expect(nextManifest.files).toHaveLength(7)
    expect(new Set(next.files.map((file) => file.path))).toEqual(new Set([
      ...fixture.files.map((file) => file.path).filter((filePath) => filePath !== oldInkPreview.path),
      normalizedInkPath,
    ]))
    for (const file of fixture.files.filter((entry) => entry.kind === 'Photo')) {
      const nextFile = next.files.find((entry) => entry.path === file.path)!
      expect(nextFile.sha256).toBe(file.sha256)
      expect(await sha256Hex(nextFile.bytes)).toBe(file.sha256)
    }
    const normalizedPreview = next.files.find((file) => file.path === normalizedInkPath)!
    expect(normalizedPreview.sha256).toBe(oldInkPreview.sha256)
    expect(normalizedPreview.sizeBytes).toBe(oldInkPreview.sizeBytes)
    expect(await sha256Hex(normalizedPreview.bytes)).toBe(oldInkPreview.sha256)
    for (const file of fixture.files.filter((entry) =>
      entry.kind === 'RecordingManifest' || entry.kind === 'AudioChunk')) {
      expect(next.files).toContainEqual(expect.objectContaining({
        path: file.path,
        kind: file.kind,
        sha256: file.sha256,
        sizeBytes: file.sizeBytes,
      }))
    }

    const exportDirectory = path.resolve(process.cwd(), 'scratch/interop/web-continuation-v1')
    mkdirSync(exportDirectory, { recursive: true })
    writeFileSync(path.join(exportDirectory, 'input.json'), next.inputJson)
    writeFileSync(path.join(exportDirectory, 'manifest.json'), next.manifestJson)
    for (const file of next.files) {
      const bytes = file.bytes.length ? file.bytes : fixture.bytesByPath.get(file.path)!
      const outputPath = path.join(exportDirectory, file.path)
      mkdirSync(path.dirname(outputPath), { recursive: true })
      writeFileSync(outputPath, bytes)
    }
    writeFileSync(path.join(exportDirectory, 'request.json'), JSON.stringify({
      sourceVersionId: fixture.input.id,
      expectedCurrentVersionId: fixture.input.id,
      material: {
        versionId: next.versionId,
        noteId: next.noteId,
        baseVersionId: next.baseVersionId,
        inputJson: next.inputJson,
        manifestJson: next.manifestJson,
        storageConsent: true,
      },
    }))

    expect(JSON.parse(readFileSync(path.join(exportDirectory, 'note.json'), 'utf8'))).toEqual(JSON.parse(next.inputJson).note)
    expect(readFileSync(path.join(exportDirectory, 'document.md'), 'utf8')).toContain('Web continuation')
  })
})
