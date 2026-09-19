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
import { inspectZip } from './zip-writer.mjs'

/**
 * 递归收集 manifest 里引用的包内文件路径。
 *
 * 只检查 background 是不够的（#312 评审）：popup / options / icons /
 * content_scripts / web_accessible_resources 里写错路径同样会让插件加载失败
 * 或功能缺失，而纯 background 检查会放过它们。
 *
 * 只收集看起来像"包内相对路径"的字符串：跳过 scheme/绝对 URL、通配符、以及
 * 以扩展 API 占位符（__MSG_*__）开头或含 * 的匹配模式。
 */
export function collectManifestReferences(manifest) {
  const references = new Set()

  // 只检查"值本身就是包内相对路径"的字段。用白名单字段名而不是"长得像路径就当路径"，
  // 因为 manifest 里还有 version（"1.0.0"）、minimum_chrome_version（"140"）这类值，
  // 按形状猜测会把它们误判成缺失文件（实测 version "1.0.0" 会被当成路径）。
  const isArchivePath = (value) => {
    if (typeof value !== 'string' || value.length === 0) return false
    if (value.startsWith('__MSG_')) return false
    if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return false // scheme: http:, data:, chrome-extension:
    if (value.includes('*')) return false // match pattern / glob
    if (value.startsWith('/')) return false
    // 单独的 "#..." / "?..." 是页面内路由，不是文件路径。
    if (/^[#?]/.test(value)) return false
    return true
  }

  // manifest 允许把页面内路由写进页面路径，例如
  // options_ui.page = "static/app.html#/other/option"（time-tracker fork 实际就这么写）。
  // 归档里只存在 `static/app.html`，因此比对前必须去掉 fragment/query，
  // 否则会把完全正常的产物误判为"引用了不存在的文件"。
  const toArchivePath = (value) => value.replace(/\\/g, '/').split('#')[0].split('?')[0]

  const add = (value) => {
    if (isArchivePath(value)) references.add(toArchivePath(value))
  }

  // background
  const background = manifest.background ?? {}
  if (typeof background.service_worker === 'string') add(background.service_worker)
  for (const script of Array.isArray(background.scripts) ? background.scripts : []) add(script)
  if (typeof background.page === 'string') add(background.page)

  // 顶层页面/资源类字段
  for (const key of ['options_ui', 'options_page', 'devtools_page', 'sidebar_action', 'browser_action', 'action', 'chrome_url_overrides']) {
    const node = manifest[key]
    if (typeof node === 'string') {
      add(node)
    } else if (node && typeof node === 'object') {
      if (typeof node.page === 'string') add(node.page)
      if (typeof node.default_popup === 'string') add(node.default_popup)
      if (typeof node.default_panel === 'string') add(node.default_panel)
      if (typeof node.default_icon === 'string') add(node.default_icon)
      if (node.default_icon && typeof node.default_icon === 'object') {
        for (const iconPath of Object.values(node.default_icon)) add(iconPath)
      }
    }
  }

  // icons / theme 的路径表
  for (const key of ['icons', 'theme_icons']) {
    const icons = manifest[key]
    if (icons && typeof icons === 'object') {
      for (const iconPath of Object.values(icons)) add(iconPath)
    }
  }

  // content_scripts / web_accessible_resources 的 js+css（含 MV2 的字符串数组形态）
  for (const entry of Array.isArray(manifest.content_scripts) ? manifest.content_scripts : []) {
    for (const script of Array.isArray(entry?.js) ? entry.js : []) add(script)
    for (const style of Array.isArray(entry?.css) ? entry.css : []) add(style)
  }
  for (const entry of Array.isArray(manifest.web_accessible_resources) ? manifest.web_accessible_resources : []) {
    if (typeof entry === 'string') {
      add(entry)
      continue
    }
    for (const resource of Array.isArray(entry?.resources) ? entry.resources : []) add(resource)
  }

  return [...references]
}

/** 校验单个归档；返回该归档的问题列表（空数组表示通过）。 */
export function verifyArchive(zipPath, browser = 'chrome') {
  if (!existsSync(zipPath)) return [`${zipPath}: file does not exist`]

  // 完整解析（含 CRC / 大小 / 本地头一致性）：损坏归档必须在这里就失败，
  // 不能只看中央目录的条目名就放行（#312 评审）。
  let entries
  try {
    entries = inspectZip(zipPath).entries
  } catch (error) {
    return [`${zipPath}: not a readable zip (${error.message})`]
  }

  const problems = []
  if (entries.length === 0) return [`${zipPath}: archive is empty`]

  const names = entries.map((entry) => entry.name)

  // 1. 规范分隔符：这是 #312 的核心缺陷。
  const backslash = names.filter((name) => name.includes('\\'))
  if (backslash.length > 0) {
    problems.push(`${zipPath}: ${backslash.length} entry name(s) use a backslash separator (e.g. ${backslash[0]})`)
  }

  // 2. 路径安全：条目名不得逃出归档根，也不得是绝对路径。
  for (const name of names) {
    const normalized = name.replace(/\\/g, '/')
    if (normalized.startsWith('/') || /^[a-z]:/i.test(normalized)) {
      problems.push(`${zipPath}: entry uses an absolute path: ${name}`)
    }
    if (normalized.split('/').includes('..')) {
      problems.push(`${zipPath}: entry escapes the archive root: ${name}`)
    }
  }

  const normalized = new Set(names.map((name) => name.replace(/\\/g, '/')))
  if (!normalized.has('manifest.json')) {
    problems.push(`${zipPath}: manifest.json missing from the archive root`)
    return problems
  }

  // 3. manifest 自身与其引用的每个文件都必须真实存在。
  let manifest
  try {
    manifest = JSON.parse(
      entries.find((entry) => entry.name.replace(/\\/g, '/') === 'manifest.json').content.toString('utf8'),
    )
  } catch (error) {
    problems.push(`${zipPath}: manifest.json is not valid JSON (${error.message})`)
    return problems
  }

  for (const reference of collectManifestReferences(manifest)) {
    if (!normalized.has(reference)) {
      problems.push(`${zipPath}: manifest references a file that is not in the archive: ${reference}`)
    }
  }

  // 4. 目标一致性：把 Chrome 产物当 Firefox 发正是本 issue 的配套缺陷。
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
