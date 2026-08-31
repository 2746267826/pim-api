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

// offscreen.html handling: ensure dist/src/offscreen.html exists for service worker
const offscreenSrc = resolve(root, 'src/offscreen.html')
const offscreenDist = resolve(root, 'dist/src/offscreen.html')
if (existsSync(offscreenSrc)) {
  mkdirSync(dirname(offscreenDist), { recursive: true })
  copyFileSync(offscreenSrc, offscreenDist)
  console.log('Copied src/offscreen.html')
}

// Also ensure dist/offscreen.html references correct js if needed
// The vite build outputs offscreen.js at dist/offscreen.js, but offscreen.html expects ./offscreen.ts
// We copy and keep html as is; browser will load via script tag that vite should have transformed?
// Our manual html is simple and loads offscreen.ts via type=module, but dist version should load offscreen.js
// Patch html to point to ../offscreen.js or ./offscreen.js accordingly
try {
  const htmlPaths = [resolve(root, 'dist/offscreen.html'), resolve(root, 'dist/src/offscreen.html')]
  for (const p of htmlPaths) {
    if (existsSync(p)) {
      let html = readFileSync(p, 'utf8')
      // Replace src reference to offscreen.ts with offscreen.js
      html = html.replace('src="./offscreen.ts"', 'src="../offscreen.js"').replace('src="./offscreen.js"', 'src="../offscreen.js"')
      // For dist/offscreen.html case
      if (p.endsWith('dist/offscreen.html')) {
        html = readFileSync(p, 'utf8').replace('src="./offscreen.ts"', 'src="./offscreen.js"')
      }
      // simpler: ensure it loads offscreen.js from dist root
      if (html.includes('offscreen.ts')) {
        html = html.replace(/offscreen\.ts/g, 'offscreen.js')
      }
      writeFileSync(p, html)
    }
  }
} catch (e) {
  console.warn('Failed to patch offscreen.html:', e)
}
