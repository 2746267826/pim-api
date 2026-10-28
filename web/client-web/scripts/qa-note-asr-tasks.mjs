// Built UI with isolated synthetic APIs only. No .env, credentials, private audio or external model calls.
// npm run build && node scripts/qa-note-asr-tasks.mjs [output-directory]
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..'), dist = resolve(root, 'dist')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/note-asr')); mkdirSync(out, { recursive: true })
assert.ok(existsSync(resolve(dist, 'index.html')), 'Build first')
const server = createServer((req, res) => {
  const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname), candidate = resolve(dist, `.${path}`)
  if (!candidate.startsWith(dist + sep) || path.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[extname(file)] ?? 'application/octet-stream')
  res.end(readFileSync(file))
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`, owner = randomUUID(), version = randomUUID(), modelId = randomUUID(), revision = randomUUID()
const preset = { id: 'mimo-asr', version: '1', name: 'MiMo 语音识别', defaultBaseUrl: 'https://synthetic.invalid/v1/', operationPath: 'chat/completions',
  defaultModel: 'mimo-v2.5-asr', models: ['mimo-v2.5-asr'], formats: ['wav'], languages: ['auto', 'zh', 'en'], maxAudioBytes: 4194304, streaming: false }
const model = { id: modelId, revision, name: '合成课堂ASR', baseUrl: preset.defaultBaseUrl, model: preset.defaultModel, enabled: true,
  capabilities: ['Transcription'], hasApiKey: true, timeoutSeconds: 30, maxOutputTokens: 100, createdAt: '2026-10-28T00:00:00Z', updatedAt: '2026-10-28T00:00:00Z', asrPresetId: preset.id }
const report = { boundary: 'Built UI, synthetic API fixtures; no real classroom audio, background scheduler or model quality acceptance.', viewports: [] }
let browser
try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop', context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'synthetic-note-asr-token'))
    let hasSource = false, task = null, recovered = false, sends = 0, retries = 0
    const violations = [], errors = []
    await context.route('**/*', async (route) => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname
      if (url.origin !== base) { violations.push(path); return route.abort() }
      if (!path.startsWith('/api/')) return route.continue()
      const ok = (data) => route.fulfill({ json: { code: 0, message: 'synthetic', data } })
      if (path === '/api/v1/auth/me') return ok({ id: owner, username: 'synthetic-admin', displayName: '课堂验证', role: 'admin' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'synthetic', capabilities: [] } })
      if (path === '/api/v1/status/summary') return ok({ status: 0, label: 'synthetic', checkedAt: new Date().toISOString() })
      if (path === '/api/v1/ai/models') return ok([model])
      if (path === '/api/v1/ai/model-settings') return ok({ revision, enabled: true, textModelId: null, visionModelId: null, transcriptionModelId: model.id })
      if (path === '/api/v1/ai/asr-presets') return ok([preset])
      if (path === '/api/v1/ai/status') return ok({ enabled: false, provider: 'synthetic', baseUrl: '', defaultModel: '', lastError: null })
      if (path.endsWith('/asr-tasks/sources')) return ok({ total: hasSource ? 1 : 0, offset: 0, items: hasSource ? [{ noteId: version, versionId: version, title: '研究方法课堂 · 固定材料',
        createdAt: model.createdAt, isCurrent: true, hasConflict: false, recordings: 2, totalSamples: 1920000, sampleRate: 16000 }] : [] })
      if (path.endsWith('/asr-tasks')) {
        if (request.method() === 'GET') return ok(task ? [task] : [])
        sends++; const body = request.postDataJSON(); assert.equal(body.expectedUserId, owner); assert.equal(body.versionId, version)
        assert.equal(body.consent.confirmed, true); assert.equal(body.consent.modelRevision, revision)
        task = { taskId: body.taskId, revision: randomUUID(), noteId: version, versionId: version, inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), phase: 'NeedsAttention',
          language: body.language, destination: body.consent, createdAt: model.createdAt, updatedAt: model.updatedAt, totalSegments: 2, completedSegments: 1, failedSegments: 1,
          totalSamples: 1920000, completedSamples: 960000, sampleRate: 16000, stopRequested: false, backgroundProcessingAvailable: true }
        return ok(task)
      }
      if (task && path.endsWith(`/asr-tasks/${task.taskId}/segments`)) return ok({ total: 2, offset: 0, items: [0, 1].map((index) => ({ index, recordingId: version, recordingSegment: index + 1,
        chunkIndex: 1, sourceFileIndex: index, sourcePath: `recordings/synthetic-${index}.wav`, sourceSha256: 'c'.repeat(64), sourceOffsetSamples: 0,
        recordingOffsetSamples: 0, timelineOffsetSamples: index * 960000, samples: 960000, status: index === 0 || recovered ? 'Succeeded' : 'Incomplete',
        transcript: index === 0 ? '第一片原始转写：保留课堂原文，不覆盖已成功片段。' : recovered ? '第二片恢复后的完整转写。' : null,
        attempts: [{ requestId: randomUUID(), logId: randomUUID(), status: index === 0 ? 'Succeeded' : 'Incomplete', errorMessage: index === 0 ? null : '调用结果未知，请核对记录。' },
          ...(index === 1 && recovered ? [{ requestId: randomUUID(), logId: randomUUID(), status: 'Succeeded', errorMessage: null }] : [])] })) })
      if (task && path.endsWith(`/asr-tasks/${task.taskId}/retry`)) {
        const body = request.postDataJSON(); assert.deepEqual(body.segmentIndexes, [1]); assert.equal(body.expectedUserId, owner); assert.equal(body.expectedRevision, task.revision)
        assert.deepEqual(body.consent, task.destination); assert.equal(body.acknowledgePossibleDuplicateCharge, true)
        retries++; recovered = true; task = { ...task, revision: randomUUID(), phase: 'Succeeded', completedSegments: 2, completedSamples: 1920000, failedSegments: 0 }; return ok(task)
      }
      if (task && path.endsWith(`/asr-tasks/${task.taskId}`)) return ok(task)
      return ok([])
    })
    const page = await context.newPage(); page.on('pageerror', (error) => errors.push(error.message))
    await page.goto(base + '/settings/ai'); await page.getByRole('tablist').last().getByText('笔记录音任务', { exact: true }).click()
    await page.getByText('没有已上传材料', { exact: true }).waitFor(); await page.screenshot({ path: resolve(out, `${tag}-empty.png`), fullPage: true })
    hasSource = true; await page.getByRole('button', { name: '刷新', exact: true }).click()
    await page.getByRole('combobox', { name: '固定材料版本' }).click(); await page.getByRole('option', { name: /研究方法课堂/ }).click()
    assert.equal(await page.getByRole('button', { name: '开始后台转写' }).isDisabled(), true)
    await page.getByRole('checkbox', { name: '确认将该固定版本的全部录音发送至上述模型' }).check()
    await page.getByRole('button', { name: '开始后台转写' }).click(); await page.getByLabel('第1片转写', { exact: true }).waitFor()
    assert.equal(sends, 1); assert.equal(await page.getByRole('checkbox', { name: '恢复第1片' }).count(), 0)
    await page.getByLabel('第1片转写').scrollIntoViewIfNeeded(); await page.screenshot({ path: resolve(out, `${tag}-partial.png`), fullPage: true })
    await page.getByRole('checkbox', { name: '恢复第2片' }).check(); assert.equal(await page.getByRole('button', { name: '恢复所选分片' }).isDisabled(), true)
    await page.getByRole('checkbox', { name: '确认分片恢复和可能重复计费' }).check(); await page.getByRole('button', { name: '恢复所选分片' }).click()
    await page.getByLabel('第2片转写').waitFor(); assert.equal(retries, 1); assert.equal(sends, 1)
    await page.getByLabel('第2片转写').scrollIntoViewIfNeeded(); await page.screenshot({ path: resolve(out, `${tag}-recovered.png`), fullPage: true })
    const width = await page.evaluate(() => ({ content: document.documentElement.scrollWidth, viewport: innerWidth }))
    assert.ok(width.content <= width.viewport + 1, JSON.stringify(width)); assert.deepEqual(violations, []); assert.deepEqual(errors, [])
    report.viewports.push({ ...viewport, sends, retries, states: ['empty', 'consent', 'partial', 'recovered'], horizontalOverflow: false }); await context.close()
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2)); process.stdout.write(JSON.stringify(report, null, 2) + '\n')
} finally { await browser?.close(); await new Promise((r) => server.close(r)) }
