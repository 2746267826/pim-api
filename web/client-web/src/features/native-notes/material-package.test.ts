// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { Zip, ZipDeflate, zipSync } from 'fflate'
import { buildMaterialBundle, sha256Hex } from './material-model'
import {
  assertAndroidNotePackageSize,
  exportAndroidNotePackage,
  importAndroidNotePackage,
  MAX_ANDROID_NOTE_PACKAGE_BYTES,
} from './material-package'

const noteId = '11111111-1111-4111-8111-111111111111'
const versionId = '22222222-2222-4222-8222-222222222222'
const markdownId = '33333333-3333-4333-8333-333333333333'
const photoBlockId = '44444444-4444-4444-8444-444444444444'
const assetId = '55555555-5555-4555-8555-555555555555'
const inkId = '66666666-6666-4666-8666-666666666666'

async function mixedArchive() {
  const bundle = await buildMaterialBundle({
    versionId,
    noteId,
    baseVersionId: null,
    revision: 2,
    title: '跨端混合笔记',
    noteCreatedAtMillis: 10,
    createdAtMillis: 20,
    blocks: [
      { id: markdownId, kind: 'markdown', text: '图像在中间' },
      {
        id: photoBlockId,
        kind: 'photo',
        assetId,
        mimeType: 'image/webp',
        bytes: new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 7]),
      },
      { id: inkId, kind: 'ink', ink: {
        width: 320,
        height: 240,
        strokes: [{ width: 2.5, points: [{ x: 12, y: 20, pressure: 0.4 }, { x: 90, y: 110, pressure: 0.9 }] }],
      }, bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 9]) },
      { id: '77777777-7777-4777-8777-777777777777', kind: 'markdown', text: '图像之后' },
    ],
  })
  return { bundle, archive: await exportAndroidNotePackage(bundle) }
}

function streamingZip(files: Array<{ path: string; bytes: Uint8Array }>) {
  const chunks: Uint8Array[] = []
  let failure: Error | null = null
  const zip = new Zip((error, chunk) => {
    if (error) failure = error
    else if (chunk) chunks.push(chunk)
  })
  for (const file of files) {
    const entry = new ZipDeflate(file.path, { level: 6 })
    zip.add(entry)
    entry.push(file.bytes, true)
  }
  zip.end()
  if (failure) throw failure
  const length = chunks.reduce((sum, chunk) => sum + chunk.length, 0)
  const result = new Uint8Array(length)
  let offset = 0
  for (const chunk of chunks) {
    result.set(chunk, offset)
    offset += chunk.length
  }
  return result
}

describe('Android schema-v1 note package', () => {
  it('round-trips ordered Markdown, WebP bytes, editable ink, and PNG preview', async () => {
    const { bundle, archive } = await mixedArchive()
    const imported = await importAndroidNotePackage(archive)

    expect(imported.title).toBe('跨端混合笔记')
    expect(imported.blocks.map((block) => block.kind)).toEqual(['markdown', 'photo', 'ink', 'markdown'])
    expect(imported.blocks[1]).toMatchObject({ kind: 'photo', assetId, mimeType: 'image/webp' })
    expect(imported.blocks[1]?.kind === 'photo' && imported.blocks[1].bytes).toEqual(bundle.files.find((file) => file.kind === 'Photo')?.bytes)
    expect(imported.blocks[2]).toMatchObject({
      kind: 'ink',
      ink: { width: 320, height: 240, strokes: [{ width: 2.5, points: [{ x: 12, y: 20, pressure: 0.4 }, { x: 90, y: 110, pressure: 0.9 }] }] },
    })
    expect(imported.blocks[2]?.kind === 'ink' && imported.blocks[2].bytes).toEqual(bundle.files.find((file) => file.kind === 'InkPreview')?.bytes)
  })

  it('reads streaming DEFLATE entries with data descriptors like Android ZipOutputStream', async () => {
    const { bundle } = await mixedArchive()
    const archive = streamingZip(bundle.files)
    const flags = new DataView(archive.buffer, archive.byteOffset, archive.byteLength).getUint16(6, true)
    expect(flags & 8).toBe(8)
    const imported = await importAndroidNotePackage(archive)
    expect(imported.blocks.map((block) => block.kind)).toEqual(['markdown', 'photo', 'ink', 'markdown'])
  })

  it('imports Kotlin-style float JSON without rewriting its content-addressed ink path', async () => {
    const noteId = '88888888-8888-4888-8888-888888888888'
    const blockId = '99999999-9999-4999-8999-999999999999'
    const inkJson = '{"width":320.0,"height":240.0,"strokes":[{"points":[{"x":12.0,"y":20.0,"pressure":0.4}],"width":2.5}]}'
    const inkSha = await sha256Hex(new TextEncoder().encode(inkJson))
    const path = `assets/${blockId}-${inkSha}.png`
    const noteJson = `{"schemaVersion":1,"id":"${noteId}","revision":0,"title":"Kotlin floats","createdAtMillis":1,"blocks":[{"id":"${blockId}","kind":"ink","ink":${inkJson}}]}`
    const document = `![手写卡片](${path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->`
    const archive = zipSync({
      'note.json': new TextEncoder().encode(noteJson),
      'document.md': new TextEncoder().encode(document),
      [path]: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 4]),
    }, { level: 0, mtime: new Date('1980-01-01T00:00:00Z') })

    const imported = await importAndroidNotePackage(archive)
    expect(imported.blocks[0]).toMatchObject({ kind: 'ink', ink: { width: 320, height: 240 } })
  })

  it('rejects ZIP entry paths that can escape the package root', async () => {
    const archive = zipSync({ '../note.json': new TextEncoder().encode('{}') })
    await expect(importAndroidNotePackage(archive)).rejects.toThrow('路径无效')
  })

  it('rejects binary corruption before exposing imported blocks', async () => {
    const { archive } = await mixedArchive()
    const corrupted = archive.slice()
    const view = new DataView(corrupted.buffer, corrupted.byteOffset, corrupted.byteLength)
    const nameLength = view.getUint16(26, true)
    const extraLength = view.getUint16(28, true)
    corrupted[30 + nameLength + extraLength] ^= 0xff
    await expect(importAndroidNotePackage(corrupted)).rejects.toThrow('CRC 不匹配')
  })

  it('rejects an ink preview whose content-addressed path does not match its vector', async () => {
    const { bundle } = await mixedArchive()
    const files = Object.fromEntries(bundle.files.map((file) => [file.path, file.bytes])) as Record<string, Uint8Array>
    const note = JSON.parse(new TextDecoder().decode(files['note.json'])) as {
      blocks: Array<{ kind: string; ink?: { strokes: Array<{ points: Array<{ x: number }> }> } }>
    }
    const ink = note.blocks.find((block) => block.kind === 'ink')!
    ink.ink!.strokes[0]!.points[0]!.x += 1
    files['note.json'] = new TextEncoder().encode(JSON.stringify(note))
    const archive = zipSync(files, { level: 0, mtime: new Date('1980-01-01T00:00:00Z') })
    await expect(importAndroidNotePackage(archive)).rejects.toThrow('SHA-256 匹配的 PNG 预览')
  })

  it('rejects Windows packages rather than guessing their distinct schema', async () => {
    const archive = zipSync({
      'note.md': new TextEncoder().encode('Windows package'),
      'version.json': new TextEncoder().encode('{}'),
      'bundle-manifest.json': new TextEncoder().encode('{}'),
    })
    await expect(importAndroidNotePackage(archive)).rejects.toThrow('不属于 Android schema-v1')
  })

  it('enforces a finite archive-size budget before allocating file bytes', () => {
    expect(() => assertAndroidNotePackageSize(22)).not.toThrow()
    expect(() => assertAndroidNotePackageSize(MAX_ANDROID_NOTE_PACKAGE_BYTES)).not.toThrow()
    expect(() => assertAndroidNotePackageSize(MAX_ANDROID_NOTE_PACKAGE_BYTES + 1)).toThrow('160 MiB')
    expect(() => assertAndroidNotePackageSize(Number.POSITIVE_INFINITY)).toThrow('160 MiB')
  })
})
