#!/usr/bin/env node
/**
 * 校验已打包的浏览器插件 zip 是「规范归档」（#312）。
 *
 * 检查项：
 *   1. 归档可解析、非空；
 *   2. 条目名一律用 `/`，不得出现 `\`
 *      （Windows 的 Compress-Archive 会写反斜杠，Firefox 与规范解包器直接判定损坏）；
 *   3. 存在 manifest.json，且 manifest 引用的文件在包内真实存在；
 *   4. 期望的目标浏览器与 manifest 版本一致
 *      （firefox → MV2 + gecko id；chrome → MV3）。
 *
 * 用法：
 *   node scripts/ci/verify-extension-zips.mjs <zip>[:<browser>] [<zip>:<browser> ...]
 * 例：
 *   node scripts/ci/verify-extension-zips.mjs \
 *     publish/browser-extension.zip:chrome publish/browser-extension-firefox.zip:firefox
 */
import { existsSync, readFileSync } from 'node:fs'
import { inflateRawSync } from 'node:zlib'
import { readZipEntryNames } from './zip-writer.mjs'

const SIG_LOCAL = 0x04034b50
const SIG_CENTRAL = 0x02014b50
const SIG_EOCD = 0x06054b50

/** 从已读入内存的 zip 中取出单个条目的内容（只处理 store / deflate）。 */
function readZipEntry(buffer, wantedName) {
  let eocd = -1
  for (let i = buffer.length - 22; i >= Math.max(0, buffer.length - 22 - 0xffff); i--) {
    if (buffer.readUInt32LE(i) === SIG_EOCD) {
      eocd = i
      break
    }
  }
  if (eocd < 0) throw new Error('no EOCD record')

  const entryCount = buffer.readUInt16LE(eocd + 10)
  let cursor = buffer.readUInt32LE(eocd + 16)

  for (let i = 0; i < entryCount; i++) {
    if (buffer.readUInt32LE(cursor) !== SIG_CENTRAL) throw new Error('corrupt central directory')
    const method = buffer.readUInt16LE(cursor + 10)
    const compressedSize = buffer.readUInt32LE(cursor + 20)
    const nameLength = buffer.readUInt16LE(cursor + 28)
    const extraLength = buffer.readUInt16LE(cursor + 30)
    const commentLength = buffer.readUInt16LE(cursor + 32)
    const localOffset = buffer.readUInt32LE(cursor + 42)
    const name = buffer.toString('utf8', cursor + 46, cursor + 46 + nameLength)

    if (name.replace(/\\/g, '/') === wantedName) {
      if (buffer.readUInt32LE(localOffset) !== SIG_LOCAL) throw new Error('corrupt local header')
      const localNameLength = buffer.readUInt16LE(localOffset + 26)
      const localExtraLength = buffer.readUInt16LE(localOffset + 28)
      const dataStart = localOffset + 30 + localNameLength + localExtraLength
      const payload = buffer.subarray(dataStart, dataStart + compressedSize)
      if (method === 0) return payload
      if (method === 8) return inflateRawSync(payload)
      throw new Error(`unsupported compression method ${method}`)
    }

    cursor += 46 + nameLength + extraLength + commentLength
  }

  throw new Error(`entry not found: ${wantedName}`)
}

/** 校验单个归档；返回该归档的问题列表（空数组表示通过）。 */
export function verifyArchive(zipPath, browser = 'chrome') {
  const problems = []

  if (!existsSync(zipPath)) return [`${zipPath}: file does not exist`]

  let names
  try {
    names = readZipEntryNames(zipPath)
  } catch (error) {
    return [`${zipPath}: not a readable zip (${error.message})`]
  }

  if (names.length === 0) return [`${zipPath}: archive is empty`]

  // 1. 规范分隔符：这是 #312 的核心缺陷。
  const backslash = names.filter((name) => name.includes('\\'))
  if (backslash.length > 0) {
    problems.push(`${zipPath}: ${backslash.length} entry name(s) use a backslash separator (e.g. ${backslash[0]})`)
  }

  const normalized = new Set(names.map((name) => name.replace(/\\/g, '/')))
  if (!normalized.has('manifest.json')) {
    problems.push(`${zipPath}: manifest.json missing from the archive root`)
    return problems
  }

  // 2. manifest 的引用必须在包内存在（Firefox 报"损坏"就是因为这里对不上）。
  let manifest
  try {
    manifest = JSON.parse(readZipEntry(readFileSync(zipPath), 'manifest.json').toString('utf8'))
  } catch (error) {
    problems.push(`${zipPath}: manifest.json is not valid JSON (${error.message})`)
    return problems
  }

  const referenced = []
  if (Array.isArray(manifest.background?.scripts)) referenced.push(...manifest.background.scripts)
  if (typeof manifest.background?.service_worker === 'string') referenced.push(manifest.background.service_worker)
  for (const reference of referenced) {
    if (!normalized.has(reference.replace(/\\/g, '/'))) {
      problems.push(`${zipPath}: manifest references a file that is not in the archive: ${reference}`)
    }
  }

  // 3. 目标一致性：把 Chrome 产物当 Firefox 发正是本 issue 的配套缺陷。
  if (browser === 'firefox') {
    if (manifest.manifest_version !== 2) {
      problems.push(`${zipPath}: marked as firefox but manifest_version=${manifest.manifest_version} (expected 2)`)
    }
    if (manifest.background?.service_worker) {
      problems.push(`${zipPath}: marked as firefox but declares background.service_worker (MV3 only)`)
    }
    if (!manifest.browser_specific_settings?.gecko?.id) {
      problems.push(`${zipPath}: marked as firefox but browser_specific_settings.gecko.id is missing`)
    }
  } else if (manifest.manifest_version !== 3) {
    problems.push(`${zipPath}: marked as chrome but manifest_version=${manifest.manifest_version} (expected 3)`)
  }

  return problems
}

// 作为脚本直接运行时才读参数（被 import 时只暴露 verifyArchive）。
if (import.meta.url === `file://${process.argv[1]}`) {
  const specs = process.argv.slice(2)
  if (specs.length === 0) {
    console.error('usage: verify-extension-zips.mjs <zip>[:chrome|firefox] ...')
    process.exit(2)
  }

  const allProblems = []
  for (const spec of specs) {
    const colonIndex = spec.lastIndexOf(':')
    // Windows 绝对路径（C:\...）也含冒号，所以只在后缀确实是浏览器名时才切分。
    const hasBrowserSuffix = colonIndex > 0 && ['chrome', 'firefox'].includes(spec.slice(colonIndex + 1))
    const zipPath = hasBrowserSuffix ? spec.slice(0, colonIndex) : spec
    const browser = hasBrowserSuffix ? spec.slice(colonIndex + 1) : 'chrome'

    const problems = verifyArchive(zipPath, browser)
    if (problems.length === 0) {
      console.log(`✓ ${zipPath} (${browser})`)
    } else {
      for (const problem of problems) console.error(`✗ ${problem}`)
      allProblems.push(...problems)
    }
  }

  if (allProblems.length > 0) {
    console.error(`\n${allProblems.length} problem(s) found`)
    process.exit(1)
  }
  console.log('\nAll extension archives are spec-compliant')
}
