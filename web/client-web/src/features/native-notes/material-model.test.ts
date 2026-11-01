// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  buildMaterialBundle,
  hydrateParsedPhotos,
  parseMaterialVersion,
  sha256Hex,
  type MaterialDraftBlock,
  type NoteMaterialVersion,
} from './material-model'

const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'
const assetId = '33333333-3333-4333-8333-333333333333'
const encoder = new TextEncoder()

function asMaterialVersion(bundle: Awaited<ReturnType<typeof buildMaterialBundle>>): NoteMaterialVersion {
  return {
    inputJson: bundle.inputJson,
    manifestJson: bundle.manifestJson,
    upload: {
      versionId: bundle.versionId,
      noteId: bundle.noteId,
      baseVersionId: bundle.baseVersionId,
      currentVersionId: bundle.noteId,
      phase: 'Uploaded',
      createdAt: '2026-11-01T10:00:00Z',
      expiresAt: '2026-11-01T11:00:00Z',
      isCurrent: true,
      hasConflict: false,
      limits: {
        partSizeBytes: 1024,
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
        partCount: 1,
        uploadedParts: [0],
      })),
    },
  }
}

describe('native note material model', () => {
  it('builds a backend-valid fixed version for markdown and photo blocks', async () => {
    const photo = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4])
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '  课堂笔记  ',
      noteCreatedAtMillis: 100,
      createdAtMillis: 200,
      blocks: [
        { id: '44444444-4444-4444-8444-444444444444', kind: 'markdown', text: '# 第一节' },
        {
          id: '55555555-5555-4555-8555-555555555555',
          kind: 'photo',
          assetId,
          mimeType: 'image/png',
          bytes: photo,
        },
      ],
    })

    const input = JSON.parse(bundle.inputJson)
    const manifest = JSON.parse(bundle.manifestJson)
    expect(input.note).toMatchObject({
      schemaVersion: 1,
      id: noteId,
      revision: 0,
      title: '课堂笔记',
      blocks: [
        { kind: 'markdown', text: '# 第一节' },
        { kind: 'photo', assetId, mimeType: 'image/png' },
      ],
    })
    expect(input.note.blocks[1].sha256).toBe(await sha256Hex(photo))
    expect(manifest.inputSha256).toBe(await sha256Hex(encoder.encode(bundle.inputJson)))
    expect(manifest.files).toContainEqual(expect.objectContaining({
      path: `assets/${assetId}.png`,
      kind: 'Photo',
      sha256: await sha256Hex(photo),
      sizeBytes: photo.length,
    }))
    const markdownFile = bundle.files.find((file) => file.kind === 'Markdown')
    expect(new TextDecoder().decode(markdownFile?.bytes)).toBe(`# 第一节\n\n![照片](assets/${assetId}.png)`)
  })

  it('parses the exact fixed note.json hash and remote file indexes', async () => {
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 3,
      title: '来源核对',
      noteCreatedAtMillis: 10,
      createdAtMillis: 20,
      blocks: [{ id: '44444444-4444-4444-8444-444444444444', kind: 'markdown', text: '正文' }],
    })
    const parsed = parseMaterialVersion(asMaterialVersion(bundle))
    expect(parsed).toMatchObject({
      versionId,
      noteId,
      title: '来源核对',
      revision: 3,
      recordings: 0,
      blocks: [{ kind: 'markdown', text: '正文' }],
    })
  })

  it('fails closed when the fixed manifest identifies a different note version', async () => {
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 3,
      title: '来源核对',
      noteCreatedAtMillis: 10,
      createdAtMillis: 20,
      blocks: [{ id: '44444444-4444-4444-8444-444444444444', kind: 'markdown', text: '正文' }],
    })
    const material = asMaterialVersion(bundle)
    const manifest = JSON.parse(material.manifestJson) as { versionId: string }
    manifest.versionId = '99999999-9999-4999-8999-999999999999'
    material.manifestJson = JSON.stringify(manifest)

    expect(() => parseMaterialVersion(material)).toThrow('固定材料清单与原笔记身份或修订号不一致')
  })

  it('preserves an existing vector ink block and its preview bytes when creating a new version', async () => {
    const ink = { width: 120, height: 160, strokes: [] }
    const inkJson = JSON.stringify(ink)
    const inkHash = await sha256Hex(encoder.encode(inkJson))
    const blockId = '66666666-6666-4666-8666-666666666666'
    const preview = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 9, 8, 7])
    const previewHash = await sha256Hex(preview)
    const note = {
      schemaVersion: 1,
      id: noteId,
      revision: 2,
      title: '手写保持',
      createdAtMillis: 10,
      blocks: [{ id: blockId, kind: 'ink', ink }],
    }
    const inputJson = JSON.stringify({
      schemaVersion: 1,
      createdAtMillis: 20,
      id: versionId,
      note,
      recordings: [],
    })
    const noteBytes = encoder.encode(JSON.stringify(note))
    const document = `![手写卡片](assets/${blockId}-${inkHash}.png)\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->`
    const manifestJson = JSON.stringify({
      schemaVersion: 1,
      versionId,
      noteId,
      inputSha256: await sha256Hex(encoder.encode(inputJson)),
      revision: 2,
      files: [
        { path: 'note.json', kind: 'Document', sha256: await sha256Hex(noteBytes), sizeBytes: noteBytes.length },
        { path: 'document.md', kind: 'Markdown', sha256: await sha256Hex(encoder.encode(document)), sizeBytes: encoder.encode(document).length },
        { path: `assets/${blockId}-${inkHash}.png`, kind: 'InkPreview', sha256: previewHash, sizeBytes: preview.length, blockId },
      ],
    })
    const material: NoteMaterialVersion = {
      inputJson,
      manifestJson,
      upload: {
        ...asMaterialVersion(await buildMaterialBundle({
          versionId, noteId, baseVersionId: null, revision: 0, title: 'fixture',
          noteCreatedAtMillis: 0, createdAtMillis: 0,
          blocks: [{ id: '77777777-7777-4777-8777-777777777777', kind: 'markdown', text: '' }],
        })).upload,
        files: [
          { index: 0, path: 'note.json', kind: 'Document', sha256: 'a', sizeBytes: 1, partCount: 1, uploadedParts: [0] },
          { index: 1, path: 'document.md', kind: 'Markdown', sha256: 'b', sizeBytes: 1, partCount: 1, uploadedParts: [0] },
          { index: 2, path: `assets/${blockId}-${inkHash}.png`, kind: 'InkPreview', sha256: previewHash, sizeBytes: preview.length, partCount: 1, uploadedParts: [0] },
        ],
      },
    }

    const parsed = parseMaterialVersion(material)
    const blocks = await hydrateParsedPhotos(parsed, material, async () => preview) as MaterialDraftBlock[]
    expect(blocks).toContainEqual(expect.objectContaining({
      kind: 'ink',
      id: blockId,
      sha256: inkHash,
      bytes: preview,
    }))
    const next = await buildMaterialBundle({
      versionId: '88888888-8888-4888-8888-888888888888',
      noteId,
      baseVersionId: versionId,
      revision: 3,
      title: '手写保持',
      noteCreatedAtMillis: 10,
      createdAtMillis: 30,
      blocks,
    })
    expect(next.files).toContainEqual(expect.objectContaining({
      path: `assets/${blockId}-${inkHash}.png`,
      kind: 'InkPreview',
      sha256: previewHash,
    }))
    expect(JSON.parse(next.inputJson).note.blocks[0].ink).toEqual(ink)
  })
})
