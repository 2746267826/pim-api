#!/usr/bin/env node
/**
 * #312 回归：站点级（site）产物的 PIM 上报钩子校验必须在**两个浏览器目标**下都执行。
 *
 * 评审发现的缺陷：Firefox 分支把 `kind === 'site'` 的 reporter 检查整个跳过了，
 * 于是「Firefox 版 Time Tracker fork 缺了上报逻辑」也能打包发布——产物不可用却无人拦。
 *
 * 这里用一个假源码目录跑真实的构建脚本（--browser firefox --kind site），
 * 断言缺少 marker 时必须失败。
 */
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const BUILDER = join(REPO_ROOT, 'scripts/ci/build-browser-extension.mjs')

/** 造一个最小 site 级源码目录；`withReporter` 决定产物里是否含 PIM 上报 marker。 */
function makeSiteSource({ withReporter }) {
  const src = mkdtempSync(join(tmpdir(), 'pim-site-src-'))

  // 已构建好的产物目录（脚本会直接使用 npm run build 的结果，这里用假 build 脚本产出）。
  const distDir = join(src, 'dist_prod_firefox')
  mkdirSync(join(distDir, 'vendor'), { recursive: true })
  writeFileSync(join(distDir, 'manifest.json'), JSON.stringify({
    name: 'tt4b',
    version: '4.5.2',
    manifest_version: 2,
    background: { scripts: ['background.js'], persistent: false },
    browser_action: { default_popup: 'static/popup.html' },
    browser_specific_settings: { gecko: { id: '{a8cf72f7-09b7-4cd4-9aaa-7a023bf09916}' } },
    permissions: ['storage', '<all_urls>'],
  }))
  writeFileSync(join(distDir, 'background.js'), withReporter
    ? 'fetch("http://localhost:15601/browser/site/heartbeat");\n'
    : 'console.log("no pim reporting hook");\n')
  mkdirSync(join(distDir, 'static'), { recursive: true })
  writeFileSync(join(distDir, 'static', 'x.js'), 'x')
  writeFileSync(join(distDir, 'static', 'popup.html'), '<!doctype html>')

  // 假 package.json：build:firefox 只做一件事——什么都不做（产物已就位）。
  writeFileSync(join(src, 'package.json'), JSON.stringify({
    name: 'tt4b',
    version: '4.5.2',
    scripts: {
      'pure-install': 'node -e "process.exit(0)"',
      'build:firefox': 'node -e "process.exit(0)"',
    },
  }))

  return src
}

function runBuilder(src) {
  const out = mkdtempSync(join(tmpdir(), 'pim-site-out-'))
  const zipPath = join(mkdtempSync(join(tmpdir(), 'pim-site-zip-')), 'site-firefox.zip')
  try {
    execFileSync('node', [
      BUILDER,
      '--src', src,
      '--out', out,
      '--zip', zipPath,
      '--kind', 'site',
      '--browser', 'firefox',
    ], { encoding: 'utf8', stdio: 'pipe' })
    return { ok: true, stderr: '' }
  } catch (error) {
    return { ok: false, stderr: String(error.stderr ?? error.message) }
  } finally {
    rmSync(out, { recursive: true, force: true })
    rmSync(dirname(zipPath), { recursive: true, force: true })
  }
}

test('Firefox 站点级产物缺少 PIM 上报钩子 → 构建必须失败', () => {
  const src = makeSiteSource({ withReporter: false })
  try {
    const result = runBuilder(src)
    assert.equal(result.ok, false, '缺少 reporter 的 Firefox site 产物本应被拒绝')
    assert.match(result.stderr, /site reporter/i)
  } finally {
    rmSync(src, { recursive: true, force: true })
  }
})

test('Firefox 站点级产物含 PIM 上报钩子 → 构建成功', () => {
  const src = makeSiteSource({ withReporter: true })
  try {
    const result = runBuilder(src)
    assert.equal(result.ok, true, `构建本应成功，stderr=${result.stderr}`)
  } finally {
    rmSync(src, { recursive: true, force: true })
  }
})
