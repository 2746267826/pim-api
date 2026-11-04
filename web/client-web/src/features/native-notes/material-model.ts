import type { components } from '@/api/generated'

export type NoteMaterialVersion = components['schemas']['NoteMaterialVersionDto']
export type NoteMaterialUpload = components['schemas']['NoteMaterialUploadDto']
export type NoteMaterialUploadFile = components['schemas']['NoteMaterialUploadFileDto']

export type SupportedPhotoMime = 'image/jpeg' | 'image/png' | 'image/webp'

export interface NewPhotoInput {
  id: string
  assetId: string
  mimeType: SupportedPhotoMime
  bytes: Uint8Array
}

export interface ExistingPhotoInput {
  id: string
  assetId: string
  mimeType: SupportedPhotoMime
  sha256: string
  fileIndex: number
  bytes: Uint8Array
}

export interface ExistingInkInput {
  id: string
  ink: Record<string, unknown>
  sha256: string
  fileIndex: number
  bytes: Uint8Array
}

export interface NewInkInput {
  id: string
  ink: Record<string, unknown>
  bytes: Uint8Array
}

export type MaterialDraftBlock =
  | { kind: 'markdown'; id: string; text: string; source?: Record<string, unknown> }
  | ({ kind: 'photo' } & (NewPhotoInput | ExistingPhotoInput))
  | ({ kind: 'ink' } & (ExistingInkInput | NewInkInput))

export interface ParsedPhotoBlock {
  id: string
  kind: 'photo'
  assetId: string
  mimeType: SupportedPhotoMime
  sha256: string
  path: string
  fileIndex: number
  source: Record<string, unknown>
}

export interface ParsedInkBlock {
  id: string
  kind: 'ink'
  ink: Record<string, unknown>
  path: string
  fileIndex: number
  previewSha256: string
  source: Record<string, unknown>
}

export type ParsedMaterialBlock =
  | { id: string; kind: 'markdown'; text: string; source: Record<string, unknown> }
  | ParsedPhotoBlock
  | ParsedInkBlock

export interface ParsedNoteMaterial {
  versionId: string
  noteId: string
  title: string
  revision: number
  createdAtMillis: number
  blocks: ParsedMaterialBlock[]
  recordings: number
  recordingEntries: unknown[]
  manifestFiles: PreservedMaterialFile[]
  sourceInputJson: string
}

export interface MaterialFileSpec {
  path: string
  kind: string
  bytes: Uint8Array
  sha256: string
  sizeBytes: number
  blockId?: string
  recordingId?: string
  segment?: number
  chunkIndex?: number
  samples?: number
  preserved?: boolean
}

export interface PreservedMaterialFile {
  path: string
  kind: string
  sha256: string
  sizeBytes: number
  blockId?: string
  recordingId?: string
  segment?: number
  chunkIndex?: number
  samples?: number
}

export interface MaterialBundle {
  versionId: string
  noteId: string
  baseVersionId: string | null
  revision: number
  inputJson: string
  manifestJson: string
  files: MaterialFileSpec[]
  recordedAudio: number
}

export interface BuildMaterialInput {
  versionId: string
  noteId: string
  baseVersionId: string | null
  revision: number
  title: string
  noteCreatedAtMillis: number
  createdAtMillis: number
  blocks: MaterialDraftBlock[]
  recordings?: unknown[]
  preservedRecordings?: unknown[]
  preservedFiles?: PreservedMaterialFile[]
  preserveSourceFiles?: boolean
  sourceInputJson?: string
}

const encoder = new TextEncoder()
const photoExtensions: Record<SupportedPhotoMime, string> = {
  'image/jpeg': 'jpg',
  'image/png': 'png',
  'image/webp': 'webp',
}

export function createMaterialId(): string {
  const value = globalThis.crypto
  if (typeof value?.randomUUID === 'function') return value.randomUUID()
  const bytes = value.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 15) | 64
  bytes[8] = (bytes[8]! & 63) | 128
  const hex = [...bytes].map((part) => part.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const copy = new Uint8Array(bytes.length)
  copy.set(bytes)
  const digest = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', copy.buffer))
  return [...digest].map((part) => part.toString(16).padStart(2, '0')).join('')
}

export async function buildMaterialBundle(input: BuildMaterialInput): Promise<MaterialBundle> {
  const title = input.title.trim()
  if (!title) throw new Error('标题不能为空')
  if (title.length > 500) throw new Error('标题不能超过 500 个字符')
  if (!input.blocks.length) throw new Error('笔记至少需要一个正文块')

  const noteBlocks: Record<string, unknown>[] = []
  const rendered: string[] = []
  const files: MaterialFileSpec[] = []
  const blockIds = new Set<string>()

  for (const block of input.blocks) {
    if (!block.id || blockIds.has(block.id)) throw new Error('正文块标识重复，请刷新后重试')
    blockIds.add(block.id)
    if (block.kind === 'markdown') {
      noteBlocks.push({ ...(block.source ?? {}), id: block.id, kind: 'markdown', text: block.text })
      rendered.push(block.text)
      continue
    }
    if (block.kind === 'photo') {
      const extension = photoExtensions[block.mimeType]
      if (!extension) throw new Error('Web 目前只支持 JPEG/PNG/WebP 照片')
      if (!block.bytes.length) throw new Error('照片内容为空')
      if (block.bytes.length > 32 * 1024 * 1024) throw new Error('单张照片不能超过 32 MiB')
      const hash = await sha256Hex(block.bytes)
      if (block.kind === 'photo' && 'sha256' in block && block.sha256 !== hash) {
        throw new Error('已保存照片的摘要发生变化，未创建新版本')
      }
      const path = `assets/${block.assetId}.${extension}`
      noteBlocks.push({ ...(block.source ?? {}),
        id: block.id,
        kind: 'photo',
        assetId: block.assetId,
        mimeType: block.mimeType,
        sha256: hash,
      })
      rendered.push(`![照片](${path})`)
      files.push({
        path,
        kind: 'Photo',
        bytes: block.bytes,
        sha256: hash,
        sizeBytes: block.bytes.length,
        blockId: block.id,
      })
      continue
    }

    validateInkDocument(block.ink)
    const inkJson = JSON.stringify(block.ink)
    const hash = await sha256Hex(encoder.encode(inkJson))
    if ('sha256' in block && hash !== block.sha256) throw new Error('已保存手写卡片的摘要发生变化，未创建新版本')
    if (block.bytes.length > 40 * 1024 * 1024) throw new Error('单张手写预览不能超过 40 MiB')
    const path = `assets/${block.id}-${hash}.png`
    noteBlocks.push({ ...(block.source ?? {}), id: block.id, kind: 'ink', ink: block.ink })
    rendered.push(`![手写卡片](${path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->`)
    files.push({
      path,
      kind: 'InkPreview',
      bytes: block.bytes,
      sha256: await sha256Hex(block.bytes),
      sizeBytes: block.bytes.length,
      blockId: block.id,
    })
  }

  const sourceInput = input.sourceInputJson ? JSON.parse(input.sourceInputJson) as {
    [key: string]: unknown
    note?: Record<string, unknown>
  } : undefined
  const sourceNote = sourceInput?.note
  const note = {
    ...(sourceNote ?? {}),
    schemaVersion: 1,
    id: input.noteId,
    revision: input.revision,
    title,
    createdAtMillis: input.noteCreatedAtMillis,
    blocks: noteBlocks,
  }
  const inputDocument = {
    ...(sourceInput ?? {}),
    schemaVersion: 1,
    createdAtMillis: input.createdAtMillis,
    id: input.versionId,
    note,
    recordings: input.preservedRecordings ?? input.recordings ?? [],
  }
  const inputJson = JSON.stringify(inputDocument)
  const noteJson = JSON.stringify(note)
  const documentMarkdown = rendered.join('\n\n')

  const documentBytes = encoder.encode(documentMarkdown)
  const noteBytes = encoder.encode(noteJson)
  const metadataFiles: MaterialFileSpec[] = [
    {
      path: 'note.json',
      kind: 'Document',
      bytes: noteBytes,
      sha256: await sha256Hex(noteBytes),
      sizeBytes: noteBytes.length,
    },
    {
      path: 'document.md',
      kind: 'Markdown',
      bytes: documentBytes,
      sha256: await sha256Hex(documentBytes),
      sizeBytes: documentBytes.length,
    },
  ]
  const sourceByPath = new Map((input.preservedFiles ?? []).map((file) => [file.path, file]))
  const generatedByPath = new Map([...metadataFiles, ...files].map((file) => [file.path, file]))
  const preservedFiles = input.preservedFiles ?? []
  const orderedFiles: MaterialFileSpec[] = []
  for (const preserved of preservedFiles) {
    const generated = generatedByPath.get(preserved.path)
    if (generated) {
      if (generated.kind === preserved.kind && generated.sha256 === preserved.sha256 &&
          generated.sizeBytes === preserved.sizeBytes) {
        const source = sourceByPath.get(preserved.path) ?? preserved
        orderedFiles.push({ ...generated, ...source, bytes: generated.bytes, preserved: true })
      } else if (input.preserveSourceFiles && (preserved.kind === 'Photo' || preserved.kind === 'InkPreview')) {
        throw new Error(`续写附件与原版本不匹配：${preserved.path}`)
      } else {
        orderedFiles.push(generated)
      }
      generatedByPath.delete(preserved.path)
    } else {
      orderedFiles.push({ ...preserved, bytes: new Uint8Array(), preserved: true })
    }
  }
  orderedFiles.push(...generatedByPath.values())
  files.splice(0, files.length, ...orderedFiles)

  const manifest = {
    schemaVersion: 1,
    versionId: input.versionId,
    noteId: input.noteId,
    inputSha256: await sha256Hex(encoder.encode(inputJson)),
    revision: input.revision,
    files: files.map((file) => ({
      path: file.path,
      kind: file.kind,
      sha256: file.sha256,
      sizeBytes: file.sizeBytes,
      blockId: file.blockId,
      recordingId: file.recordingId,
      segment: file.segment,
      chunkIndex: file.chunkIndex,
      samples: file.samples,
    })),
  }

  return {
    versionId: input.versionId,
    noteId: input.noteId,
    baseVersionId: input.baseVersionId,
    revision: input.revision,
    inputJson,
    manifestJson: JSON.stringify(manifest),
    files,
    recordedAudio: input.recordings?.length ?? 0,
  }
}

function validateInkDocument(ink: Record<string, unknown>) {
  const width = ink.width
  const height = ink.height
  const strokes = ink.strokes
  if (typeof width !== 'number' || !Number.isFinite(width) || width < 80 || width > 2048 ||
      typeof height !== 'number' || !Number.isFinite(height) || height < 80 || height > 4096 ||
      !Array.isArray(strokes) || strokes.length > 10_000) {
    throw new Error('手写卡片尺寸或笔画结构超出 schema-v1 范围')
  }
  let pointCount = 0
  for (const stroke of strokes) {
    if (!stroke || typeof stroke !== 'object' || Array.isArray(stroke)) {
      throw new Error('手写卡片包含无效笔画')
    }
    const strokeRecord = stroke as Record<string, unknown>
    const strokeWidth = strokeRecord.width === undefined ? 2.5 : strokeRecord.width
    const points = strokeRecord.points
    if (typeof strokeWidth !== 'number' || !Number.isFinite(strokeWidth) || strokeWidth < 0.5 || strokeWidth > 24 ||
        !Array.isArray(points) || points.length === 0) {
      throw new Error('手写卡片包含无效笔画')
    }
    pointCount += points.length
    if (pointCount > 50_000) throw new Error('手写卡片超过 Web 笔迹点数上限')
    for (const point of points) {
      if (!point || typeof point !== 'object' || Array.isArray(point)) {
        throw new Error('手写卡片包含无效坐标点')
      }
      const value = point as Record<string, unknown>
      const x = value.x
      const y = value.y
      const pressure = value.pressure === undefined ? 1 : value.pressure
      if (typeof x !== 'number' || !Number.isFinite(x) || x < 0 || x > width ||
          typeof y !== 'number' || !Number.isFinite(y) || y < 0 || y > height ||
          typeof pressure !== 'number' || !Number.isFinite(pressure) || pressure < 0 || pressure > 1) {
        throw new Error('手写卡片坐标或压力超出 schema-v1 范围')
      }
    }
  }
}

export function parseMaterialVersion(material: NoteMaterialVersion): ParsedNoteMaterial {
  const input = JSON.parse(material.inputJson) as {
    id?: string
    createdAtMillis?: number
    note?: {
      id?: string
      title?: string
      revision?: number
      createdAtMillis?: number
      blocks?: Array<Record<string, unknown>>
    }
    recordings?: unknown[]
  }
  const manifest = JSON.parse(material.manifestJson) as {
    versionId?: string
    noteId?: string
    revision?: number
    files?: Array<{ path?: string; kind?: string; sha256?: string; blockId?: string }>
  }
  if (!input.note || input.note.id !== material.upload.noteId || input.id !== material.upload.versionId) {
    throw new Error('固定材料来源与版本标识不一致')
  }
  const revision = Number(input.note.revision ?? 0)
  if (manifest.versionId !== material.upload.versionId ||
      manifest.noteId !== material.upload.noteId ||
      Number(manifest.revision) !== revision) {
    throw new Error('固定材料清单与原笔记身份或修订号不一致')
  }
  const files = manifest.files ?? []
  const fileIndex = (path: string, expectedKind: string) => {
    const index = files.findIndex((file) => file.path === path && file.kind === expectedKind)
    if (index < 0) throw new Error(`固定材料缺少 ${path}`)
    return index
  }

  const blocks = (input.note.blocks ?? []).map((raw): ParsedMaterialBlock => {
    const id = String(raw.id ?? '')
    if (!id) throw new Error('固定材料包含无效正文块')
    const kind = String(raw.kind ?? '')
    if (kind === 'markdown') return { id, kind, text: String(raw.text ?? ''), source: { ...raw } }
    if (kind === 'photo') {
      const assetId = String(raw.assetId ?? '')
      const mimeType = String(raw.mimeType ?? '') as SupportedPhotoMime
      const extension = photoExtensions[mimeType]
      if (!assetId || !extension) throw new Error('固定材料包含不受支持的照片')
      const path = `assets/${assetId}.${extension}`
      return {
        id,
        kind,
        assetId,
        mimeType,
        sha256: String(raw.sha256 ?? ''),
        path,
        fileIndex: fileIndex(path, 'Photo'),
        source: { ...raw },
      }
    }
    if (kind === 'ink') {
      const ink = raw.ink
      if (!ink || typeof ink !== 'object' || Array.isArray(ink)) throw new Error('固定材料包含无效手写卡片')
      const index = files.findIndex((file) => file.kind === 'InkPreview' && file.blockId === id)
      const file = files[index]
      if (index < 0 || !file?.path) throw new Error('固定材料缺少手写预览图')
      return {
        id,
        kind,
        ink: ink as Record<string, unknown>,
        path: file.path,
        fileIndex: index,
        previewSha256: String(file.sha256 ?? ''),
        source: { ...raw },
      }
    }
    throw new Error(`Web 暂不能编辑未知正文块：${kind || 'unknown'}`)
  })

  return {
    versionId: material.upload.versionId,
    noteId: material.upload.noteId,
    title: String(input.note.title ?? ''),
    revision,
    createdAtMillis: Number(input.note.createdAtMillis ?? 0),
    blocks,
    recordings: input.recordings?.length ?? 0,
    recordingEntries: input.recordings ?? [],
    manifestFiles: files.map((file) => ({ ...file })),
    sourceInputJson: material.inputJson,
    preservedRecordings: input.recordings ?? [],
    preservedFiles: files,
  }
}

export async function hydrateParsedPhotos(
  parsed: ParsedNoteMaterial,
  material: NoteMaterialVersion,
  readFile: (versionId: string, fileIndex: number) => Promise<Uint8Array>,
): Promise<MaterialDraftBlock[]> {
  const result: MaterialDraftBlock[] = []
  for (const block of parsed.blocks) {
    if (block.kind === 'markdown') {
      result.push(block)
      continue
    }
    const bytes = await readFile(material.upload.versionId, block.fileIndex)
    if (block.kind === 'photo') {
      if (await sha256Hex(bytes) !== block.sha256) {
        throw new Error('已保存照片的原字节摘要不匹配，未进入编辑')
      }
      result.push({ ...block, kind: 'photo', bytes })
      continue
    }
    const actual = await sha256Hex(encoder.encode(JSON.stringify(block.ink)))
    const expectedPath = `assets/${block.id}-${actual}.png`
    if (block.path !== expectedPath) throw new Error('手写卡片原始笔迹与预览图路径不一致，未进入编辑')
    if (await sha256Hex(bytes) !== block.previewSha256) throw new Error('手写预览图原字节摘要不匹配，未进入编辑')
    result.push({ ...block, kind: 'ink', sha256: actual, bytes })
  }
  return result
}
