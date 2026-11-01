import { Inflate, zipSync } from 'fflate'
import { sha256Hex, type MaterialBundle, type MaterialDraftBlock, type SupportedPhotoMime } from './material-model'

export interface ImportedAndroidNotePackage {
  title: string
  blocks: MaterialDraftBlock[]
}

export const MAX_ANDROID_NOTE_PACKAGE_BYTES = 160 * 1024 * 1024
const MAX_UNCOMPRESSED_BYTES = 128 * 1024 * 1024
const MAX_ENTRIES = 2_048
const MAX_METADATA_BYTES = 8 * 1024 * 1024
const MAX_DOCUMENT_BYTES = 16 * 1024 * 1024
const MAX_PHOTO_BYTES = 32 * 1024 * 1024
const MAX_INK_PREVIEW_BYTES = 40 * 1024 * 1024
const MAX_INFLATE_CHUNK_BYTES = 64 * 1024
const MAX_INFLATE_OVERRUN_BYTES = 64 * 1024
const MAX_DEFLATE_EXPANSION_PER_INPUT_BYTE = 2_048
const HASH_PATTERN = /^[0-9a-f]{64}$/i
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const textDecoder = new TextDecoder('utf-8', { fatal: true })
const textEncoder = new TextEncoder()

interface ZipEntryInfo {
  path: string
  size: number
  crc: number
  dataStart: number
  compressedSize: number
  method: number
  localStart: number
}

export async function exportAndroidNotePackage(bundle: MaterialBundle): Promise<Uint8Array> {
  if (bundle.recordedAudio > 0) throw new Error('含录音的固定版本不能由 Web 重建完整离线包')
  if (bundle.files.length < 2 || bundle.files.length > MAX_ENTRIES) throw new Error('笔记材料数量超出离线包上限')
  const files: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>
  let totalBytes = 0
  for (const file of bundle.files) {
    if (!isAndroidPackagePath(file.path) || files[file.path]) throw new Error('材料路径无效或重复，未导出')
    if (file.bytes.length !== file.sizeBytes || await sha256Hex(file.bytes) !== file.sha256) {
      throw new Error(`材料摘要或长度不匹配：${file.path}`)
    }
    const limit = file.path === 'note.json'
      ? MAX_METADATA_BYTES
      : file.path === 'document.md'
        ? MAX_DOCUMENT_BYTES
        : file.kind === 'InkPreview'
          ? MAX_INK_PREVIEW_BYTES
          : MAX_PHOTO_BYTES
    if (file.bytes.length === 0 || file.bytes.length > limit) throw new Error(`材料超出离线包单文件上限：${file.path}`)
    totalBytes += file.bytes.length
    if (totalBytes > MAX_UNCOMPRESSED_BYTES) throw new Error('笔记离线包超过 128 MiB 解压预算')
    files[file.path] = file.bytes
  }
  if (!files['note.json'] || !files['document.md']) throw new Error('离线包缺少 Android schema-v1 文档文件')
  const archive = zipSync(files, { level: 0, mtime: new Date('1980-01-01T00:00:00Z') })
  if (archive.length > MAX_ANDROID_NOTE_PACKAGE_BYTES) throw new Error('笔记离线包超过 160 MiB 下载预算')
  return archive
}

export function assertAndroidNotePackageSize(sizeBytes: number): void {
  if (!Number.isSafeInteger(sizeBytes) || sizeBytes < 22 || sizeBytes > MAX_ANDROID_NOTE_PACKAGE_BYTES) {
    throw new Error('ZIP 大小为空或超过 160 MiB 上限')
  }
}

export async function importAndroidNotePackage(archive: Uint8Array): Promise<ImportedAndroidNotePackage> {
  const entries = inspectZip(archive)
  const entryMap = new Map<string, Uint8Array>()
  let totalInflatedBytes = 0
  for (const entry of entries) {
    const bytes = inflateEntry(archive, entry)
    totalInflatedBytes += bytes.length
    if (totalInflatedBytes > MAX_UNCOMPRESSED_BYTES) throw new Error('ZIP 实际解压总量超过 128 MiB')
    if (bytes.length !== entry.size || crc32(bytes) !== entry.crc) {
      throw new Error(`笔记包文件损坏或 CRC 不匹配：${entry.path}`)
    }
    entryMap.set(entry.path, bytes)
  }

  const noteBytes = requireEntry(entryMap, 'note.json')
  const documentBytes = requireEntry(entryMap, 'document.md')
  if (noteBytes.length > MAX_METADATA_BYTES || documentBytes.length > MAX_DOCUMENT_BYTES) {
    throw new Error('笔记包元数据或正文超过读取上限')
  }
  const noteJsonText = textDecoder.decode(noteBytes)
  const note = parseJsonObject(noteBytes, 'note.json')
  if (note.schemaVersion !== 1 || !isUuid(note.id) || !isInteger(note.revision) || note.revision < 0 ||
      typeof note.title !== 'string' || !note.title.trim() || note.title.length > 500 ||
      !isInteger(note.createdAtMillis) || !Array.isArray(note.blocks) || note.blocks.length === 0 || note.blocks.length > 10_000) {
    throw new Error('Android note.json 不符合 schema-v1 笔记结构')
  }

  const blockIds = new Set<string>()
  const rawBlocks = splitJsonArrayValues(findObjectPropertyRaw(noteJsonText, 'blocks'))
  if (rawBlocks.length !== note.blocks.length) throw new Error('Android note.json 正文块结构不一致')
  const expectedPaths = new Set(['note.json', 'document.md'])
  const blocks: MaterialDraftBlock[] = []
  const rendered: string[] = []
  for (let blockIndex = 0; blockIndex < note.blocks.length; blockIndex++) {
    const raw = note.blocks[blockIndex]
    if (!isRecord(raw) || !isUuid(raw.id) || blockIds.has(raw.id)) throw new Error('笔记正文块身份为空、无效或重复')
    blockIds.add(raw.id)
    if (raw.kind === 'markdown') {
      if (typeof raw.text !== 'string') throw new Error('Markdown 正文块缺少 text')
      blocks.push({ id: raw.id, kind: 'markdown', text: raw.text })
      rendered.push(raw.text)
      continue
    }
    if (raw.kind === 'photo') {
      if (!isUuid(raw.assetId) || typeof raw.mimeType !== 'string' || !isPhotoMime(raw.mimeType) ||
          typeof raw.sha256 !== 'string' || !HASH_PATTERN.test(raw.sha256)) {
        throw new Error('照片块缺少有效 assetId、MIME 或 SHA-256')
      }
      const path = `assets/${raw.assetId}.${photoExtension(raw.mimeType)}`
      const bytes = requireEntry(entryMap, path)
      if (!bytes.length || bytes.length > MAX_PHOTO_BYTES || await sha256Hex(bytes) !== raw.sha256) {
        throw new Error(`照片原字节为空、超限或摘要不匹配：${path}`)
      }
      if (!hasImageSignature(bytes, raw.mimeType)) throw new Error(`照片文件签名与 MIME 不匹配：${path}`)
      expectedPaths.add(path)
      const photo = { id: raw.id, kind: 'photo' as const, assetId: raw.assetId, mimeType: raw.mimeType, bytes }
      blocks.push(photo)
      rendered.push(`![照片](${path})`)
      continue
    }
    if (raw.kind === 'ink') {
      const ink = raw.ink
      validateInk(ink)
      // Keep numeric lexemes: Kotlin Float serialization may emit 320.0 where JS emits 320.
      const rawInk = findObjectPropertyRaw(rawBlocks[blockIndex]!, 'ink')
      const inkVectorSha = await sha256Hex(textEncoder.encode(compactJson(rawInk)))
      const path = `assets/${raw.id}-${inkVectorSha}.png`
      const entry = entries.find((candidate) => candidate.path.toLowerCase() === path.toLowerCase())
      if (!entry) throw new Error('手写块缺少与原始向量 SHA-256 匹配的 PNG 预览')
      const bytes = requireEntry(entryMap, entry.path)
      if (!bytes.length || bytes.length > MAX_INK_PREVIEW_BYTES || !hasPngSignature(bytes)) {
        throw new Error(`手写预览为空、超限或不是 PNG：${entry.path}`)
      }
      expectedPaths.add(entry.path)
      const inkBlock = { id: raw.id, kind: 'ink' as const, ink, bytes }
      blocks.push(inkBlock)
      rendered.push(`![手写卡片](${entry.path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->`)
      continue
    }
    throw new Error(`不支持的正文块类型，拒绝丢弃材料：${String(raw.kind ?? 'unknown')}`)
  }

  if (expectedPaths.size !== entries.length || entries.some((entry) => !expectedPaths.has(entry.path))) {
    throw new Error('笔记包包含未引用文件或缺失正文材料')
  }
  const document = textDecoder.decode(documentBytes)
  if (document !== rendered.join('\n\n')) throw new Error('document.md 与 note.json 正文块顺序或内容不一致')
  return { title: note.title.trim(), blocks }
}

function inspectZip(bytes: Uint8Array): ZipEntryInfo[] {
  assertAndroidNotePackageSize(bytes.length)
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const minimum = Math.max(0, bytes.length - 65_557)
  let end = -1
  for (let offset = bytes.length - 22; offset >= minimum; offset--) {
    if (view.getUint32(offset, true) === 0x06054b50) {
      end = offset
      break
    }
  }
  if (end < 0) throw new Error('ZIP 结束目录缺失')
  const disk = view.getUint16(end + 4, true)
  const directoryDisk = view.getUint16(end + 6, true)
  const diskEntries = view.getUint16(end + 8, true)
  const entryCount = view.getUint16(end + 10, true)
  const directoryBytes = view.getUint32(end + 12, true)
  const directoryOffset = view.getUint32(end + 16, true)
  const commentLength = view.getUint16(end + 20, true)
  if (end + 22 + commentLength !== bytes.length || disk || directoryDisk || diskEntries !== entryCount ||
      entryCount === 0xffff || directoryBytes === 0xffffffff || directoryOffset === 0xffffffff || entryCount > MAX_ENTRIES ||
      directoryOffset + directoryBytes !== end) {
    throw new Error('不支持多卷、ZIP64、重复记录或异常目录偏移')
  }

  const directoryEnd = directoryOffset + directoryBytes
  const paths = new Set<string>()
  const entries: ZipEntryInfo[] = []
  const ranges: Array<[number, number]> = []
  let totalSize = 0
  let cursor = directoryOffset
  for (let index = 0; index < entryCount; index++) {
    if (cursor + 46 > directoryEnd || view.getUint32(cursor, true) !== 0x02014b50) throw new Error('ZIP 中央目录记录无效')
    const madeBy = view.getUint16(cursor + 4, true) >>> 8
    const flags = view.getUint16(cursor + 8, true)
    const method = view.getUint16(cursor + 10, true)
    const crc = view.getUint32(cursor + 16, true)
    const compressedSize = view.getUint32(cursor + 20, true)
    const size = view.getUint32(cursor + 24, true)
    const nameLength = view.getUint16(cursor + 28, true)
    const extraLength = view.getUint16(cursor + 30, true)
    const entryCommentLength = view.getUint16(cursor + 32, true)
    const diskStart = view.getUint16(cursor + 34, true)
    const externalAttributes = view.getUint32(cursor + 38, true)
    const localStart = view.getUint32(cursor + 42, true)
    const recordEnd = cursor + 46 + nameLength + extraLength + entryCommentLength
    if (!nameLength || recordEnd > directoryEnd || diskStart !== 0 || compressedSize === 0xffffffff || size === 0xffffffff ||
        localStart === 0xffffffff || flags & 1 || flags & ~0x080e || (method !== 0 && method !== 8) ||
        (method === 0 && compressedSize !== size) ||
        (madeBy === 3 && ((externalAttributes >>> 16) & 0xf000) === 0xa000)) {
      throw new Error('ZIP 包含加密、特殊文件或不支持的压缩记录')
    }
    const path = textDecoder.decode(bytes.subarray(cursor + 46, cursor + 46 + nameLength))
    if (!isSafeArchivePath(path) || !isSupportedAndroidPackagePath(path) || paths.has(path.toLowerCase())) {
      throw new Error(`ZIP 路径无效、重复或不属于 Android schema-v1：${path}`)
    }
    paths.add(path.toLowerCase())
    const limit = path === 'note.json'
      ? MAX_METADATA_BYTES
      : path === 'document.md'
        ? MAX_DOCUMENT_BYTES
        : isAndroidPhotoPath(path)
          ? MAX_PHOTO_BYTES
          : MAX_INK_PREVIEW_BYTES
    if (size > limit) throw new Error(`ZIP 文件项超过解压上限：${path}`)
    totalSize += size
    if (totalSize > MAX_UNCOMPRESSED_BYTES) throw new Error('ZIP 解压总量超过 128 MiB')

    if (localStart + 30 > directoryOffset || view.getUint32(localStart, true) !== 0x04034b50) {
      throw new Error(`ZIP 本地文件头无效：${path}`)
    }
    const localFlags = view.getUint16(localStart + 6, true)
    const localMethod = view.getUint16(localStart + 8, true)
    const localCrc = view.getUint32(localStart + 14, true)
    const localCompressedSize = view.getUint32(localStart + 18, true)
    const localSize = view.getUint32(localStart + 22, true)
    const localNameLength = view.getUint16(localStart + 26, true)
    const localExtraLength = view.getUint16(localStart + 28, true)
    const dataStart = localStart + 30 + localNameLength + localExtraLength
    const dataEnd = dataStart + compressedSize
    const localPath = textDecoder.decode(bytes.subarray(localStart + 30, localStart + 30 + localNameLength))
    if (dataStart > directoryOffset || localStart + 30 + localNameLength > directoryOffset ||
        localFlags !== flags || localMethod !== method || localPath !== path || dataEnd > directoryOffset ||
        (flags & 8
          ? (localCrc !== 0 && localCrc !== crc) || (localCompressedSize !== 0 && localCompressedSize !== compressedSize) ||
            (localSize !== 0 && localSize !== size)
          : localCrc !== crc || localCompressedSize !== compressedSize || localSize !== size)) {
      throw new Error(`ZIP 本地文件头与中央目录不一致：${path}`)
    }
    let dataRecordEnd = dataEnd
    if (flags & 8) {
      if (dataRecordEnd + 12 > directoryOffset) throw new Error(`ZIP 数据描述符缺失：${path}`)
      if (view.getUint32(dataRecordEnd, true) === 0x08074b50) dataRecordEnd += 4
      if (dataRecordEnd + 12 > directoryOffset || view.getUint32(dataRecordEnd, true) !== crc ||
          view.getUint32(dataRecordEnd + 4, true) !== compressedSize || view.getUint32(dataRecordEnd + 8, true) !== size) {
        throw new Error(`ZIP 数据描述符与目录不一致：${path}`)
      }
      dataRecordEnd += 12
    }
    entries.push({ path, size, crc, dataStart, compressedSize, method, localStart })
    ranges.push([localStart, dataRecordEnd])
    cursor = recordEnd
  }
  if (cursor !== directoryEnd) throw new Error('ZIP 中央目录长度不一致')
  ranges.sort((left, right) => left[0] - right[0])
  if (ranges.length && (ranges[0]![0] !== 0 || ranges[ranges.length - 1]![1] !== directoryOffset)) {
    throw new Error('ZIP 文件数据区域存在前缀或未验证间隙')
  }
  for (let index = 1; index < ranges.length; index++) {
    if (ranges[index]![0] !== ranges[index - 1]![1]) throw new Error('ZIP 文件数据区域重叠或存在间隙')
  }
  return entries
}

function inflateEntry(archive: Uint8Array, entry: ZipEntryInfo): Uint8Array {
  if (entry.method === 0) return archive.subarray(entry.dataStart, entry.dataStart + entry.compressedSize)

  const output = new Uint8Array(entry.size)
  let written = 0
  let finished = false
  const inflater = new Inflate((chunk, final) => {
    if (written + chunk.length > entry.size) {
      throw new Error(`ZIP 实际解压字节超过目录声明：${entry.path}`)
    }
    output.set(chunk, written)
    written += chunk.length
    finished = final
  })
  const compressedEnd = entry.dataStart + entry.compressedSize
  for (let offset = entry.dataStart; offset < compressedEnd;) {
    const remainingOutputBudget = entry.size - written + MAX_INFLATE_OVERRUN_BYTES
    const boundedInputBytes = Math.max(1, Math.floor(remainingOutputBudget / MAX_DEFLATE_EXPANSION_PER_INPUT_BYTE))
    const next = Math.min(offset + Math.min(MAX_INFLATE_CHUNK_BYTES, boundedInputBytes), compressedEnd)
    inflater.push(archive.subarray(offset, next), next === compressedEnd)
    offset = next
  }
  if (entry.compressedSize === 0) inflater.push(new Uint8Array(0), true)
  if (!finished || written !== entry.size) {
    throw new Error(`ZIP 实际解压长度与目录声明不一致：${entry.path}`)
  }
  return output
}

function parseJsonObject(bytes: Uint8Array, label: string): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(textDecoder.decode(bytes))
    if (!isRecord(value)) throw new Error()
    return value
  } catch {
    throw new Error(`${label} 不是有效的 UTF-8 JSON 对象`)
  }
}

function findObjectPropertyRaw(source: string, property: string): string {
  let cursor = skipJsonWhitespace(source, 0)
  if (source[cursor] !== '{') throw new Error('Android note.json 对象结构无效')
  cursor = skipJsonWhitespace(source, cursor + 1)
  const seen = new Set<string>()
  let found: string | undefined
  while (source[cursor] !== '}') {
    if (source[cursor] !== '"') throw new Error('Android note.json 属性名无效')
    const keyEnd = jsonStringEnd(source, cursor)
    let key: unknown
    try {
      key = JSON.parse(source.slice(cursor, keyEnd))
    } catch {
      throw new Error('Android note.json 属性名不是有效 JSON')
    }
    if (typeof key !== 'string' || seen.has(key)) throw new Error('Android note.json 包含重复或无效属性')
    seen.add(key)
    cursor = skipJsonWhitespace(source, keyEnd)
    if (source[cursor] !== ':') throw new Error('Android note.json 属性缺少冒号')
    const valueStart = skipJsonWhitespace(source, cursor + 1)
    const valueEnd = jsonValueEnd(source, valueStart)
    if (key === property) found = source.slice(valueStart, valueEnd)
    cursor = skipJsonWhitespace(source, valueEnd)
    if (source[cursor] === ',') cursor = skipJsonWhitespace(source, cursor + 1)
    else if (source[cursor] !== '}') throw new Error('Android note.json 对象分隔符无效')
  }
  if (found !== undefined) return found
  throw new Error(`Android note.json 缺少 ${property}`)
}

function splitJsonArrayValues(source: string): string[] {
  let cursor = skipJsonWhitespace(source, 0)
  if (source[cursor] !== '[') throw new Error('Android note.json 正文块不是数组')
  cursor = skipJsonWhitespace(source, cursor + 1)
  const values: string[] = []
  if (source[cursor] === ']') return values
  while (cursor < source.length) {
    const end = jsonValueEnd(source, cursor)
    values.push(source.slice(cursor, end))
    cursor = skipJsonWhitespace(source, end)
    if (source[cursor] === ']') return values
    if (source[cursor] !== ',') throw new Error('Android note.json 正文块数组分隔符无效')
    cursor = skipJsonWhitespace(source, cursor + 1)
  }
  throw new Error('Android note.json 正文块数组未闭合')
}

function jsonValueEnd(source: string, start: number): number {
  const first = source[start]
  if (first === '"') return jsonStringEnd(source, start)
  if (first === '{' || first === '[') {
    const closing: string[] = [first === '{' ? '}' : ']']
    let cursor = start + 1
    while (cursor < source.length && closing.length) {
      const character = source[cursor]
      if (character === '"') cursor = jsonStringEnd(source, cursor)
      else {
        if (character === '{') closing.push('}')
        else if (character === '[') closing.push(']')
        else if (character === '}' || character === ']') {
          if (closing.pop() !== character) throw new Error('Android note.json 括号不匹配')
        }
        cursor++
      }
    }
    if (closing.length) throw new Error('Android note.json 复合值未闭合')
    return cursor
  }
  let cursor = start
  while (cursor < source.length && !/[\s,}\]]/.test(source[cursor]!)) cursor++
  if (cursor === start) throw new Error('Android note.json 值为空')
  return cursor
}

function jsonStringEnd(source: string, start: number): number {
  let cursor = start + 1
  while (cursor < source.length) {
    if (source[cursor] === '\\') cursor += 2
    else if (source[cursor++] === '"') return cursor
  }
  throw new Error('Android note.json 字符串未闭合')
}

function skipJsonWhitespace(source: string, start: number): number {
  let cursor = start
  while (cursor < source.length && /\s/.test(source[cursor]!)) cursor++
  return cursor
}

function compactJson(source: string): string {
  let result = ''
  let inString = false
  for (let index = 0; index < source.length; index++) {
    const character = source[index]!
    if (inString) {
      result += character
      if (character === '\\' && index + 1 < source.length) result += source[++index]!
      else if (character === '"') inString = false
    } else if (character === '"') {
      inString = true
      result += character
    } else if (!/\s/.test(character)) result += character
  }
  return result
}

function validateInk(value: unknown): asserts value is Record<string, unknown> {
  if (!isRecord(value) || !isFiniteNumber(value.width) || !isFiniteNumber(value.height) ||
      value.width < 80 || value.width > 2048 || value.height < 80 || value.height > 4096 ||
      !Array.isArray(value.strokes) || value.strokes.length > 10_000) {
    throw new Error('手写向量不符合 Android schema-v1 尺寸或笔画结构')
  }
  let pointsTotal = 0
  for (const stroke of value.strokes) {
    if (!isRecord(stroke) || !Array.isArray(stroke.points) || !stroke.points.length) throw new Error('手写笔画缺少坐标点')
    const width = stroke.width === undefined ? 2.5 : stroke.width
    if (!isFiniteNumber(width) || width < 0.5 || width > 24) throw new Error('手写笔画宽度无效')
    pointsTotal += stroke.points.length
    if (pointsTotal > 50_000) throw new Error('手写坐标点超过 Web 安全上限')
    for (const point of stroke.points) {
      if (!isRecord(point)) throw new Error('手写坐标或压力超出 Android schema-v1 范围')
      const x = point.x
      const y = point.y
      const pressure = point.pressure ?? 1
      if (!isFiniteNumber(x) || !isFiniteNumber(y) || !isFiniteNumber(pressure) ||
          x < 0 || x > value.width || y < 0 || y > value.height || pressure < 0 || pressure > 1) {
        throw new Error('手写坐标或压力超出 Android schema-v1 范围')
      }
    }
  }
}

function hasImageSignature(bytes: Uint8Array, mimeType: SupportedPhotoMime): boolean {
  if (mimeType === 'image/png') return hasPngSignature(bytes)
  if (mimeType === 'image/jpeg') return bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff
  return bytes.length >= 12 && ascii(bytes, 0, 4) === 'RIFF' && ascii(bytes, 8, 4) === 'WEBP'
}

function hasPngSignature(bytes: Uint8Array) {
  return bytes.length >= 8 && bytes[0] === 0x89 && ascii(bytes, 1, 3) === 'PNG' &&
    bytes[4] === 0x0d && bytes[5] === 0x0a && bytes[6] === 0x1a && bytes[7] === 0x0a
}

function ascii(bytes: Uint8Array, offset: number, length: number) {
  return String.fromCharCode(...bytes.subarray(offset, offset + length))
}

function isAndroidPackagePath(path: string) {
  return isSupportedAndroidPackagePath(path)
}

function isSupportedAndroidPackagePath(path: string) {
  return path === 'note.json' || path === 'document.md' || isAndroidPhotoPath(path) || isAndroidInkPreviewPath(path)
}

function isAndroidPhotoPath(path: string) {
  return /^assets\/[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.(?:jpg|png|webp)$/i.test(path)
}

function isAndroidInkPreviewPath(path: string) {
  return /^assets\/[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}-[0-9a-f]{64}\.png$/i.test(path)
}

function isSafeArchivePath(path: string) {
  return !path.startsWith('/') && !path.includes('\\') && !path.includes('\0') && !path.endsWith('/') &&
    !path.split('/').some((segment) => !segment || segment === '.' || segment === '..')
}

function requireEntry(entries: Map<string, Uint8Array>, path: string) {
  const value = entries.get(path)
  if (!value) throw new Error(`笔记包缺少 ${path}`)
  return value
}

function isPhotoMime(value: string): value is SupportedPhotoMime {
  return value === 'image/jpeg' || value === 'image/png' || value === 'image/webp'
}

function photoExtension(mimeType: SupportedPhotoMime) {
  return mimeType === 'image/jpeg' ? 'jpg' : mimeType === 'image/png' ? 'png' : 'webp'
}

function isUuid(value: unknown): value is string {
  return typeof value === 'string' && UUID_PATTERN.test(value)
}

function isInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value)
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

const crcTable = Uint32Array.from({ length: 256 }, (_, index) => {
  let value = index
  for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1
  return value >>> 0
})

function crc32(bytes: Uint8Array) {
  let value = 0xffffffff
  for (const byte of bytes) value = crcTable[(value ^ byte) & 0xff]! ^ (value >>> 8)
  return (value ^ 0xffffffff) >>> 0
}
