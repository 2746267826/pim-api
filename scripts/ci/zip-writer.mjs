#!/usr/bin/env node
/**
 * 跨平台、符合规范的 zip 打包器。
 *
 * 为什么不用平台自带工具（#312）：
 *   - Windows 的 `Compress-Archive` 把条目名写成 `src\background\main.js`（反斜杠），
 *     而 zip 规范（APPNOTE 4.4.17.1）要求路径分隔符是 `/`。按规范读取的实现
 *     （Firefox 的附加组件安装、`unzip` 等）会因此找不到文件，实测报「损坏」。
 *   - `Compress-Archive` 还有 2GB 限制、不支持流式写入，且行为随 PowerShell 版本漂移。
 *   - 依赖外部 `zip` 命令则要求 runner 预装该工具。
 *
 * 因此这里自己写 zip：只依赖 Node 内置的 zlib，条目名一律用 `/`，
 * 在 Windows / Linux / macOS 上产出完全一致的字节。
 *
 * 实现说明：因为整份归档在内存里组装，每个条目的 CRC32 与大小在写本地文件头时就
 * 已经确定，所以直接写准确值即可——不需要 data descriptor，也不需要回填。
 * 这是兼容性最好的形态（所有解包器都支持），也避免了流式写入时"标志位与
 * descriptor 不一致"这一类经典坑。
 */
import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { join, posix, relative, sep } from 'node:path'
import { deflateRawSync } from 'node:zlib'

const CRC_TABLE = (() => {
  const table = new Int32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c
  }
  return table
})()

/** 标准 CRC32（zip 使用的多项式），返回无符号 32 位。 */
export function crc32(buffer) {
  let crc = -1
  for (let i = 0; i < buffer.length; i++) crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ buffer[i]) & 0xff]
  return (crc ^ -1) >>> 0
}

/**
 * 把本地文件系统路径转成 zip 条目名：**一律使用 `/`**。
 * 这是本模块存在的核心理由——绝不能让 `\` 泄漏进归档。
 */
export function toEntryName(relativePath) {
  return relativePath.split(sep).join(posix.sep)
}

/** 递归列出目录下所有文件的相对路径（不含目录条目，跳过空目录以免产生无用条目）。 */
export function listFilesRecursive(rootDir, currentDir = rootDir, acc = []) {
  for (const entry of readdirSync(currentDir, { withFileTypes: true })) {
    const full = join(currentDir, entry.name)
    if (entry.isDirectory()) {
      listFilesRecursive(rootDir, full, acc)
    } else if (entry.isFile()) {
      acc.push(relative(rootDir, full))
    }
  }
  return acc
}

const SIG_LOCAL_HEADER = 0x04034b50
const SIG_CENTRAL_HEADER = 0x02014b50
const SIG_EOCD = 0x06054b50
const VERSION_NEEDED = 20 // 2.0：支持 deflate
const METHOD_DEFLATE = 8
const METHOD_STORE = 0

/** DOS 时间戳（zip 只有这个精度）。 */
function toDosDateTime(date) {
  const year = Math.max(1980, date.getFullYear())
  const dosTime = (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1)
  const dosDate = ((year - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate()
  return { dosTime, dosDate }
}

/**
 * 把 <paramref name="sourceDir"/> 整个目录打包成 zip。
 *
 * @returns {{ entryCount: number, entryNames: string[], bytes: number }}
 */
export function writeZipFromDirectory(sourceDir, zipPath, options = {}) {
  const { onProgress } = options
  // 排序保证同样的输入产生逐字节一致的归档（便于比对与缓存）。
  const files = listFilesRecursive(sourceDir).sort()

  const central = []
  const chunks = []
  let offset = 0

  const pushBuffer = (buffer) => {
    chunks.push(buffer)
    offset += buffer.length
  }

  for (const relativePath of files) {
    const entryName = toEntryName(relativePath)
    if (entryName.includes('\\')) {
      throw new Error(`zip entry name must not contain a backslash: ${entryName}`)
    }

    const fullPath = join(sourceDir, relativePath)
    const data = readFileSync(fullPath)
    const stat = statSync(fullPath)
    const { dosTime, dosDate } = toDosDateTime(stat.mtime)

    // 只有真的变小才用 deflate，否则存储（小文件压缩反而变大）。
    const deflated = deflateRawSync(data, { level: 9 })
    const useDeflate = deflated.length < data.length
    const payload = useDeflate ? deflated : data
    const method = useDeflate ? METHOD_DEFLATE : METHOD_STORE
    const crc = crc32(data)

    // 条目名必须写成 UTF-8 字节；纯 ASCII 时不置 UTF-8 标志位以兼容老解包器。
    const nameBytes = Buffer.from(entryName, 'utf8')
    const isAscii = nameBytes.every((b) => b < 0x80)
    const flags = isAscii ? 0 : 0x0800

    const localHeader = Buffer.alloc(30)
    localHeader.writeUInt32LE(SIG_LOCAL_HEADER, 0)
    localHeader.writeUInt16LE(VERSION_NEEDED, 4)
    localHeader.writeUInt16LE(flags, 6)
    localHeader.writeUInt16LE(method, 8)
    localHeader.writeUInt16LE(dosTime, 10)
    localHeader.writeUInt16LE(dosDate, 12)
    localHeader.writeUInt32LE(crc, 14)
    localHeader.writeUInt32LE(payload.length, 18)
    localHeader.writeUInt32LE(data.length, 22)
    localHeader.writeUInt16LE(nameBytes.length, 26)
    localHeader.writeUInt16LE(0, 28) // extra field length

    const localHeaderOffset = offset
    pushBuffer(localHeader)
    pushBuffer(nameBytes)
    pushBuffer(payload)

    central.push({
      entryName,
      nameBytes,
      flags,
      method,
      dosTime,
      dosDate,
      crc,
      compressedSize: payload.length,
      uncompressedSize: data.length,
      localHeaderOffset,
    })

    onProgress?.(entryName)
  }

  const centralStart = offset
  for (const entry of central) {
    const header = Buffer.alloc(46)
    header.writeUInt32LE(SIG_CENTRAL_HEADER, 0)
    header.writeUInt16LE(VERSION_NEEDED, 4) // version made by
    header.writeUInt16LE(VERSION_NEEDED, 6) // version needed
    header.writeUInt16LE(entry.flags, 8)
    header.writeUInt16LE(entry.method, 10)
    header.writeUInt16LE(entry.dosTime, 12)
    header.writeUInt16LE(entry.dosDate, 14)
    header.writeUInt32LE(entry.crc, 16)
    header.writeUInt32LE(entry.compressedSize, 20)
    header.writeUInt32LE(entry.uncompressedSize, 24)
    header.writeUInt16LE(entry.nameBytes.length, 28)
    header.writeUInt16LE(0, 30) // extra
    header.writeUInt16LE(0, 32) // comment
    header.writeUInt16LE(0, 34) // disk number
    header.writeUInt16LE(0, 36) // internal attrs
    header.writeUInt32LE(0, 38) // external attrs
    header.writeUInt32LE(entry.localHeaderOffset, 42)

    pushBuffer(header)
    pushBuffer(entry.nameBytes)
  }
  const centralSize = offset - centralStart

  const eocd = Buffer.alloc(22)
  eocd.writeUInt32LE(SIG_EOCD, 0)
  eocd.writeUInt16LE(0, 4) // this disk
  eocd.writeUInt16LE(0, 6) // disk with central dir
  eocd.writeUInt16LE(central.length, 8)
  eocd.writeUInt16LE(central.length, 10)
  eocd.writeUInt32LE(centralSize, 12)
  eocd.writeUInt32LE(centralStart, 16)
  eocd.writeUInt16LE(0, 20) // comment length
  pushBuffer(eocd)

  const archive = Buffer.concat(chunks)
  writeFileSync(zipPath, archive)

  return {
    entryCount: central.length,
    entryNames: central.map((entry) => entry.entryName),
    bytes: archive.length,
  }
}

/**
 * 读取 zip 中央目录里的条目名（只解析元数据，不解压数据）。
 *
 * 用 Node 自己解析而不是 shell 调用 `unzip`：Windows runner 上并不保证有 `unzip`，
 * 而「校验 Windows 构建链产物」恰恰是本 issue 的重点，校验逻辑不能依赖平台工具（#312）。
 */
export function readZipEntryNames(zipPath) {
  const buffer = readFileSync(zipPath)

  // 从尾部往前找 EOCD（注释最长 65535 字节）。
  let eocd = -1
  for (let i = buffer.length - 22; i >= Math.max(0, buffer.length - 22 - 0xffff); i--) {
    if (buffer.readUInt32LE(i) === SIG_EOCD) {
      eocd = i
      break
    }
  }
  if (eocd < 0) throw new Error(`${zipPath} is not a zip archive (no EOCD record found)`)

  const entryCount = buffer.readUInt16LE(eocd + 10)
  let cursor = buffer.readUInt32LE(eocd + 16)
  const names = []

  for (let i = 0; i < entryCount; i++) {
    if (buffer.readUInt32LE(cursor) !== SIG_CENTRAL_HEADER) {
      throw new Error(`${zipPath} has a corrupt central directory at offset ${cursor}`)
    }
    const nameLength = buffer.readUInt16LE(cursor + 28)
    const extraLength = buffer.readUInt16LE(cursor + 30)
    const commentLength = buffer.readUInt16LE(cursor + 32)
    names.push(buffer.toString('utf8', cursor + 46, cursor + 46 + nameLength))
    cursor += 46 + nameLength + extraLength + commentLength
  }

  return names
}
