// Real built UI + isolated fixture API. No .env, live credentials, cloud API, or model calls.
// npm run build && node scripts/qa-ai-text.mjs [output-directory]
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dist = resolve(root, 'dist')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/ai-text'))
assert.ok(existsSync(resolve(dist, 'index.html')), 'Build first')
mkdirSync(out, { recursive: true })
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.json': 'application/json' }
const server = createServer((req, res) => {
  const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname)
  const candidate = resolve(dist, `.${pathname}`)
  if (!candidate.startsWith(dist + sep) || pathname.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', mime[extname(file)] ?? 'application/octet-stream'); res.end(readFileSync(file))
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`
const user = '11111111-1111-4111-8111-111111111111'
const envelope = (data) => ({ code: 0, message: 'fixture', data })
const report = { boundary: 'Built UI with synthetic API fixtures only. Actual SDK/local HTTP is tested separately by backend tests. No real model/cloud/device acceptance.', viewports: [] }
let browser
try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'isolated-ai-fixture-not-a-token'))
    let profiles = []
    let config = { revision: '00000000-0000-0000-0000-000000000000', enabled: false, textModelId: null, visionModelId: null, transcriptionModelId: null }
    const records = []
    const violations = []
    let calls = 0; let conflict = false; let releaseCall
    const held = new Promise((r) => { releaseCall = r })
    const prompt = '第一行：完整上下文\npassword=普通材料\n第二行：保留原始换行'
    const system = '请使用全部上下文回答'
    const replyText = '完整回执\n这是第二行，保持原文。'
    await context.route('**/*', async (route) => {
      const request = route.request(); const url = new URL(request.url()); const path = url.pathname
      if (url.origin !== base) { violations.push('External request blocked: ' + url.origin); return route.abort() }
      if (!path.startsWith('/api/')) return route.continue()
      const reply = (data) => route.fulfill({ json: envelope(data) })
      if (path === '/api/v1/auth/me') return reply({ id: user, username: 'fixture-admin', displayName: 'AI 验证', role: 'admin' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'fixture', capabilities: [], latestVersion: null } })
      if (path === '/api/v1/status/summary') return reply({ status: 0, label: 'fixture', message: '', checkedAt: new Date().toISOString() })
      if (path === '/api/v1/calendar/calendars' || path === '/api/v1/calendar/task-books') return reply([])
      if (path === '/api/v1/ai/models' && request.method() === 'GET') return reply(profiles)
      if (path === '/api/v1/ai/models' && request.method() === 'POST') {
        const b = request.postDataJSON()
        assert.equal(b.apiKey, 'fake-ui-model-key'); assert.deepEqual(b.capabilities, ['Text', 'Vision'])
        profiles.push({ id: randomUUID(), revision: randomUUID(), name: b.name, baseUrl: b.baseUrl.replace(/\/$/, '') + '/', model: b.model,
          enabled: b.enabled, capabilities: b.capabilities, hasApiKey: true, timeoutSeconds: b.timeoutSeconds, maxOutputTokens: b.maxOutputTokens,
          createdAt: '2026-10-27T03:00:00Z', updatedAt: '2026-10-27T03:00:00Z' })
        return reply(profiles.at(-1))
      }
      if (path.startsWith('/api/v1/ai/models/') && request.method() === 'PUT') {
        if (conflict) { conflict = false; return route.fulfill({ status: 409, json: { code: 40901, message: '配置已被修改，请刷新核对后再保存', data: null } }) }
        const b = request.postDataJSON(); const p = profiles.find((v) => v.id === path.split('/').at(-1))
        assert.equal(b.expectedRevision, p.revision); assert.equal(b.apiKey, null)
        p.name = b.name; p.revision = randomUUID(); return reply(p)
      }
      if (path.startsWith('/api/v1/ai/models/') && request.method() === 'DELETE') {
        const id = path.split('/').at(-1); const p = profiles.find((v) => v.id === id)
        assert.equal(url.searchParams.get('revision'), p.revision)
        assert.notEqual(config.textModelId, id); assert.notEqual(config.visionModelId, id)
        profiles = profiles.filter((v) => v.id !== id); return reply(true)
      }
      if (path === '/api/v1/ai/model-settings') {
        if (request.method() === 'GET') return reply(config)
        const b = request.postDataJSON()
        assert.deepEqual(Object.keys(b).sort(), ['enabled', 'expectedRevision', 'textModelId', 'transcriptionModelId', 'visionModelId'].sort())
        assert.equal(b.expectedRevision, config.revision)
        config = { revision: randomUUID(), enabled: b.enabled, textModelId: b.textModelId, visionModelId: b.visionModelId, transcriptionModelId: b.transcriptionModelId }
        return reply(config)
      }
      if (path === '/api/v1/ai/text-calls' && request.method() === 'POST') {
        const b = request.postDataJSON(); calls++
        assert.equal(b.expectedUserId, user); assert.equal(b.prompt, prompt); assert.equal(b.systemPrompt, system); assert.equal(b.modelId, null)
        assert.ok(config.enabled); const model = profiles.find((p) => p.id === config.textModelId)
        const row = { id: randomUUID(), clientRequestId: b.clientRequestId, userId: user, module: 'system', purpose: 'ai.text-call', sourceObjectType: 'ai-model', sourceObjectId: model.id,
          model: model.model, provider: 'openai-compatible', modelProfileId: model.id, modelProfileName: model.name, modelEndpoint: model.baseUrl, modelRevision: model.revision,
          startedAt: '2026-10-27T03:00:00Z', status: 'Running', correlationId: randomUUID(), attemptNumber: 1, maxAttempts: 1,
          requestMessagesJson: JSON.stringify([{ role: 'system', content: b.systemPrompt }, { role: 'user', content: b.prompt }]), requestPayloadJson: '{}', responseRawJson: '{}',
          responseText: null, errorMessage: null, contentRedacted: false, usage: {}, totalTokens: null, estimatedCost: null, durationMs: null }
        records.push(row); await held
        row.status = 'Succeeded'; row.responseText = replyText; row.finishReason = 'stop'; row.usage = { promptTokens: 7, completionTokens: 11, totalTokens: 18, estimatedCost: null, currency: null }; row.totalTokens = 18; row.durationMs = 123
        return reply({ logId: row.id, status: row.status, responseText: row.responseText, errorMessage: null, replayed: false })
      }
      if (path === '/api/v1/ai/requests') return reply({ items: records, totalCount: records.length, page: 1, pageSize: 20, totalPages: 1 })
      if (path.startsWith('/api/v1/ai/requests/')) return reply(records.find((r) => r.id === path.split('/').at(-1)))
      if (path === '/api/v1/ai/usage/summary') return reply({ requestCount: 1, successCount: 1, failureCount: 0, runningCount: 0, promptTokens: 7, completionTokens: 11, totalTokens: 18,
        estimatedCost: 0, unknownTokenUsageCount: 0, knownCostRequestCount: 0, averageDurationMs: 123, byModule: [], byPurpose: [], byStatus: [], byModel: [{ groupKey: 'fixture-model', requestCount: 1, successCount: 1, runningCount: 0, totalTokens: 18 }] })
      violations.push(`Unconfigured fixture: ${request.method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'Unconfigured fixture', data: null } })
    })
    const page = await context.newPage()
    page.on('pageerror', (error) => violations.push(error.message))
    page.on('requestfailed', (r) => { if (r.url().includes('/ai/text-calls') && r.method() === 'POST') violations.push('Unexpected call abort on tab navigation') })
    const shot = async (state) => {
      // Capture the settled UI, rather than a partly transparent dialog or moving tab pill.
      await page.waitForFunction(() => !document.getAnimations().some((a) =>
        a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().endTime)), undefined, { timeout: 5000 })
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true, animations: 'disabled' })
      const overflow = await page.evaluate(() => ({ document: document.documentElement.scrollWidth - innerWidth, main: document.querySelector('main').scrollWidth - document.querySelector('main').clientWidth }))
      assert.ok(overflow.document <= 1 && overflow.main <= 1, `${tag}/${state} overflow ${JSON.stringify(overflow)}`)
    }
    const select = async (label, option) => { await page.getByRole('combobox', { name: label, exact: true }).click(); await page.getByRole('option', { name: option, exact: true }).click() }
    try {
      await page.goto(`${base}/settings/ai`)
      await page.getByText('添加第一个模型', { exact: true }).waitFor(); await shot('empty')
      await page.getByRole('button', { name: '添加模型', exact: true }).click()
      let dialog = page.getByRole('dialog')
      await dialog.getByLabel('配置名称', { exact: true }).fill('课堂文字模型')
      await dialog.getByLabel('API 根地址', { exact: true }).fill('https://model.fixture/v1')
      await dialog.getByLabel('模型标识', { exact: true }).fill('fixture-model')
      await dialog.getByLabel('API Key', { exact: true }).fill('fake-ui-model-key')
      await dialog.getByLabel('视觉', { exact: true }).check(); await shot('model-form')
      await dialog.getByRole('button', { name: '保存模型', exact: true }).click(); await dialog.waitFor({ state: 'hidden' })
      await select('默认文本模型', '课堂文字模型'); await select('默认视觉模型', '课堂文字模型')
      await page.getByRole('switch', { name: '启用 SDK 直连', exact: true }).click()
      await page.getByRole('button', { name: '编辑 课堂文字模型', exact: true }).waitFor()
      await page.getByLabel('系统提示（可选）', { exact: true }).fill(system)
      await page.getByLabel('正文与上下文', { exact: true }).fill(prompt)
      await page.getByRole('button', { name: '发送并记录', exact: true }).click()
      await page.getByRole('button', { name: '取消等待', exact: true }).waitFor(); await shot('calling')
      await page.getByText('调用记录', { exact: true }).click()
      await page.getByRole('button', { name: /课堂文字模型/ }).waitFor(); await shot('history-running')
      releaseCall()
      await page.getByText('模型设置', { exact: true }).click()
      await page.getByLabel('实际模型回复', { exact: true }).waitFor()
      assert.equal(await page.getByLabel('实际模型回复', { exact: true }).textContent(), replyText)
      await page.getByLabel('实际模型回复', { exact: true }).scrollIntoViewIfNeeded()
      await shot('reply')
      await page.getByRole('button', { name: '查看本次完整记录', exact: true }).click()
      dialog = page.getByRole('dialog'); await dialog.getByLabel('实际回复', { exact: true }).waitFor()
      assert.equal(await dialog.getByLabel('实际回复', { exact: true }).inputValue(), replyText)
      assert.deepEqual(JSON.parse(await dialog.getByLabel('实际请求消息／上下文', { exact: true }).inputValue()), [{ role: 'system', content: system }, { role: 'user', content: prompt }])
      await shot('detail'); await dialog.getByRole('button', { name: '关闭', exact: true }).first().click()
      await page.getByText('统计', { exact: true }).click(); await page.getByText('100.0%', { exact: true }).waitFor(); await shot('statistics')
      await page.getByText('模型设置', { exact: true }).click(); await page.getByRole('button', { name: '编辑 课堂文字模型', exact: true }).click()
      dialog = page.getByRole('dialog'); assert.equal(await dialog.getByLabel('API Key（已保存）', { exact: true }).inputValue(), '')
      await dialog.getByLabel('配置名称', { exact: true }).fill('冲突草稿')
      conflict = true; await dialog.getByRole('button', { name: '保存模型', exact: true }).click()
      await page.getByText('配置已被修改，请刷新核对后再保存', { exact: true }).waitFor()
      assert.equal(await dialog.getByLabel('配置名称', { exact: true }).inputValue(), '冲突草稿'); await shot('conflict')
      await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' })
      await select('默认文本模型', '未选择'); await select('默认视觉模型', '未选择')
      await page.getByRole('button', { name: '删除 课堂文字模型', exact: true }).click()
      await page.getByRole('dialog').getByRole('button', { name: '删除配置', exact: true }).click()
      await page.getByText('添加第一个模型', { exact: true }).waitFor()
      await page.getByText('调用记录', { exact: true }).click(); await page.getByRole('button', { name: /课堂文字模型/ }).click()
      assert.equal(await page.getByRole('dialog').getByLabel('实际回复', { exact: true }).inputValue(), replyText)
      await shot('history-after-model-delete')
      assert.equal(calls, 1); assert.deepEqual(violations, [])
      report.viewports.push({ tag, viewport, fixtureCallRequests: calls, states: 9, pendingCallSurvivedTabSwitch: true, exactPromptReply: true, historyRetainedAfterDelete: true, pass: true })
      console.log(`PASS ${tag}: settings/call/history/statistics, exact content, conflict, retained history, no overflow`)
    } catch (error) { await page.screenshot({ path: resolve(out, `${tag}-failure.png`), fullPage: true }); throw error }
    finally { releaseCall(); await context.close() }
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally { await browser?.close(); await new Promise((r) => server.close(r)) }
