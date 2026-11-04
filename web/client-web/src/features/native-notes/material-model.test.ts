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

  it('publishes editable schema-v1 ink vectors with a bounded PNG preview file', async () => {
    const ink = {
      width: 320,
      height: 240,
      strokes: [{
        width: 2.5,
        points: [{ x: 12, y: 18, pressure: 0.4 }, { x: 50, y: 60, pressure: 0.9 }],
      }],
    }
    const previewBytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1])
    const inkId = '66666666-6666-4666-8666-666666666666'
    const bundle = await buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '手写草图',
      noteCreatedAtMillis: 100,
      createdAtMillis: 200,
      blocks: [
        { id: '44444444-4444-4444-8444-444444444444', kind: 'markdown', text: '图示' },
        { id: inkId, kind: 'ink', ink, bytes: previewBytes },
      ],
    })
    const input = JSON.parse(bundle.inputJson)
    const manifest = JSON.parse(bundle.manifestJson)
    const inkHash = await sha256Hex(encoder.encode(JSON.stringify(ink)))
    const previewHash = await sha256Hex(previewBytes)

    expect(input.note.blocks[1]).toEqual({ id: inkId, kind: 'ink', ink })
    expect(manifest.files).toContainEqual(expect.objectContaining({
      path: `assets/${inkId}-${inkHash}.png`,
      kind: 'InkPreview',
      blockId: inkId,
      sha256: previewHash,
      sizeBytes: previewBytes.length,
    }))
    expect(bundle.files.find((file) => file.kind === 'InkPreview')?.bytes).toEqual(previewBytes)
  })

  it('rejects ink coordinates outside the shared native schema', async () => {
    await expect(buildMaterialBundle({
      versionId,
      noteId,
      baseVersionId: null,
      revision: 0,
      title: '无效笔迹',
      noteCreatedAtMillis: 1,
      createdAtMillis: 2,
      blocks: [{ id: '44444444-4444-4444-8444-444444444444', kind: 'ink',
        ink: { width: 320, height: 240, strokes: [{ points: [{ x: 321, y: 2 }] }] }, bytes: new Uint8Array([1]) }],
    })).rejects.toThrow('手写卡片坐标或压力超出 schema-v1 范围')
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

  it('round-trips unknown block fields and preserves mixed recording and attachment descriptors on append', async () => {
    const photoBytes = new Uint8Array([9, 8, 7, 6])
    const photoHash = await sha256Hex(photoBytes)
    const recordingId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    const recording = {
      id: recordingId,
      noteId,
      schemaVersion: 1,
      currentSegment: 1,
      phase: 'Completed',
      createdAtMillis: 15,
      chunks: [{ segment: 1, index: 1, samples: 1, vendorChunk: { keep: true } }],
      vendorRecording: { keep: true },
    }
    const markdown = {
      id: '44444444-4444-4444-8444-444444444444',
      kind: 'markdown',
      text: '原文',
      vendorBlock: { keep: true },
    }
    const photo = {
      id: '55555555-5555-4555-8555-555555555555',
      kind: 'photo',
      assetId,
      mimeType: 'image/png',
      sha256: photoHash,
      vendorBlock: { keep: true },
    }
    const ink = { width: 120, height: 160, strokes: [] }
    const inkId = '66666666-6666-4666-8666-666666666666'
    const inkHash = await sha256Hex(encoder.encode(JSON.stringify(ink)))
    const inkBytes = new Uint8Array([5, 4, 3, 2, 1])
    const inkPreviewHash = await sha256Hex(inkBytes)
    const inkBlock = { id: inkId, kind: 'ink', ink, vendorBlock: { keep: true } }
    const note = {
      schemaVersion: 1,
      id: noteId,
      revision: 4,
      title: '原始混合笔记',
      createdAtMillis: 10,
      blocks: [markdown, photo, inkBlock],
      vendorNote: { keep: true },
    }
    const inputJson = JSON.stringify({
      schemaVersion: 1,
      createdAtMillis: 20,
      id: versionId,
      note,
      recordings: [recording],
      vendorEnvelope: { keep: true },
    })
    const rendered = [
      '原文',
      `![照片](assets/${assetId}.png)`,
      `![手写卡片](assets/${inkId}-${inkHash}.png)\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->`,
    ].join('\n\n')
    const noteBytes = encoder.encode(JSON.stringify(note))
    const recordingBytes = encoder.encode(JSON.stringify(recording))
    const sourceFiles = [
      { path: 'note.json', kind: 'Document', sha256: await sha256Hex(noteBytes), sizeBytes: noteBytes.length },
      { path: 'document.md', kind: 'Markdown', sha256: await sha256Hex(encoder.encode(rendered)), sizeBytes: encoder.encode(rendered).length },
      { path: `assets/${assetId}.png`, kind: 'Photo', sha256: photoHash, sizeBytes: photoBytes.length, blockId: photo.id },
      { path: `assets/${inkId}-${inkHash}.png`, kind: 'InkPreview', sha256: inkPreviewHash, sizeBytes: inkBytes.length, blockId: inkId },
      { path: `recordings/${recordingId}/manifest.json`, kind: 'RecordingManifest', sha256: await sha256Hex(recordingBytes), sizeBytes: recordingBytes.length, recordingId },
      {
        path: `recordings/${recordingId}/s0001-c000001.wav`,
        kind: 'AudioChunk',
        sha256: 'a'.repeat(64),
        sizeBytes: 46,
        recordingId,
        segment: 1,
        chunkIndex: 1,
        samples: 1,
      },
    ]
    const manifestJson = JSON.stringify({
      schemaVersion: 1,
      versionId,
      noteId,
      inputSha256: await sha256Hex(encoder.encode(inputJson)),
      revision: 4,
      files: sourceFiles,
    })
    const material: NoteMaterialVersion = {
      inputJson,
      manifestJson,
      upload: {
        ...asMaterialVersion(await buildMaterialBundle({
          versionId, noteId, baseVersionId: null, revision: 0, title: 'fixture',
          noteCreatedAtMillis: 0, createdAtMillis: 0,
          blocks: [{ id: '77777777-7777-4777-8777-777777777777', kind: 'markdown', text: 'fixture' }],
        })).upload,
        baseVersionId: versionId,
        files: sourceFiles.map((file, index) => ({
          index,
          ...file,
          partCount: 1,
          uploadedParts: [0],
        })),
      },
    }

    const parsed = parseMaterialVersion(material)
    expect(parsed.blocks[0]).toMatchObject({ source: { vendorBlock: { keep: true } } })
    const blocks = await hydrateParsedPhotos(parsed, material, async (_sourceVersionId, index) => {
      const path = sourceFiles[index]?.path
      if (path === `assets/${assetId}.png`) return photoBytes
      if (path === `assets/${inkId}-${inkHash}.png`) return inkBytes
      throw new Error(`unexpected block attachment ${path}`)
    })
    const next = await buildMaterialBundle({
      versionId: '88888888-8888-4888-8888-888888888888',
      noteId,
      baseVersionId: versionId,
      revision: 5,
      title: '续写后的笔记',
      noteCreatedAtMillis: 10,
      createdAtMillis: 30,
      blocks: [...blocks, { id: '99999999-9999-4999-8999-999999999999', kind: 'markdown', text: '新增内容' }],
      sourceInputJson: inputJson,
      preservedRecordings: parsed.recordingEntries,
      preservedFiles: parsed.manifestFiles,
      preserveSourceFiles: true,
    })
    const nextInput = JSON.parse(next.inputJson)
    const nextManifest = JSON.parse(next.manifestJson)
    expect(nextInput.vendorEnvelope).toEqual({ keep: true })
    expect(nextInput.note.vendorNote).toEqual({ keep: true })
    expect(nextInput.recordings).toEqual([recording])
    expect(next.recordedAudio).toBe(1)
    expect(nextInput.note.blocks.slice(0, 3)).toEqual([markdown, photo, inkBlock])
    expect(nextManifest.files.slice(2)).toEqual(sourceFiles.slice(2))
    expect(next.files.slice(2).map(({ path, kind, sha256, sizeBytes, blockId, recordingId: owner, segment, chunkIndex, samples }) => ({
      path, kind, sha256, sizeBytes, blockId, recordingId: owner, segment, chunkIndex, samples,
    }))).toEqual(sourceFiles.slice(2))
    expect(next.files.slice(2).every((file) => file.preserved)).toBe(true)
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
