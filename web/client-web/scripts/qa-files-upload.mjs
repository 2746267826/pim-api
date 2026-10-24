// Isolated frontend proof: real built app + upload engine, fixture API only.
// Run after npm run build: node scripts/qa-files-upload.mjs [output-directory]
// Never loads .env, real credentials, or a remote API. This is not cloud acceptance.
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dist = resolve(root, 'dist')
assert.ok(existsSync(resolve(dist, 'index.html')), 'Run npm run build first')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/files-upload'))
mkdirSync(out, { recursive: true })
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.json': 'application/json' }
const server = createServer((req, res) => {
  const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname)
  const candidate = resolve(dist, `.${pathname}`)
  if (!candidate.startsWith(dist + sep) || pathname.startsWith('/api/')) {
    res.writeHead(404).end()
    return
  }
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', mime[extname(file)] ?? 'application/octet-stream')
  res.end(readFileSync(file))
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`
let browser
const report = { boundary: 'Frontend fixtures only; no real cloud or physical device acceptance.', viewports: [] }
const account = (id, provider) => ({
  id, provider, accountName: id, status: 'connected', isDefault: id === 'fixture-a',
  syncStatus: 'idle', syncedItemCount: 0,
})
const accounts = [account('fixture-a', 'onedrive'), account('fixture-b', 'google')]
const envelope = (data) => ({ code: 0, message: 'fixture', data })
const paged = (items) => ({ items, totalCount: items.length, page: 1, pageSize: 100, totalPages: 1 })

try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => {
      localStorage.setItem('pim.accessToken', 'isolated-fixture-not-a-real-token')
      localStorage.setItem('pim.fileBrowser', JSON.stringify({ path: '/inbox' }))
    })
    const items = []
    const uploads = []
    const violations = []
    let noAccounts = false
    let failNext = false
    let releaseUpload
    const heldUpload = new Promise((r) => { releaseUpload = r })
    await context.route('**/*', async (route) => {
      const request = route.request()
      const url = new URL(request.url())
      if (url.origin !== base) {
        violations.push(`External request blocked: ${url.origin}`)
        return route.abort()
      }
      if (!url.pathname.startsWith('/api/')) return route.continue()
      const reply = (data) => route.fulfill({ json: envelope(data) })
      const path = url.pathname
      if (path === '/api/v1/auth/me') return reply({ id: 'fixture-user', username: 'fixture', displayName: 'Upload fixture', role: 'user' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'fixture', capabilities: [], latestVersion: null } })
      if (path === '/api/v1/status/summary') return reply({ status: 0, label: 'fixture', message: '', checkedAt: new Date().toISOString() })
      if (path === '/api/v1/calendar/calendars' || path === '/api/v1/calendar/task-books') return reply([])
      if (path === '/api/v1/files/providers') return reply(noAccounts ? [] : accounts)
      if (/^\/api\/v1\/files\/providers\/[^/]+\/sync-status$/.test(path)) return reply({ syncStatus: 'idle', syncedItemCount: 0 })
      if (path === '/api/v1/files/items' && request.method() === 'GET') {
        const q = url.searchParams
        const selected = q.get('type') === 'folder' ? [] : items.filter((i) =>
          i.providerId === q.get('providerId') && i.path.slice(0, i.path.lastIndexOf('/')) === (q.get('path') === '/' ? '' : q.get('path')))
        return reply({ result: paged(selected) })
      }
      if (path === '/api/v1/files/items/upload' && request.method() === 'POST') {
        const form = await new Response(request.postDataBuffer(), { headers: { 'Content-Type': request.headers()['content-type'] } }).formData()
        const file = form.get('file')
        const upload = { providerId: form.get('providerId'), path: form.get('path'), name: file.name, bytes: await file.text() }
        uploads.push(upload)
        if (uploads.length === 1) await heldUpload
        if (failNext) {
          failNext = false
          return route.fulfill({ status: 403, json: { code: 403, message: 'fixture: 网盘拒绝写入', data: null } })
        }
        const stamp = new Date().toISOString()
        const item = { id: `fixture-item-${uploads.length}`, providerId: upload.providerId, path: upload.path, name: file.name,
          itemType: 'file', mimeType: 'text/plain', size: file.size, createdAt: stamp, modifiedAt: stamp, syncedAt: stamp, indexStatus: 'indexed' }
        items.push(item)
        return reply(item)
      }
      violations.push(`Unconfigured fixture: ${request.method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'Unconfigured fixture', data: null } })
    })
    const page = await context.newPage()
    page.on('pageerror', (e) => violations.push(`Page error: ${e.message}`))
    const queue = page.getByRole('region', { name: '传输队列', exact: true })
    const uploadButton = page.getByRole('button', { name: '上传文件', exact: true })
    const row = (name) => page.getByRole('button', { name, exact: true })
    const capture = async (state) => {
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true })
      const overflow = await page.evaluate(() => {
        const main = document.querySelector('main')
        return { document: document.documentElement.scrollWidth - innerWidth, main: main.scrollWidth - main.clientWidth }
      })
      assert.ok(overflow.document <= 1 && overflow.main <= 1, `${tag}/${state} horizontal overflow: ${JSON.stringify(overflow)}`)
      if (tag === 'mobile' && await queue.isVisible()) {
        const panel = await queue.boundingBox()
        const fab = await page.getByRole('button', { name: '快捷记录', exact: true }).boundingBox()
        assert.ok(panel.y + panel.height < fab.y, 'Transfer controls must not overlap the global quick-record button')
      }
    }
    await page.goto(`${base}/files`)
    await uploadButton.waitFor({ state: 'visible' })
    await page.getByRole('combobox', { name: '选择网盘账号' }).waitFor()
    assert.equal(await uploadButton.isEnabled(), true)
    await capture('empty')
    // A cancelled picker does not enqueue anything.
    const cancelled = page.waitForEvent('filechooser')
    await uploadButton.click()
    await (await cancelled).setFiles([])
    assert.equal(uploads.length, 0)
    const choosing = page.waitForEvent('filechooser')
    await uploadButton.click()
    await (await choosing).setFiles([
      { name: 'first.txt', mimeType: 'text/plain', buffer: Buffer.from('first upload bytes') },
      { name: 'second.txt', mimeType: 'text/plain', buffer: Buffer.from('second upload bytes') },
    ])
    await queue.getByText('上传中', { exact: true }).waitFor()
    assert.equal(await queue.isVisible(), true)
    assert.equal(await row('first.txt').count(), 0, 'No fabricated list entry before API success')
    await capture('uploading')
    releaseUpload()
    await row('first.txt').waitFor()
    await row('second.txt').waitFor()
    assert.deepEqual(uploads, [
      { providerId: 'fixture-a', path: '/inbox/first.txt', name: 'first.txt', bytes: 'first upload bytes' },
      { providerId: 'fixture-a', path: '/inbox/second.txt', name: 'second.txt', bytes: 'second upload bytes' },
    ])
    await capture('success')
    await queue.getByRole('button', { name: '关闭', exact: true }).click()
    await page.getByRole('button', { name: '传输', exact: true }).click()
    await queue.waitFor({ state: 'visible' })
    await page.getByRole('combobox', { name: '选择网盘账号' }).selectOption('fixture-b')
    failNext = true
    await page.getByLabel('选择上传文件').setInputFiles({ name: 'retry.txt', mimeType: 'text/plain', buffer: Buffer.from('retry bytes') })
    await queue.getByText('fixture: 网盘拒绝写入', { exact: true }).waitFor()
    assert.equal(await row('retry.txt').count(), 0)
    await capture('failure')
    await queue.getByRole('button', { name: '重试', exact: true }).click()
    await row('retry.txt').waitFor()
    assert.equal(uploads.length, 4)
    for (const upload of uploads.slice(2)) assert.deepEqual(upload, { providerId: 'fixture-b', path: '/retry.txt', name: 'retry.txt', bytes: 'retry bytes' })
    await capture('retry-success')
    noAccounts = true
    await page.reload()
    await page.getByText('尚未连接网盘', { exact: true }).waitFor()
    assert.equal(await uploadButton.isDisabled(), true)
    await page.getByRole('button', { name: '去连接网盘', exact: true }).waitFor()
    await capture('no-account')
    assert.deepEqual(violations, [])
    report.viewports.push({ tag, viewport, uploads: uploads.length, states: ['empty', 'uploading', 'success', 'failure', 'retry-success', 'no-account'], pass: true })
    await context.close()
    console.log(`PASS ${tag}: chooser, cancel, multipart bytes/account/path, pending, success, retry, no-account, no overflow`)
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally {
  await browser?.close()
  await new Promise((r) => server.close(r))
}
