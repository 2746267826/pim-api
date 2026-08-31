import { readFileSync, writeFileSync, copyFileSync, mkdirSync, existsSync } from 'fs'
import { resolve, dirname } from 'path'

const root = resolve(dirname(new URL(import.meta.url).pathname), '..')
const pkg = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'))
const manifestSrc = JSON.parse(readFileSync(resolve(root, 'src/manifest.json'), 'utf8'))

const manifest = {
  name: pkg.name,
  description: pkg.description,
  version: pkg.version,
  ...manifestSrc,
}

mkdirSync(resolve(root, 'dist'), { recursive: true })
writeFileSync(resolve(root, 'dist/manifest.json'), JSON.stringify(manifest, null, 2))
console.log('Generated dist/manifest.json')

// Copy static assets
const assets = ['logo-128.png', 'offscreen.html']
for (const asset of assets) {
  const src = resolve(root, `src/${asset}`)
  const dest = resolve(root, `dist/${asset}`)
  if (existsSync(src)) {
    // ensure dest dir exists
    mkdirSync(dirname(dest), { recursive: true })
    copyFileSync(src, dest)
    console.log(`Copied ${asset}`)
  }
}

// Ensure legacy path dist/src/offscreen.html exists for older service worker references
const legacySrc = resolve(root, 'src/offscreen.html')
const legacyDest = resolve(root, 'dist/src/offscreen.html')
if (existsSync(legacySrc) && !existsSync(legacyDest)) {
  mkdirSync(dirname(legacyDest), { recursive: true })
  copyFileSync(legacySrc, legacyDest)
  console.log('Copied src/offscreen.html (legacy)')
}

// offscreen.html handling: ensure both locations reference correct js
// Vite builds offscreen.ts -> dist/offscreen.js, but offscreen.html originally references offscreen.ts
try {
  for (const rel of ['dist/offscreen.html', 'dist/src/offscreen.html']) {
    const p = resolve(root, rel)
    if (!existsSync(p)) continue
    let html = readFileSync(p, 'utf8')
    // Single idempotent replacement
    html = html.replace(/offscreen\.ts/g, 'offscreen.js')
    // Ensure src path is correct for each location
    if (rel === 'dist/src/offscreen.html') {
      // from dist/src/ need to go up one level
      html = html.replace('src="./offscreen.js"', 'src="../offscreen.js"')
    }
    writeFileSync(p, html)
  }
} catch (e) {
  console.warn('Failed to patch offscreen.html:', e)
}
