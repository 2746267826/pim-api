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
 * Usage:
 *   node scripts/ci/build-browser-extension.mjs \
 *     --src <checkout dir> --out <staging dir> --zip <zip path> --kind url|site
 */
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, cpSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

const args = new Map()
for (let i = 2; i < process.argv.length; i += 2) {
  args.set(process.argv[i].replace(/^--/, ''), process.argv[i + 1])
}

const src = resolve(args.get('src') ?? '')
const out = resolve(args.get('out') ?? '')
const zipPath = resolve(args.get('zip') ?? '')
const kind = args.get('kind') ?? 'url'

if (!src || !out || !zipPath) {
  console.error('usage: --src <dir> --out <dir> --zip <path> [--kind url|site]')
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

function tryRun(command, commandArgs) {
  try {
    execFileSync(command, commandArgs, { cwd: src, stdio: 'pipe', shell: process.platform === 'win32' })
    return true
  } catch {
    return false
  }
}

const pkg = JSON.parse(readFileSync(join(src, 'package.json'), 'utf8'))
console.log(`building ${pkg.name} v${pkg.version} (kind=${kind})`)

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

// 3. Build.
run('npm', ['run', 'build'])

// 4. Locate build output: tracker-web emits build/, Time Tracker fork dist_prod/.
const candidates = ['build', 'dist_prod', 'dist']
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
if (kind === 'url') {
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

// 6. Stage + zip.
rmSync(out, { recursive: true, force: true })
mkdirSync(out, { recursive: true })
cpSync(buildDir, out, { recursive: true })

mkdirSync(dirname(zipPath), { recursive: true })
rmSync(zipPath, { force: true })
if (process.platform === 'win32') {
  run('powershell', [
    '-NoProfile',
    '-Command',
    `Compress-Archive -Path '${out}\\*' -DestinationPath '${zipPath}' -Force`,
  ])
} else {
  run('bash', ['-c', `cd '${out}' && zip -FS -r '${zipPath}' . > /dev/null`])
}

console.log(`${manifest.name} v${manifest.version} (kind=${kind}) packaged to ${out}`)
console.log(`zip: ${zipPath}`)
