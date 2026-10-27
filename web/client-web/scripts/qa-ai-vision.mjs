// Built UI with isolated synthetic APIs/images. No .env, live model, private photo or cloud storage.
// npm run build && node scripts/qa-ai-vision.mjs [output-directory]
import assert from 'node:assert/strict'
import { randomUUID, createHash } from 'node:crypto'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..'), dist = resolve(root, 'dist')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/ai-vision')); mkdirSync(out, { recursive: true })
assert.ok(existsSync(resolve(dist, 'index.html')), 'Build first')
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.json': 'application/json' }
const server = createServer((req, res) => {
  const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname), candidate = resolve(dist, `.${path}`)
  if (!candidate.startsWith(dist + sep) || path.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', mime[extname(file)] ?? 'application/octet-stream'); res.end(readFileSync(file))
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`, user = '11111111-1111-4111-8111-111111111111'
const pngs = ['iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8oKHBwMDAxMDAwMDAAAAP8gFEwVJxKAAAAABJRU5ErkJggg==', 'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPUCPjAwMDAxMDAwMDAAAAQQgFsd1MS4gAAAABJRU5ErkJggg==']
const buffers = pngs.map((p) => Buffer.from(p, 'base64'))
const prompt = '结合照片与手写的全部内容\npassword=普通材料\n保留第二行', system = '请检查本次全部图片', reply = '完整视觉回执（合成）\n照片与手写分别保留。'
const report = { boundary: 'Real built UI + isolated fixture APIs and generated red/blue PNGs. SDK/local HTTP/files are tested separately. No real model or private photo.', viewports: [] }
let browser
try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop', context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'isolated-vision-fixture-not-a-token'))
    let model = { id: randomUUID(), revision: randomUUID(), name: '课堂视觉模型', model: 'synthetic-vision', baseUrl: 'https://synthetic-model.invalid/v1/',
      capabilities: ['Vision'], enabled: true, hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024, createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }
    const settings = { revision: randomUUID(), enabled: true, textModelId: null, visionModelId: model.id, transcriptionModelId: null }
    const records = [], violations = []; let calls = 0, changedImage = false, releaseCall
    const held = new Promise((r) => { releaseCall = r })
    const images = buffers.map((bytes, index) => ({ id: randomUUID(), index, name: index === 0 ? '照片.png' : '手写.png', contentType: 'image/png', sizeBytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'), stored: true }))
    await context.route('**/*', async (route) => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname
      if (url.origin !== base) { violations.push('external origin'); return route.abort() }
      if (!path.startsWith('/api/')) return route.continue()
      const ok = (data) => route.fulfill({ json: { code: 0, message: 'synthetic', data } })
      if (path === '/api/v1/auth/me') return ok({ id: user, username: 'synthetic-admin', displayName: '视觉验证', role: 'admin' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'synthetic', capabilities: [] } })
      if (path === '/api/v1/status/summary') return ok({ status: 0, label: 'synthetic', checkedAt: new Date().toISOString() })
      if (['/api/v1/calendar/calendars', '/api/v1/calendar/task-books'].includes(path)) return ok([])
      if (path === '/api/v1/ai/models') return ok([model])
      if (path === '/api/v1/ai/model-settings') return ok(settings)
      if (path === '/api/v1/ai/vision-calls' && request.method() === 'POST') {
        calls++; const b = request.postDataJSON()
        assert.equal(b.expectedUserId, user); assert.equal(b.prompt, prompt); assert.equal(b.systemPrompt, system)
        assert.equal(b.imageCount, 2); assert.deepEqual(b.images.map((i) => i.base64), pngs)
        assert.deepEqual(b.consent, { modelId: model.id, modelRevision: model.revision, endpoint: model.baseUrl, confirmed: true })
        const row = { id: randomUUID(), userId: user, clientRequestId: b.clientRequestId, module: 'system', purpose: 'ai.vision-call', model: model.model, modelProfileName: model.name,
          modelEndpoint: model.baseUrl, modelRevision: model.revision, modelProfileId: model.id, status: 'Running', startedAt: '2026-10-27T00:00:00Z', responseText: null,
          requestMessagesJson: JSON.stringify([{ role: 'system', content: b.systemPrompt }, { role: 'user', content: [{ type: 'text', text: b.prompt }, ...images.map((i) => ({ type: 'image_url', image_url: { url: `pim-ai-image:${i.id}` } }))] }]),
          requestPayloadJson: '{}', responseRawJson: '{}', usage: {}, finishReason: null }
        records.push(row); await held
        row.status = 'Succeeded'; row.responseText = reply; row.finishReason = 'stop'; row.durationMs = 123; row.usage = { promptTokens: 12, completionTokens: 8, totalTokens: 20 }; row.totalTokens = 20
        records.push({ ...row, id: randomUUID(), userId: randomUUID(), modelProfileName: '另一账号的视觉调用' })
        return ok({ logId: row.id, status: row.status, responseText: row.responseText, replayed: false })
      }
      if (path === '/api/v1/ai/requests') return ok({ items: records, totalCount: records.length, page: 1, pageSize: 20, totalPages: 1 })
      if (path === '/api/v1/ai/usage/summary') return ok({ requestCount: 2, successCount: 2, failureCount: 0, runningCount: 0, promptTokens: 24, completionTokens: 16, totalTokens: 40,
        estimatedCost: 0, unknownTokenUsageCount: 0, knownCostRequestCount: 0, averageDurationMs: 123, byModel: [], byModule: [], byPurpose: [], byStatus: [] })
      const record = records.find((r) => path.startsWith(`/api/v1/ai/requests/${r.id}`))
      if (record && path === `/api/v1/ai/requests/${record.id}`) return ok(record)
      if (record && path.endsWith('/images')) {
        if (record.userId !== user) { violations.push('other owner image query'); return route.fulfill({ status: 404, json: { code: 40401, message: 'private' } }) }
        return ok(images)
      }
      const index = images.findIndex((i) => path.endsWith('/images/' + i.id))
      if (index >= 0 && record?.userId === user) {
        if (changedImage && index === 0) return route.fulfill({ status: 409, json: { code: 40901, message: '图片已改变' } })
        return route.fulfill({ contentType: 'image/png', body: buffers[index], headers: { 'Cache-Control': 'private, no-store' } })
      }
      violations.push(`unconfigured ${request.method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'unconfigured' } })
    })
    const page = await context.newPage(); page.on('pageerror', (e) => violations.push(e.message))
    const shot = async (name) => {
      await page.waitForFunction(() => !document.getAnimations().some((a) => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().endTime)), undefined, { timeout: 5000 })
      await page.screenshot({ path: resolve(out, `${tag}-${name}.png`), fullPage: true, animations: 'disabled' })
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1 && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth + 1), `${tag}/${name} overflow`)
    }
    try {
      await page.goto(base + '/settings/ai'); await page.getByText('图片与正文', { exact: true }).click()
      await page.getByRole('heading', { name: '多图视觉调用', exact: true }).waitFor(); await shot('empty')
      await page.getByLabel('本次图片', { exact: true }).setInputFiles(buffers.map((buffer, index) => ({ buffer, name: images[index].name, mimeType: 'image/png' })))
      await page.getByLabel('正文与图片问题', { exact: true }).fill(prompt); await page.getByLabel('视觉系统提示（可选）', { exact: true }).fill(system)
      await page.waitForFunction(() => [...document.querySelectorAll('img[alt^="待发送图片"]')].length === 2 && [...document.querySelectorAll('img[alt^="待发送图片"]')].every((i) => i.complete && i.naturalWidth === 2))
      assert.ok(await page.getByRole('button', { name: '发送图片并记录', exact: true }).isDisabled()); assert.equal(calls, 0); await shot('selected-unconfirmed')
      const consent = page.getByRole('checkbox', { name: '确认将本次图片发送到上述模型地址', exact: true })
      await consent.check(); model = { ...model, revision: randomUUID(), baseUrl: 'https://changed-synthetic-model.invalid/v1/' }
      await page.getByRole('button', { name: '刷新', exact: true }).click(); await page.getByText(model.baseUrl, { exact: true }).last().waitFor()
      assert.equal(await consent.isChecked(), false); assert.ok(await page.getByRole('button', { name: '发送图片并记录', exact: true }).isDisabled()); await shot('destination-changed')
      await consent.check(); await page.getByRole('button', { name: '发送图片并记录', exact: true }).click()
      await page.getByRole('button', { name: '取消视觉等待', exact: true }).waitFor(); await shot('waiting')
      await page.getByText('调用记录', { exact: true }).click(); await page.getByRole('button', { name: /课堂视觉模型/ }).waitFor(); await shot('history-running')
      releaseCall(); await page.getByRole('button', { name: /课堂视觉模型/ }).click()
      let dialog = page.getByRole('dialog'); await dialog.getByLabel('实际回复', { exact: true }).waitFor()
      await page.waitForFunction((expected) => document.getElementById('ai-record-reply')?.value === expected, reply)
      await shot('detail-text')
      await page.waitForFunction(() => [...document.querySelectorAll('img[alt^="本次输入图"]')].length === 2 && [...document.querySelectorAll('img[alt^="本次输入图"]')].every((i) => i.complete && i.naturalWidth === 2))
      const thumbnails = dialog.locator('img[alt^="本次输入图"]')
      await thumbnails.first().scrollIntoViewIfNeeded(); await shot('detail-images')
      await thumbnails.last().scrollIntoViewIfNeeded(); await shot('detail-second-image')
      const box = await thumbnails.last().boundingBox(), close = await dialog.getByRole('button', { name: '关闭', exact: true }).first().boundingBox()
      assert.ok(box && close && box.y + box.height <= close.y + 2 && box.y >= 0, 'Second input image must be reachable above the dialog footer')
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click()
      await page.getByRole('button', { name: /另一账号的视觉调用/ }).waitFor(); await page.getByRole('button', { name: /另一账号的视觉调用/ }).click()
      dialog = page.getByRole('dialog'); await dialog.getByText('原始图片仅调用发起账号可查看。', { exact: true }).waitFor(); await shot('other-owner')
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click(); changedImage = true
      await page.getByRole('button', { name: /课堂视觉模型/ }).click(); dialog = page.getByRole('dialog')
      await dialog.getByText(/图片无法读取或解码/).waitFor(); await dialog.getByText(/图片无法读取或解码/).scrollIntoViewIfNeeded(); await shot('material-changed')
      assert.equal(await dialog.getByLabel('实际回复', { exact: true }).inputValue(), reply)
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click(); await page.getByText('统计', { exact: true }).click(); await page.getByText('100.0%', { exact: true }).waitFor(); await shot('statistics')
      assert.equal(calls, 1); assert.deepEqual(violations, [])
      report.viewports.push({ tag, viewport, states: 11, modelCalls: calls, twoDecodedImages: true, imagesReachableAboveFooter: true, destinationChangeRevokesConsent: true, ownerOnly: true, changedImageVisible: true, pass: true })
      console.log(`PASS ${tag}: two PNGs, explicit destination consent, one call, immutable history, owner-only images, changed-file error, no overflow`)
    } catch (error) { await page.screenshot({ path: resolve(out, `${tag}-failure.png`), fullPage: true }); throw error }
    finally { releaseCall(); await context.close() }
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally { await browser?.close(); await new Promise((r) => server.close(r)) }
