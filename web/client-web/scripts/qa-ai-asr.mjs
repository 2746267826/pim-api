// Real built UI, synthetic WAV and isolated API fixtures. No .env, real key, private audio or external network.
// npm run build && node scripts/qa-ai-asr.mjs [output-directory]
import assert from 'node:assert/strict'
import { randomUUID, createHash } from 'node:crypto'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..'), dist = resolve(root, 'dist')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/ai-asr')); mkdirSync(out, { recursive: true })
const server = createServer((req, res) => {
  const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname), candidate = resolve(dist, `.${path}`)
  if (!candidate.startsWith(dist + sep) || path.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[extname(file)] ?? 'application/octet-stream'); res.end(readFileSync(file))
})
assert.ok(existsSync(resolve(dist, 'index.html')), 'Build first')
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`, user = '11111111-1111-4111-8111-111111111111'
const wave = Buffer.alloc(364); wave.write('RIFF', 0); wave.writeUInt32LE(356, 4); wave.write('WAVEfmt ', 8); wave.writeUInt32LE(16, 16); wave.writeUInt16LE(1, 20); wave.writeUInt16LE(1, 22)
wave.writeUInt32LE(16000, 24); wave.writeUInt32LE(32000, 28); wave.writeUInt16LE(2, 32); wave.writeUInt16LE(16, 34); wave.write('data', 36); wave.writeUInt32LE(320, 40)
const report = { boundary: 'Built UI with isolated APIs and synthetic PCM WAV only; no real model/private audio. SDK/Files HTTP and real model tests recorded separately.', viewports: [] }
let browser
try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop', context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'synthetic-asr-not-a-token'))
    const preset = { id: 'mimo-asr', version: '1', name: 'MiMo 语音识别', defaultBaseUrl: 'https://synthetic-asr.invalid/v1/', operationPath: 'chat/completions',
      defaultModel: 'mimo-v2.5-asr', models: ['mimo-v2.5-asr'], formats: ['wav', 'mp3'], languages: ['auto', 'zh', 'en'], maxAudioBytes: 4 * 1024 * 1024, streaming: false }
    let profiles = [], settings = { revision: randomUUID(), enabled: false, textModelId: null, visionModelId: null, transcriptionModelId: null }
    const records = [], violations = []; let calls = 0, changedAudio = false, releaseCall
    const held = new Promise((r) => { releaseCall = r })
    const audio = { id: randomUUID(), name: '合成语音.wav', contentType: 'audio/wav', sizeBytes: wave.length, sha256: createHash('sha256').update(wave).digest('hex'), stored: true }
    const transcript = '合成转写回执\n保留全部内容。'
    await context.route('**/*', async (route) => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname
      if (url.origin !== base) { violations.push('External request'); return route.abort() }
      if (!path.startsWith('/api/')) return route.continue()
      const ok = (data) => route.fulfill({ json: { code: 0, message: 'synthetic', data } })
      if (path === '/api/v1/auth/me') return ok({ id: user, username: 'synthetic-admin', displayName: 'ASR验证', role: 'admin' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'synthetic', capabilities: [] } })
      if (path === '/api/v1/status/summary') return ok({ status: 0, label: 'synthetic', checkedAt: new Date().toISOString() })
      if (['/api/v1/calendar/calendars', '/api/v1/calendar/task-books'].includes(path)) return ok([])
      if (path === '/api/v1/ai/asr-presets') return ok([preset])
      if (path === '/api/v1/ai/models') {
        if (request.method() === 'GET') return ok(profiles)
        const body = request.postDataJSON(); assert.equal(body.asrPresetId, preset.id); assert.deepEqual(body.capabilities, ['Transcription']); assert.equal(body.apiKey, 'synthetic-asr-ui-key')
        profiles.push({ ...body, id: randomUUID(), revision: randomUUID(), baseUrl: body.baseUrl.replace(/chat\/completions\/?$/, ''), hasApiKey: true,
          createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }); delete profiles[0].apiKey; return ok(profiles[0])
      }
      if (path === '/api/v1/ai/model-settings') {
        if (request.method() === 'GET') return ok(settings)
        const body = request.postDataJSON(); assert.equal(body.expectedRevision, settings.revision)
        settings = { ...body, revision: randomUUID() }; return ok(settings)
      }
      if (path === '/api/v1/ai/asr-calls') {
        calls++; const b = request.postDataJSON(), model = profiles[0]
        assert.equal(b.expectedUserId, user); assert.equal(b.audio.base64, wave.toString('base64')); assert.equal(b.audio.format, 'wav'); assert.equal(b.language, 'zh')
        assert.equal(b.prompt, undefined); assert.deepEqual(b.consent, { modelId: model.id, modelRevision: model.revision, endpoint: model.baseUrl + preset.operationPath, presetId: preset.id, presetVersion: preset.version, confirmed: true })
        const row = { id: randomUUID(), userId: user, clientRequestId: b.clientRequestId, module: 'system', purpose: 'ai.asr-call', model: model.model, modelProfileName: model.name,
          modelEndpoint: b.consent.endpoint, modelRevision: model.revision, modelProfileId: model.id, startedAt: '2026-10-27T00:00:00Z', status: 'Running', responseText: null,
          requestMessagesJson: JSON.stringify([{ role: 'user', content: [{ type: 'input_audio', input_audio: { data: `pim-ai-audio:${audio.id}`, format: 'wav' } }] }]), requestPayloadJson: '{}', responseRawJson: '{}', usage: {},
          metadataJson: JSON.stringify({ asrPresetId: preset.id, asrPresetVersion: preset.version, language: b.language, reportedAudioSeconds: 0.01 }) }
        records.push(row); await held; row.status = 'Succeeded'; row.responseText = transcript; row.finishReason = 'stop'; row.durationMs = 123; row.usage = { promptTokens: 10, completionTokens: 5, totalTokens: 15 }; row.totalTokens = 15
        records.push({ ...row, id: randomUUID(), userId: randomUUID(), modelProfileName: '另一账号的ASR调用' })
        return ok({ logId: row.id, status: row.status, responseText: row.responseText, errorMessage: null, replayed: false })
      }
      if (path === '/api/v1/ai/requests') return ok({ items: records, totalCount: records.length, page: 1, pageSize: 20, totalPages: 1 })
      if (path === '/api/v1/ai/usage/summary') return ok({ requestCount: 2, successCount: 2, failureCount: 0, runningCount: 0, promptTokens: 20, completionTokens: 10, totalTokens: 30,
        estimatedCost: 0, unknownTokenUsageCount: 0, knownCostRequestCount: 0, averageDurationMs: 123, byModel: [], byModule: [], byPurpose: [], byStatus: [] })
      const row = records.find((r) => path.startsWith(`/api/v1/ai/requests/${r.id}`))
      if (row && path === `/api/v1/ai/requests/${row.id}`) return ok(row)
      if (row && path.endsWith('/audio')) {
        if (row.userId !== user) { violations.push('Other owner media query'); return route.fulfill({ status: 404 }) }
        return ok(audio)
      }
      if (row?.userId === user && path.endsWith('/audio/content')) return changedAudio
        ? route.fulfill({ status: 409, json: { code: 40901, message: '音频已改变' } })
        : route.fulfill({ contentType: 'audio/wav', body: wave, headers: { 'Cache-Control': 'private, no-store' } })
      violations.push(`Unconfigured ${request.method()} ${path}`); return route.fulfill({ status: 501, json: { code: 501, message: 'unconfigured' } })
    })
    const page = await context.newPage(); page.on('pageerror', (error) => violations.push(error.message))
    const shot = async (state) => {
      await page.waitForFunction(() => !document.getAnimations().some((a) => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().endTime)), undefined, { timeout: 5000 })
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true, animations: 'disabled' })
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1 && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth + 1), `${tag}/${state} overflow`)
    }
    const select = async (label, option) => { await page.getByRole('combobox', { name: label, exact: true }).click(); await page.getByRole('option', { name: option, exact: true }).click() }
    try {
      await page.goto(base + '/settings/ai'); await page.getByText('添加第一个模型', { exact: true }).waitFor(); await shot('empty')
      await page.getByRole('button', { name: '添加ASR配置', exact: true }).click(); let dialog = page.getByRole('dialog')
      await select('ASR接入预设', 'MiMo 语音识别 · v1')
      assert.equal(await dialog.getByLabel('模型标识', { exact: true }).inputValue(), preset.defaultModel)
      assert.ok(await dialog.getByLabel('模型标识', { exact: true }).getAttribute('readonly') !== null)
      await dialog.getByLabel('配置名称', { exact: true }).fill('课堂ASR')
      await dialog.getByLabel('API 根地址', { exact: true }).fill(preset.defaultBaseUrl + preset.operationPath)
      await dialog.getByLabel('API Key', { exact: true }).fill('synthetic-asr-ui-key'); await shot('preset-form')
      await dialog.getByRole('button', { name: '保存模型', exact: true }).click(); await dialog.waitFor({ state: 'hidden' })
      await select('默认语音转写模型', '课堂ASR'); await page.getByRole('switch', { name: '启用 SDK 直连', exact: true }).click(); await shot('configured')
      await page.getByText('音频转写', { exact: true }).click(); await page.getByText(/预设：MiMo/).waitFor()
      await page.getByLabel('本次音频', { exact: true }).setInputFiles({ name: audio.name, mimeType: 'audio/wav', buffer: wave })
      await page.waitForFunction(() => { const audio = document.querySelector('audio[aria-label="待转写音频"]'); return audio && audio.readyState >= 1 && Number.isFinite(audio.duration) && audio.duration > 0 })
      await select('识别语言', '中文'); assert.ok(await page.getByRole('button', { name: '开始转写并记录', exact: true }).isDisabled()); await shot('audio-unconfirmed')
      const consent = page.getByRole('checkbox', { name: '确认将本次音频发送到上述ASR模型', exact: true })
      await consent.check(); profiles[0] = { ...profiles[0], revision: randomUUID(), baseUrl: 'https://changed-synthetic-asr.invalid/v1/' }
      await page.getByRole('button', { name: '刷新', exact: true }).click(); await page.getByText(profiles[0].baseUrl + preset.operationPath, { exact: true }).waitFor()
      assert.equal(await consent.isChecked(), false); assert.equal(calls, 0); await shot('destination-changed')
      await consent.check(); await page.getByRole('button', { name: '开始转写并记录', exact: true }).click(); await page.getByRole('button', { name: '取消ASR等待', exact: true }).waitFor(); await shot('waiting')
      await page.getByText('调用记录', { exact: true }).click(); await page.getByRole('button', { name: /课堂ASR/ }).waitFor(); await shot('history-running')
      releaseCall(); await page.getByRole('button', { name: /课堂ASR/ }).click(); dialog = page.getByRole('dialog')
      await page.waitForFunction((text) => document.getElementById('ai-record-reply')?.value === text, transcript); await shot('transcript')
      await dialog.locator('audio').scrollIntoViewIfNeeded(); await page.waitForFunction(() => { const audio = document.querySelector('audio[aria-label="本次原音频"]'); return audio && audio.readyState >= 1 && Number.isFinite(audio.duration) && audio.duration > 0 }); await shot('original-audio')
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click(); await page.getByRole('button', { name: /另一账号的ASR调用/ }).waitFor(); await page.getByRole('button', { name: /另一账号的ASR调用/ }).click()
      dialog = page.getByRole('dialog'); await dialog.getByText('原音频仅调用发起账号可查看。', { exact: true }).waitFor(); await shot('other-owner')
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click(); changedAudio = true
      await page.getByRole('button', { name: /课堂ASR/ }).click(); dialog = page.getByRole('dialog'); await dialog.getByText(/原音频无法读取或播放/).waitFor(); await dialog.getByText(/原音频无法读取或播放/).scrollIntoViewIfNeeded(); await shot('audio-changed')
      assert.equal(await dialog.getByLabel('实际回复', { exact: true }).inputValue(), transcript)
      await dialog.getByRole('button', { name: '关闭', exact: true }).first().click(); await page.getByText('统计', { exact: true }).click(); await page.getByText('100.0%', { exact: true }).waitFor(); await shot('statistics')
      assert.equal(calls, 1); assert.deepEqual(violations, [])
      report.viewports.push({ tag, viewport, states: 12, modelCalls: calls, presetConfiguration: true, decodedOriginalAudio: true, ownerOnly: true, targetChangeRevokesConsent: true, modifiedAudioVisible: true, pass: true })
      console.log(`PASS ${tag}: registry-driven preset/configuration, audio-only call, original WAV playback metadata, owner-only access, changed-audio error, no overflow`)
    } catch (error) { await page.screenshot({ path: resolve(out, `${tag}-failure.png`), fullPage: true }); throw error }
    finally { releaseCall(); await context.close() }
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally { await browser?.close(); await new Promise((r) => server.close(r)) }
