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
import { join } from 'node:path'
import { test } from 'node:test'
import { verifyArchive } from './verify-extension-zips.mjs'
import { writeZipFromDirectory } from './zip-writer.mjs'

const REPO_ROOT = new URL('../..', import.meta.url).pathname

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
