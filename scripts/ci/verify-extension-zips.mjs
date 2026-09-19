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
import { existsSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { readZipEntry, readZipEntryNames } from './zip-writer.mjs'

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
    manifest = JSON.parse(readZipEntry(zipPath, 'manifest.json').toString('utf8'))
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
//
// 不能用 `import.meta.url === \`file://${process.argv[1]}\`` 判断：Windows 上
// process.argv[1] 是 `D:\a\...`，拼出来是 `file://D:\a\...`，永远不等于
// `file:///D:/a/...`，脚本会静默什么都不做并退出 0——校验在 Windows 上形同虚设。
// 用 fileURLToPath 归一化后比较真实路径。
const invokedDirectly = (() => {
  if (!process.argv[1]) return false
  try {
    return resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])
  } catch {
    return false
  }
})()

if (invokedDirectly) {
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
