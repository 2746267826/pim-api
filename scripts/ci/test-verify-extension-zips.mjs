#!/usr/bin/env node
/**
 * #312 回归测试：产物校验器必须能拦住「不可用产物」。
 *
 * 这正是 issue 里实测失败的那条链路——安装包里的 zip 被 Firefox 判定损坏，
 * 因此校验器本身必须有回归测试，否则它只是又一次"看起来对"的实现。
 *
 * 运行：node --test scripts/ci/test-verify-extension-zips.mjs
 */
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'
import { verifyArchive } from './verify-extension-zips.mjs'
import { crc32 } from './zip-writer.mjs'
import { writeZipFromDirectory } from './zip-writer.mjs'

// 必须用 fileURLToPath 而不是 URL.pathname：在 Windows 上 pathname 会给出
// `/D:/a/...`，再交给 path.join 就变成 `D:\D:\a\...`（CI 上实测报
// "Cannot find module 'D:\D:\a\...'")。
const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..')

function makeExtension({ browser }) {
  const root = mkdtempSync(join(tmpdir(), 'pim-ext-'))
  mkdirSync(join(root, 'src', 'background'), { recursive: true })
  const manifest = browser === 'firefox'
    ? {
        name: 'PIM 浏览器记录',
        version: '1.0.0',
        manifest_version: 2,
        background: { scripts: ['src/background/main.js'], persistent: true },
        browser_action: { default_popup: 'src/popup/index.html' },
        browser_specific_settings: { gecko: { id: '{ef87d84c-2127-493f-b952-5b4e744245bc}' } },
        permissions: ['tabs', '<all_urls>'],
      }
    : {
        name: 'PIM 浏览器记录',
        version: '1.0.0',
        manifest_version: 3,
        background: { service_worker: 'src/background/main.js', type: 'module' },
        action: { default_popup: 'src/popup/index.html' },
        host_permissions: ['<all_urls>'],
      }
  writeFileSync(join(root, 'manifest.json'), JSON.stringify(manifest, null, 2))
  writeFileSync(join(root, 'src', 'background', 'main.js'), 'console.log("bg")\n')
  // manifest 引用的文件必须真的存在——校验器现在会逐项检查（#312 评审：以前只查
  // background，fixture 声明了 popup 却没有这个文件也照样"通过"）。
  mkdirSync(join(root, 'src', 'popup'), { recursive: true })
  writeFileSync(join(root, 'src', 'popup', 'index.html'), '<!doctype html><title>popup</title>\n')
  return root
}

function zipIt(root, name = 'ext.zip') {
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  const zipPath = join(dir, name)
  writeZipFromDirectory(root, zipPath)
  return { zipPath, dir }
}

test('合规产物通过校验（firefox / chrome 各一份）', () => {
  const ffRoot = makeExtension({ browser: 'firefox' })
  const crRoot = makeExtension({ browser: 'chrome' })
  const ff = zipIt(ffRoot, 'ff.zip')
  const cr = zipIt(crRoot, 'cr.zip')
  try {
    assert.deepEqual(verifyArchive(ff.zipPath, 'firefox'), [])
    assert.deepEqual(verifyArchive(cr.zipPath, 'chrome'), [])
  } finally {
    for (const p of [ffRoot, crRoot, ff.dir, cr.dir]) rmSync(p, { recursive: true, force: true })
  }
})

test('manifest 引用了包内不存在的文件 → 必须报错（Firefox 报"损坏"的直接原因）', () => {
  const root = makeExtension({ browser: 'firefox' })
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  try {
    // 删掉背景脚本后再打包：manifest 的引用就落空了。
    rmSync(join(root, 'src', 'background', 'main.js'))
    const zipPath = join(dir, 'broken.zip')
    writeZipFromDirectory(root, zipPath)

    const problems = verifyArchive(zipPath, 'firefox')
    assert.equal(problems.length, 1)
    assert.match(problems[0], /references a file that is not in the archive/)
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})

test('把 Chrome(MV3) 产物当 Firefox 发布 → 必须报错（#312 的配套缺陷）', () => {
  const root = makeExtension({ browser: 'chrome' })
  const zip = zipIt(root, 'chrome-as-firefox.zip')
  try {
    const problems = verifyArchive(zip.zipPath, 'firefox')
    assert.ok(problems.some((p) => /manifest_version=3 \(expected 2\)/.test(p)), problems.join('; '))
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zip.dir, { recursive: true, force: true })
  }
})

test('缺少 gecko id 的 Firefox 产物 → 必须报错（未签名加载必需）', () => {
  const root = makeExtension({ browser: 'firefox' })
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  try {
    const manifestPath = join(root, 'manifest.json')
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
    delete manifest.browser_specific_settings
    writeFileSync(manifestPath, JSON.stringify(manifest))

    const zipPath = join(dir, 'nogecko.zip')
    writeZipFromDirectory(root, zipPath)
    const problems = verifyArchive(zipPath, 'firefox')
    assert.ok(problems.some((p) => /gecko.id is missing/.test(p)), problems.join('; '))
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})

test('不存在的文件与空目录 → 必须报错而不是静默通过', () => {
  const emptyRoot = mkdtempSync(join(tmpdir(), 'pim-empty-'))
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  try {
    assert.match(verifyArchive(join(dir, 'nope.zip'), 'chrome')[0], /does not exist/)

    const zipPath = join(dir, 'empty.zip')
    writeZipFromDirectory(emptyRoot, zipPath)
    assert.match(verifyArchive(zipPath, 'chrome')[0], /archive is empty/)
  } finally {
    rmSync(emptyRoot, { recursive: true, force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})

test('命令行入口：合规产物退出码为 0', () => {
  const root = makeExtension({ browser: 'firefox' })
  const zip = zipIt(root, 'ff.zip')
  try {
    const out = execFileSync('node', [
      join(REPO_ROOT, 'scripts/ci/verify-extension-zips.mjs'),
      `${zip.zipPath}:firefox`,
    ], { encoding: 'utf8' })
    assert.match(out, /spec-compliant/)
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zip.dir, { recursive: true, force: true })
  }
})

test('命令行入口：不合规产物退出码非 0', () => {
  const root = makeExtension({ browser: 'chrome' })
  const zip = zipIt(root, 'chrome-as-ff.zip')
  try {
    assert.throws(() => execFileSync('node', [
      join(REPO_ROOT, 'scripts/ci/verify-extension-zips.mjs'),
      `${zip.zipPath}:firefox`,
    ], { encoding: 'utf8', stdio: 'pipe' }))
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zip.dir, { recursive: true, force: true })
  }
})

test('损坏的 CRC → 校验必须失败（不能只看中央目录条目名就放行）', () => {
  // #312 评审：把中央目录里 manifest 的 CRC 改成 0，旧的"只读条目名"校验仍然
  // 返回"无问题"，损坏产物会被发出去。这里锁定必须失败。
  const root = makeExtension({ browser: 'firefox' })
  const zip = zipIt(root, 'corrupt-crc.zip')
  try {
    const buffer = readFileSync(zip.zipPath)
    // 找中央目录头，定位 manifest.json 条目并破坏其 CRC 字段（偏移 +16）。
    const SIG_CENTRAL = 0x02014b50
    let patched = false
    for (let i = 0; i + 46 <= buffer.length; i++) {
      if (buffer.readUInt32LE(i) !== SIG_CENTRAL) continue
      const nameLength = buffer.readUInt16LE(i + 28)
      const name = buffer.toString('utf8', i + 46, i + 46 + nameLength)
      if (name === 'manifest.json') {
        buffer.writeUInt32LE(0, i + 16)
        patched = true
        break
      }
    }
    assert.ok(patched, 'test setup: manifest.json central entry not found')
    writeFileSync(zip.zipPath, buffer)

    const problems = verifyArchive(zip.zipPath, 'firefox')
    assert.ok(
      problems.some((p) => /CRC mismatch/.test(p)),
      `expected a CRC mismatch problem, got: ${problems.join('; ') || '(none)'}`,
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zip.dir, { recursive: true, force: true })
  }
})

test('popup / options 等非 background 引用缺失 → 校验必须失败', () => {
  // #312 评审：只检查 background 会放过 popup/options 的坏路径。
  const root = makeExtension({ browser: 'firefox' })
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  try {
    rmSync(join(root, 'src', 'popup', 'index.html'))
    const zipPath = join(dir, 'nopopup.zip')
    writeZipFromDirectory(root, zipPath)

    const problems = verifyArchive(zipPath, 'firefox')
    assert.ok(
      problems.some((p) => p.includes('src/popup/index.html')),
      `expected the missing popup to be reported, got: ${problems.join('; ') || '(none)'}`,
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})

test('条目名逃出归档根或为绝对路径 → 校验必须失败', () => {
  const dir = mkdtempSync(join(tmpdir(), 'pim-evilzip-'))
  try {
    for (const [name, label] of [
      ['../evil.js', 'parent traversal'],
      ['/etc/passwd.js', 'absolute path'],
      ['C:/windows/system32/x.js', 'drive-letter absolute path'],
    ]) {
      const zipPath = join(dir, `${label.replace(/[^a-z]/gi, '_')}.zip`)
      const nameBytes = Buffer.from(name, 'utf8')
      const data = Buffer.from('x', 'utf8')
      const crc = crc32(data)

      const local = Buffer.alloc(30)
      local.writeUInt32LE(0x04034b50, 0)
      local.writeUInt16LE(20, 4)
      local.writeUInt16LE(0, 8)
      local.writeUInt32LE(crc, 14)
      local.writeUInt32LE(data.length, 18)
      local.writeUInt32LE(data.length, 22)
      local.writeUInt16LE(nameBytes.length, 26)

      const central = Buffer.alloc(46)
      central.writeUInt32LE(0x02014b50, 0)
      central.writeUInt16LE(20, 4)
      central.writeUInt16LE(20, 6)
      central.writeUInt16LE(0, 10)
      central.writeUInt32LE(crc, 16)
      central.writeUInt32LE(data.length, 20)
      central.writeUInt32LE(data.length, 24)
      central.writeUInt16LE(nameBytes.length, 28)

      const centralStart = local.length + nameBytes.length + data.length
      const eocd = Buffer.alloc(22)
      eocd.writeUInt32LE(0x06054b50, 0)
      eocd.writeUInt16LE(1, 8)
      eocd.writeUInt16LE(1, 10)
      eocd.writeUInt32LE(central.length + nameBytes.length, 12)
      eocd.writeUInt32LE(centralStart, 16)

      writeFileSync(zipPath, Buffer.concat([local, nameBytes, data, central, nameBytes, eocd]))

      const problems = verifyArchive(zipPath, 'chrome')
      assert.ok(
        problems.some((p) => /escapes the archive root|absolute path/.test(p)),
        `${label}: expected a path-safety problem, got: ${problems.join('; ') || '(none)'}`,
      )
    }
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('页面路径带 fragment/query（static/app.html#/route）不应被误判为缺失文件', () => {
  // time-tracker fork 真实产物就是 options_ui.page = "static/app.html#/other/option"；
  // 归档里只有 static/app.html。校验器去掉 fragment 后才不会误报。
  const root = mkdtempSync(join(tmpdir(), 'pim-ext-frag-'))
  const dir = mkdtempSync(join(tmpdir(), 'pim-extzip-'))
  try {
    mkdirSync(join(root, 'static'), { recursive: true })
    writeFileSync(join(root, 'static', 'app.html'), '<!doctype html>')
    writeFileSync(join(root, 'manifest.json'), JSON.stringify({
      name: 'tt4b',
      version: '4.5.2',
      manifest_version: 2,
      background: { scripts: ['background.js'] },
      options_ui: { page: 'static/app.html#/other/option' },
      browser_action: { default_popup: 'static/app.html' },
      browser_specific_settings: { gecko: { id: '{a8cf72f7-09b7-4cd4-9aaa-7a023bf09916}' } },
    }))
    writeFileSync(join(root, 'background.js'), 'console.log(1)\n')

    const zipPath = join(dir, 'frag.zip')
    writeZipFromDirectory(root, zipPath)

    assert.deepEqual(verifyArchive(zipPath, 'firefox'), [])
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})
