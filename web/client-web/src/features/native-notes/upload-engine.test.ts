// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { buildMaterialBundle, sha256Hex, type NoteMaterialUpload } from './material-model'
import { uploadMaterialBundle, type NoteMaterialUploadApi } from './upload-engine'

const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'

function remoteFrom(bundle: Awaited<ReturnType<typeof buildMaterialBundle>>, partSize: number, uploaded: number[] = []) {
  return {
    versionId: bundle.versionId,
    noteId: bundle.noteId,
    baseVersionId: bundle.baseVersionId,
    currentVersionId: null,
    phase: 'Uploading',
    createdAt: '2026-11-01T10:00:00Z',
    expiresAt: '2026-11-01T11:00:00Z',
    isCurrent: false,
    hasConflict: false,
    limits: {
      partSizeBytes: partSize,
      maxMetadataCharacters: 8_388_608,
      maxVersionBytes: 64 * 1024 * 1024 * 1024,
      maxUserReservedBytes: 64 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      maxPendingVersions: 10,
      maxVersionsPerUser: 1000,
      uploadExpiryHours: 1,
    },
    files: bundle.files.map((file, index) => ({
      index,
      path: file.path,
      kind: file.kind,
      sha256: file.sha256,
      sizeBytes: file.sizeBytes,
      partCount: Math.ceil(file.sizeBytes / partSize),
      uploadedParts: index === 0 ? uploaded : [],
    })),
  } satisfies NoteMaterialUpload
}

describe('native note upload engine', () => {
  it('resumes from server-recorded parts and posts exact binary hashes before completion', async () => {
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '分块恢复',
      noteCreatedAtMillis: 1,
      createdAtMillis: 2,
      blocks: [{ id: '33333333-3333-4333-8333-333333333333', kind: 'markdown', text: '正文'.repeat(20) }],
    })
    const partSize = 8
    const uploading = remoteFrom(bundle, partSize, [0])
    const completed = { ...uploading, phase: 'Uploaded', isCurrent: true }
    const api = {
      begin: vi.fn(async () => uploading),
      uploadPart: vi.fn(async (
        _versionId: string,
        _fileIndex: number,
        _partIndex: number,
        _body: Blob,
        _sha256: string,
      ) => 'ok'),
      complete: vi.fn(async () => completed),
    } satisfies NoteMaterialUploadApi
    const progress: number[] = []

    const result = await uploadMaterialBundle(bundle, api, (value) => {
      progress.push(value.completedParts)
    })

    expect(result.phase).toBe('Uploaded')
    expect(api.begin).toHaveBeenCalledOnce()
    expect(api.complete).toHaveBeenCalledWith(versionId)
    expect(api.uploadPart).not.toHaveBeenCalledWith(versionId, 0, 0, expect.anything(), expect.anything())
    expect(api.uploadPart).toHaveBeenCalled()
    const first = api.uploadPart.mock.calls[0]!
    const bytes = new Uint8Array(await (first[3] as Blob).arrayBuffer())
    expect(first[4]).toBe(await sha256Hex(bytes))
    expect(progress.at(-1)).toBe(bundle.files.reduce((sum, file) => sum + Math.ceil(file.sizeBytes / partSize), 0))
  })

  it('fails closed before upload if Begin returns a different fixed file', async () => {
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '摘要不一致',
      noteCreatedAtMillis: 1,
      createdAtMillis: 2,
      blocks: [{ id: '33333333-3333-4333-8333-333333333333', kind: 'markdown', text: '正文' }],
    })
    const changed = remoteFrom(bundle, 8)
    changed.files[0]!.sha256 = '0'.repeat(64)
    const api = {
      begin: vi.fn(async () => changed),
      uploadPart: vi.fn(async () => 'ok'),
      complete: vi.fn(async () => changed),
    } satisfies NoteMaterialUploadApi

    await expect(uploadMaterialBundle(bundle, api)).rejects.toThrow('服务器材料清单与本地固定文件不一致')
    expect(api.uploadPart).not.toHaveBeenCalled()
    expect(api.complete).not.toHaveBeenCalled()
  })

  it('fails closed when Begin adds a file or changes the fixed part count', async () => {
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '清单数量核对',
      noteCreatedAtMillis: 1,
      createdAtMillis: 2,
      blocks: [{ id: '33333333-3333-4333-8333-333333333333', kind: 'markdown', text: '正文' }],
    })
    const withExtraFile = remoteFrom(bundle, 8)
    withExtraFile.files.push({
      ...withExtraFile.files[0]!,
      index: withExtraFile.files.length,
      path: 'assets/unexpected.png',
    })
    const extraApi = {
      begin: vi.fn(async () => withExtraFile),
      uploadPart: vi.fn(async () => 'ok'),
      complete: vi.fn(async () => withExtraFile),
    } satisfies NoteMaterialUploadApi
    await expect(uploadMaterialBundle(bundle, extraApi)).rejects.toThrow('服务器返回的材料文件数量与本地固定清单不一致')
    expect(extraApi.uploadPart).not.toHaveBeenCalled()

    const changedParts = remoteFrom(bundle, 8)
    changedParts.files[0]!.partCount += 1
    const partsApi = {
      begin: vi.fn(async () => changedParts),
      uploadPart: vi.fn(async () => 'ok'),
      complete: vi.fn(async () => changedParts),
    } satisfies NoteMaterialUploadApi
    await expect(uploadMaterialBundle(bundle, partsApi)).rejects.toThrow('服务器返回的分块数与本地固定文件不一致')
    expect(partsApi.uploadPart).not.toHaveBeenCalled()
  })
})
