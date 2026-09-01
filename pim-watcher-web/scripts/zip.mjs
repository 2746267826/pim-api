// Package the built extension (dist/) into a single zip for manual distribution.
// The dist/ output structure (manifest.json, background/main.js, offscreen.*,
// chunks/*) is produced by `vite build` + `scripts/generate-manifest.mjs`.
import { createWriteStream } from 'fs'
import { readdir } from 'fs/promises'
import { join, relative } from 'path'
import { createRequire } from 'module'

const OUT = 'dist/pim-browser-extension.zip'

async function collect(dir, base) {
  const entries = await readdir(dir, { withFileTypes: true })
  let files = []
  for (const e of entries) {
    const p = join(dir, e.name)
    if (e.isDirectory()) files = files.concat(await collect(p, base))
    else files.push(relative(base, p))
  }
  return files
}

let archiver
try {
  archiver = createRequire(import.meta.url)('archiver')
} catch {}

if (!archiver) {
  // Fallback to the system `zip` binary (available on ubuntu-latest and macOS).
  const { execSync } = await import('child_process')
  try {
    execSync(`cd dist && rm -f pim-browser-extension.zip && zip -r pim-browser-extension.zip . > /dev/null`, { stdio: 'inherit' })
    console.log(`Created ${OUT}`)
  } catch (e) {
    console.log('zip fallback failed, skipping archive creation')
  }
  process.exit(0)
}

const output = createWriteStream(join(process.cwd(), OUT))
const archive = archiver('zip', { zlib: { level: 9 } })
archive.pipe(output)
const files = await collect('dist', 'dist')
for (const f of files) {
  if (f === 'pim-browser-extension.zip') continue
  archive.file(join('dist', f), { name: f })
}
await archive.finalize()
console.log(`Created ${OUT}`)
