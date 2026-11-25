import type { components } from '@/api/generated'
import { deriveStrokeId, parseInkDocument, type InkDocument } from './ink-document'
import { extractJsonValue, replaceJsonObjectProperties, splitJsonArrayValues } from './raw-json'

export type NoteMaterialVersion = components['schemas']['NoteMaterialVersionDto']
export type NoteMaterialUpload = components['schemas']['NoteMaterialUploadDto']
export type NoteMaterialUploadFile = components['schemas']['NoteMaterialUploadFileDto']

export type SupportedPhotoMime = 'image/jpeg' | 'image/png' | 'image/webp'

export interface NewPhotoInput {
  id: string
  assetId: string
  mimeType: SupportedPhotoMime
  bytes: Uint8Array
  source?: Record<string, unknown>
}
export interface ExistingPhotoInput {
  id: string
  assetId: string
  mimeType: SupportedPhotoMime
  sha256: string
  fileIndex: number
  bytes: Uint8Array
  source?: Record<string, unknown>
}
export interface ExistingInkInput {
  id: string
  ink: Record<string, unknown>
  sha256: string
  fileIndex: number
  bytes: Uint8Array
  source?: Record<string, unknown>
}

export interface NewInkInput {
  id: string
  ink: Record<string, unknown>
  bytes: Uint8Array
  source?: Record<string, unknown>
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

  const sourceInput = input.sourceInputJson
  const sourceNoteJson = sourceInput ? extractJsonValue(sourceInput, ['note']) : undefined
  const sourceBlockJsonById = new Map<string, string>()
  if (sourceNoteJson) {
    for (const rawBlock of splitJsonArrayValues(extractJsonValue(sourceNoteJson, ['blocks']))) {
      const parsedBlock = JSON.parse(rawBlock) as { id?: string; kind?: string }
      if (parsedBlock.id && parsedBlock.kind) sourceBlockJsonById.set(parsedBlock.id, rawBlock)
    }
  }
  const noteBlocks: Record<string, unknown>[] = []
  const rawNoteBlocks: string[] = []
  const rendered: string[] = []
  const files: MaterialFileSpec[] = []
  const blockIds = new Set<string>()
  const supersededPaths = new Set<string>()

  for (const block of input.blocks) {
    if (!block.id || blockIds.has(block.id)) throw new Error('正文块标识重复，请刷新后重试')
    blockIds.add(block.id)
    if (block.kind === 'markdown') {
      const next = { ...(block.source ?? {}), id: block.id, kind: 'markdown', text: block.text }
      noteBlocks.push(next)
      rawNoteBlocks.push(replaceJsonObjectProperties(sourceBlockJsonById.get(block.id) ?? JSON.stringify(block.source ?? {}), { id: JSON.stringify(block.id), kind: '"markdown"', text: JSON.stringify(block.text) }))
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
      const next = { ...(block.source ?? {}),
        id: block.id,
        kind: 'photo',
        assetId: block.assetId,
        mimeType: block.mimeType,
        sha256: hash,
      }
      noteBlocks.push(next)
      rawNoteBlocks.push(replaceJsonObjectProperties(sourceBlockJsonById.get(block.id) ?? JSON.stringify(block.source ?? {}), { id: JSON.stringify(block.id), kind: '"photo"', assetId: JSON.stringify(block.assetId), mimeType: JSON.stringify(block.mimeType), sha256: JSON.stringify(hash) }))
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

    const normalizedInk = parseInkDocument(block.ink, (index) => deriveStrokeId(block.id, index))
    const sourceBlockJson = sourceBlockJsonById.get(block.id)
    const sourceBlockKind = sourceBlockJson ? (JSON.parse(sourceBlockJson) as { kind?: string }).kind : undefined
    const sourceInkJson = sourceBlockKind === 'ink' && sourceBlockJson ? extractJsonValue(sourceBlockJson, ['ink']) : undefined
    let sourceInk: Record<string, unknown> | undefined
    if (sourceInkJson) {
      sourceInk = JSON.parse(sourceInkJson) as Record<string, unknown>
      parseInkDocument(sourceInk, (index) => deriveStrokeId(block.id, index))
    }
    const sourceUnchanged = sourceInk !== undefined && sourceInkJson !== undefined &&
      jsonValuesEqual(sourceInk, block.ink)
    const inkJson = sourceUnchanged
      ? preserveSourceInkJson(sourceInkJson!, normalizedInk)
      : JSON.stringify(normalizedInk)
    if (sourceUnchanged) {
      parseInkDocument(JSON.parse(inkJson) as Record<string, unknown>, (index) => deriveStrokeId(block.id, index))
    }
    const hash = await sha256Hex(encoder.encode(inkJson))
    const sourceHash = sourceInkJson
      ? await sha256Hex(encoder.encode(sourceInkJson))
      : undefined
    if ('sha256' in block) {
      const currentHash = await sha256Hex(encoder.encode(JSON.stringify(block.ink)))
      if (block.sha256 !== sourceHash && block.sha256 !== currentHash) {
        throw new Error('已保存手写卡片的摘要发生变化，未创建新版本')
      }
    }
    if (block.bytes.length > 40 * 1024 * 1024) throw new Error('单张手写预览不能超过 40 MiB')
    const path = `assets/${block.id}-${hash}.png`
    const previousPreviewHash = sourceHash ?? ('sha256' in block ? block.sha256 : undefined)
    if (previousPreviewHash && previousPreviewHash !== hash) {
      supersededPaths.add(`assets/${block.id}-${previousPreviewHash}.png`)
    }
    noteBlocks.push({ ...(block.source ?? {}), id: block.id, kind: 'ink', ink: normalizedInk })
    rawNoteBlocks.push(replaceJsonObjectProperties(sourceBlockJson ?? JSON.stringify(block.source ?? {}), { id: JSON.stringify(block.id), kind: '"ink"', ink: inkJson }))
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

  const sourceNote = sourceNoteJson ? JSON.parse(sourceNoteJson) as Record<string, unknown> : undefined
  const note = {
    ...(sourceNote ?? {}),
    schemaVersion: 1,
    id: input.noteId,
    revision: input.revision,
    title,
    createdAtMillis: input.noteCreatedAtMillis,
    blocks: noteBlocks,
  }
  const noteJson = sourceNoteJson
    ? replaceJsonObjectProperties(sourceNoteJson, {
      schemaVersion: '1', id: JSON.stringify(input.noteId), revision: String(input.revision), title: JSON.stringify(title),
      createdAtMillis: String(input.noteCreatedAtMillis), blocks: `[${rawNoteBlocks.join(',')}]`,
    })
    : JSON.stringify(note)
  const recordings = input.preservedRecordings ?? input.recordings ?? []
  const sourceRecordingsJson = sourceInput ? extractJsonValue(sourceInput, ['recordings']) : undefined
  const recordingsJson = sourceRecordingsJson && JSON.stringify(JSON.parse(sourceRecordingsJson)) === JSON.stringify(recordings)
    ? sourceRecordingsJson : JSON.stringify(recordings)
  const inputJson = sourceInput
    ? replaceJsonObjectProperties(sourceInput, { schemaVersion: '1', createdAtMillis: String(input.createdAtMillis), id: JSON.stringify(input.versionId), note: noteJson, recordings: recordingsJson })
    : JSON.stringify({ schemaVersion: 1, createdAtMillis: input.createdAtMillis, id: input.versionId, note, recordings })
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
  const preservedPaths = new Set<string>()
  for (const preserved of preservedFiles) {
    if (preservedPaths.has(preserved.path)) throw new Error(`原版本包含重复材料路径：${preserved.path}`)
    preservedPaths.add(preserved.path)
    if (supersededPaths.has(preserved.path)) continue
    const generated = generatedByPath.get(preserved.path)
    if (generated) {
      if (generated.kind === preserved.kind && generated.sha256 === preserved.sha256 &&
          generated.sizeBytes === preserved.sizeBytes && nullishEqual(generated.blockId, preserved.blockId) &&
          nullishEqual(generated.recordingId, preserved.recordingId) && nullishEqual(generated.segment, preserved.segment) &&
          nullishEqual(generated.chunkIndex, preserved.chunkIndex) && nullishEqual(generated.samples, preserved.samples)) {
        const source = sourceByPath.get(preserved.path) ?? preserved
        orderedFiles.push({ ...generated, ...source, bytes: generated.bytes, preserved: true })
      } else if (input.preserveSourceFiles && (preserved.kind === 'Photo' || preserved.kind === 'InkPreview')) {
        throw new Error(`续写附件与原版本不匹配：${preserved.path}`)
      } else {
        orderedFiles.push(generated)
      }
      generatedByPath.delete(preserved.path)
    } else {
      if (input.preserveSourceFiles && (preserved.kind === 'Photo' || preserved.kind === 'InkPreview')) {
        throw new Error(`续写正文未引用原版本附件：${preserved.path}`)
      }
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
    recordedAudio: (input.preservedRecordings ?? input.recordings)?.length ?? 0,
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
    files?: Array<{
      path?: string
      kind?: string
      sha256?: string
      sizeBytes?: number
      blockId?: string | null
      recordingId?: string | null
      segment?: number | null
      chunkIndex?: number | null
      samples?: number | null
    }>
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
  const preservedFiles: PreservedMaterialFile[] = files.map((file) => {
    if (typeof file.path !== 'string' || !file.path ||
        typeof file.kind !== 'string' || !file.kind ||
        typeof file.sha256 !== 'string' || !file.sha256 ||
        typeof file.sizeBytes !== 'number' || !Number.isSafeInteger(file.sizeBytes) || file.sizeBytes < 0) {
      throw new Error('固定材料清单包含不完整文件描述')
    }
    return {
      path: file.path,
      kind: file.kind,
      sha256: file.sha256,
      sizeBytes: file.sizeBytes,
      ...(file.blockId != null ? { blockId: file.blockId } : {}),
      ...(file.recordingId != null ? { recordingId: file.recordingId } : {}),
      ...(file.segment != null ? { segment: file.segment } : {}),
      ...(file.chunkIndex != null ? { chunkIndex: file.chunkIndex } : {}),
      ...(file.samples != null ? { samples: file.samples } : {}),
    }
  })
  const fileIndex = (path: string, expectedKind: string) => {
    const index = preservedFiles.findIndex((file) => file.path === path && file.kind === expectedKind)
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
      parseInkDocument(ink as Record<string, unknown>, (index) => deriveStrokeId(id, index))
      const index = preservedFiles.findIndex((file) => file.kind === 'InkPreview' && file.blockId === id)
      const file = preservedFiles[index]
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
    manifestFiles: preservedFiles,
    sourceInputJson: material.inputJson,
  }
}

function nullishEqual(left: unknown, right: unknown): boolean {
  return (left ?? null) === (right ?? null)
}

function jsonValuesEqual(left: unknown, right: unknown): boolean {
  if (left === right) return true
  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left) && Array.isArray(right) &&
      left.length === right.length &&
      left.every((value, index) => jsonValuesEqual(value, right[index]))
  }
  if (typeof left !== 'object' || typeof right !== 'object' || left === null || right === null) return false
  const leftRecord = left as Record<string, unknown>
  const rightRecord = right as Record<string, unknown>
  const leftKeys = Object.keys(leftRecord)
  const rightKeys = Object.keys(rightRecord)
  return leftKeys.length === rightKeys.length &&
    leftKeys.every((key) => hasOwn(rightRecord, key) && jsonValuesEqual(leftRecord[key], rightRecord[key]))
}

function preserveSourceInkJson(sourceJson: string, normalized: InkDocument): string {
  const source = JSON.parse(sourceJson) as Record<string, unknown>
  const sourceStrokes = Array.isArray(source.strokes)
    ? source.strokes as Array<Record<string, unknown>>
    : []
  const rootReplacements: Record<string, string> = {}
  if (!hasOwn(source, 'strokeIds')) rootReplacements.strokeIds = JSON.stringify(normalized.strokeIds)
  if (!hasOwn(source, 'strokeStyles')) rootReplacements.strokeStyles = JSON.stringify(normalized.strokeStyles)
  let result = Object.keys(rootReplacements).length
    ? replaceJsonObjectProperties(sourceJson, rootReplacements)
    : sourceJson

  let strokesChanged = false
  const patchedStrokes = splitJsonArrayValues(extractJsonValue(result, ['strokes'])).map((strokeJson, index) => {
    const sourceStroke = sourceStrokes[index] ?? {}
    const normalizedStroke = normalized.strokes[index]!
    const strokeReplacements: Record<string, string> = {}
    if (!hasOwn(sourceStroke, 'color')) strokeReplacements.color = JSON.stringify(normalizedStroke.color)
    if (!hasOwn(sourceStroke, 'width')) strokeReplacements.width = JSON.stringify(normalizedStroke.width)
    let next = Object.keys(strokeReplacements).length
      ? replaceJsonObjectProperties(strokeJson, strokeReplacements)
      : strokeJson
    if (Object.keys(strokeReplacements).length) strokesChanged = true

    const sourcePoints = Array.isArray(sourceStroke.points)
      ? sourceStroke.points as Array<Record<string, unknown>>
      : []
    let pointsChanged = false
    const patchedPoints = splitJsonArrayValues(extractJsonValue(next, ['points'])).map((pointJson, pointIndex) => {
      if (hasOwn(sourcePoints[pointIndex] ?? {}, 'pressure')) return pointJson
      pointsChanged = true
      return replaceJsonObjectProperties(pointJson, { pressure: '1' })
    })
    if (pointsChanged) {
      next = replaceJsonObjectProperties(next, { points: `[${patchedPoints.join(',')}]` })
      strokesChanged = true
    }
    return next
  })
  if (strokesChanged) {
    result = replaceJsonObjectProperties(result, { strokes: `[${patchedStrokes.join(',')}]` })
  }
  return result
}

function hasOwn(value: object, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(value, key)
}

export async function hydrateParsedPhotos(
  parsed: ParsedNoteMaterial,
  material: NoteMaterialVersion,
  readFile: (versionId: string, fileIndex: number) => Promise<Uint8Array>,
): Promise<MaterialDraftBlock[]> {
  const rawBlocksById = new Map<string, string>()
  if (parsed.sourceInputJson) {
    for (const rawBlock of splitJsonArrayValues(extractJsonValue(parsed.sourceInputJson, ['note', 'blocks']))) {
      const value = JSON.parse(rawBlock) as { id?: string; kind?: string }
      if (value.id && value.kind) rawBlocksById.set(value.id, rawBlock)
    }
  }
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
    const rawInk = rawBlocksById.get(block.id)
    const rawInkKind = rawInk ? (JSON.parse(rawInk) as { kind?: string }).kind : undefined
    const rawInkJson = rawInkKind === 'ink' && rawInk ? extractJsonValue(rawInk, ['ink']) : undefined
    const actual = await sha256Hex(encoder.encode(rawInkJson ?? JSON.stringify(block.ink)))
    const expectedPath = `assets/${block.id}-${actual}.png`
    if (block.path !== expectedPath) throw new Error('手写卡片原始笔迹与预览图路径不一致，未进入编辑')
    if (await sha256Hex(bytes) !== block.previewSha256) throw new Error('手写预览图原字节摘要不匹配，未进入编辑')
    result.push({ ...block, kind: 'ink', sha256: actual, bytes })
  }
  return result
}
