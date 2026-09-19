#!/usr/bin/env node
/**
 * Build a browser extension from an external fork and stage it for the
 * Windows installer.
 *
 * The two PIM extension forks differ in layout, so this script adapts instead
 * of hard-coding one shape:
 *   - 2746267826/tracker-web            (PIM Browser Watcher, URL-level)  -> build/
 *   - 2746267826/time-tracker-4-browser (Time Tracker PIM fork, site-level) -> dist_prod/
 * It also tolerates a missing package-lock.json (the Time Tracker fork does not
 * commit one upstream), choosing the right install command per repository.
 *
 * Both forks ship a Firefox target (`build:firefox`). Since #312 the script can
 * build it too (`--browser firefox`), because the Chrome MV3 build simply cannot
 * run on Firefox and shipping only that left Firefox users without any usable
 * artifact.
 *
 * Usage:
 *   node scripts/ci/build-browser-extension.mjs \
 *     --src <checkout dir> --out <staging dir> --zip <zip path> --kind url|site \
 *     [--browser chrome|firefox]
 */
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, cpSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { writeZipFromDirectory } from './zip-writer.mjs'

const args = new Map()
for (let i = 2; i < process.argv.length; i += 2) {
  args.set(process.argv[i].replace(/^--/, ''), process.argv[i + 1])
}

const src = resolve(args.get('src') ?? '')
const out = resolve(args.get('out') ?? '')
const zipPath = resolve(args.get('zip') ?? '')
const kind = args.get('kind') ?? 'url'
const browser = (args.get('browser') ?? 'chrome').toLowerCase()

if (!src || !out || !zipPath) {
  console.error('usage: --src <dir> --out <dir> --zip <path> [--kind url|site] [--browser chrome|firefox]')
  process.exit(2)
}
if (browser !== 'chrome' && browser !== 'firefox') {
  console.error(`unsupported --browser '${browser}' (expected chrome or firefox)`)
  process.exit(2)
}
if (!existsSync(src)) {
  console.error(`source directory not found: ${src}`)
  process.exit(1)
}

function run(command, commandArgs, options = {}) {
  console.log(`$ ${command} ${commandArgs.join(' ')}`)
  execFileSync(command, commandArgs, {
    cwd: options.cwd ?? src,
    stdio: 'inherit',
    shell: process.platform === 'win32',
    env: { ...process.env, ...(options.env ?? {}) },
  })
}

const pkg = JSON.parse(readFileSync(join(src, 'package.json'), 'utf8'))
console.log(`building ${pkg.name} v${pkg.version} (kind=${kind}, browser=${browser})`)

// 1. Install: prefer a committed lockfile, else the repo's own install script.
const hasLock = existsSync(join(src, 'package-lock.json'))
const hasPureInstall = Boolean(pkg.scripts?.['pure-install'])
if (hasLock) {
  run('npm', ['ci'])
} else if (hasPureInstall) {
  // time-tracker-4-browser: upstream keeps native deps optional and does not
  // commit a lockfile, so its own install script is the supported entry point.
  run('npm', ['run', 'pure-install'])
} else {
  run('npm', ['install'])
}

// 2. Optional typecheck (both forks expose `compile`).
if (pkg.scripts?.compile) {
  run('npm', ['run', 'compile'])
}

// 3. Build. The forks disagree on naming: tracker-web keys the Firefox build off
// VITE_TARGET_BROWSER, time-tracker exposes a dedicated `build:firefox` script.
const buildScript = browser === 'firefox' ? 'build:firefox' : 'build'
if (browser === 'firefox' && !pkg.scripts?.[buildScript]) {
  console.error(`package.json has no '${buildScript}' script; cannot build the Firefox target`)
  process.exit(1)
}
if (browser === 'firefox' && pkg.name === 'pim-watcher-web') {
  // tracker-web only has VITE_TARGET_BROWSER; `build:firefox` exists on both forks
  // today, but keep the env var for the Vite-based fork either way.
  run('npm', ['run', buildScript], { env: { VITE_TARGET_BROWSER: 'firefox' } })
} else {
  run('npm', ['run', buildScript])
}

// 4. Locate build output. Each target has its own directory, so the Firefox build
// must never silently pick up the stale Chrome output.
const candidates = browser === 'firefox'
  ? ['dist_prod_firefox', 'dist_firefox', 'build_firefox', 'build', 'dist_prod', 'dist']
  : ['build', 'dist_prod', 'dist']
const buildDir = candidates.map(d => join(src, d)).find(d => existsSync(join(d, 'manifest.json')))
if (!buildDir) {
  console.error(`no manifest.json found under any of: ${candidates.join(', ')}`)
  process.exit(1)
}
console.log(`build output: ${buildDir}`)

const manifest = JSON.parse(readFileSync(join(buildDir, 'manifest.json'), 'utf8'))

// 5. Regression guards (cf. pim-platform PR #183/#184: the URL-level plugin was
// unusable because the service worker could not register and the extension
// could not read tab URLs).
if (browser === 'firefox') {
  // Firefox cannot load MV3 service workers from an unsigned local build; the
  // forks emit MV2 (`background.scripts` + `browser_action`). Guard that, because
  // shipping the Chrome build under a Firefox label is exactly the #312 defect.
  if (manifest.manifest_version !== 2) {
    console.error(`expected Firefox manifest_version 2, got ${manifest.manifest_version}`)
    process.exit(1)
  }
  if (!Array.isArray(manifest.background?.scripts) || manifest.background.scripts.length === 0) {
    console.error('Firefox manifest background.scripts missing (MV2 event page required)')
    process.exit(1)
  }
  if (manifest.background?.service_worker) {
    console.error('Firefox manifest must not declare background.service_worker')
    process.exit(1)
  }
  if (!manifest.browser_action) {
    console.error('Firefox manifest browser_action missing')
    process.exit(1)
  }
  if (!manifest.browser_specific_settings?.gecko?.id) {
    console.error('Firefox manifest browser_specific_settings.gecko.id missing (required for unsigned install)')
    process.exit(1)
  }
  for (const script of manifest.background.scripts) {
    if (!existsSync(join(buildDir, script))) {
      console.error(`Firefox manifest references a missing background script: ${script}`)
      process.exit(1)
    }
  }
} else if (kind === 'url') {
  if (manifest.manifest_version !== 3) {
    console.error(`expected manifest_version 3, got ${manifest.manifest_version}`)
    process.exit(1)
  }
  if (manifest.background?.type !== 'module') {
    console.error('manifest background.type must be "module" (ESM service worker)')
    process.exit(1)
  }
  const hosts = manifest.host_permissions ?? []
  if (!hosts.includes('<all_urls>')) {
    console.error(`host_permissions must include <all_urls> (got ${JSON.stringify(hosts)})`)
    process.exit(1)
  }
  if (!manifest.action?.default_popup) {
    console.error('manifest action.default_popup missing (popup must be reachable)')
    process.exit(1)
  }
  if (!manifest.options_ui?.page) {
    console.error('manifest options_ui.page missing')
    process.exit(1)
  }
} else {
  // Time Tracker fork: assert the PIM reporting hook is actually bundled.
  // Bundle layout differs between upstream majors, so scan every JS file
  // produced by the build rather than assuming one filename.
  const marker = 'browser/site/heartbeat'
  const bundleHasReporter = (dir) =>
    readdirSync(dir, { withFileTypes: true }).some(entry => {
      const full = join(dir, entry.name)
      if (entry.isDirectory()) return entry.name !== 'node_modules' && bundleHasReporter(full)
      return entry.isFile() && entry.name.endsWith('.js') && readFileSync(full, 'utf8').includes(marker)
    })
  if (!bundleHasReporter(buildDir)) {
    console.error(`PIM site reporter (${marker}) not found in the Time Tracker bundle`)
    process.exit(1)
  }
}

// 6. Stage + zip. The archive is written by our own cross-platform writer: the
// previous Windows path used Compress-Archive, which emits `src\background\main.js`
// entry names that spec-compliant readers (Firefox, unzip) reject outright (#312).
rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
cpSync(buildDir, out, { recursive: true })

mkdirSync(dirname(zipPath), { recursive: true })
rmSync(zipPath, { force: true })

const archive = writeZipFromDirectory(out, zipPath)
const backslashEntries = archive.entryNames.filter(name => name.includes('\\'))
if (backslashEntries.length > 0) {
  console.error(`zip contains backslash entry names: ${backslashEntries.slice(0, 3).join(', ')}`)
  process.exit(1)
}
if (archive.entryCount === 0) {
  console.error('zip is empty; refusing to publish an unusable artifact')
  process.exit(1)
}

console.log(`${manifest.name} v${manifest.version} (kind=${kind}, browser=${browser}) packaged to ${out}`)
console.log(`zip: ${zipPath} (${archive.entryCount} entries, ${archive.bytes} bytes)`)
