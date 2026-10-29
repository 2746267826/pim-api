// Built UI with an isolated synthetic API only. No .env, credentials, real notes,
// model calls or background jobs. Real Test API/loopback acceptance is separate.
// npm run build && node scripts/qa-note-derived.mjs [output-directory]
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { randomUUID } from 'node:crypto'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..'), dist = resolve(root, 'dist')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/note-derived'))
mkdirSync(out, { recursive: true })
assert.ok(existsSync(resolve(dist, 'index.html')), 'Build first')

const server = createServer((req, res) => {
  const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname)
  const candidate = resolve(dist, `.${path}`)
  if (!candidate.startsWith(dist + sep) || path.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  const type = ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[extname(file)] ?? 'application/octet-stream'
  res.writeHead(200, { 'Content-Type': type })
  res.end(readFileSync(file))
})
await new Promise((resolveListen) => server.listen(0, '127.0.0.1', resolveListen))

const base = `http://127.0.0.1:${server.address().port}`
const owner = randomUUID(), noteId = randomUUID(), versionId = randomUUID(), modelId = randomUUID(), modelRevision = randomUUID()
const recordId = randomUUID(), analysisId = randomUUID(), recordVersion = randomUUID(), analysisVersion = randomUUID()
const recordRevision = randomUUID(), analysisRevision = randomUUID()
const taskId = randomUUID(), taskRevision = randomUUID()
const source = { noteId, versionId, title: '零录音合成材料', createdAt: '2026-10-29T00:00:00Z', isCurrent: true, hasConflict: false, recordings: 0, totalSamples: 0, sampleRate: 16000 }
const destination = { modelId, modelRevision, displayName: '合成共享目标', model: 'synthetic-vision-text', endpoint: 'https://synthetic.invalid/v1/', capabilities: ['Text', 'Vision'], backgroundProcessingAvailable: true }
const coverage = { complete: true, textBlocks: 1, transcriptSegments: 0, completedTranscriptSegments: 0, photos: 1, completedPhotos: 1, inkPreviews: 1, completedInkPreviews: 1, visionBatches: 1, completedVisionBatches: 1, issues: [] }
const task = { taskId, revision: taskRevision, noteId, sourceVersionId: versionId, asrTaskId: null, asrRevision: null, inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), phase: 'Succeeded', destination: { modelId, modelRevision, endpoint: destination.endpoint, confirmed: true }, coverage, artifacts: [{ artifactId: recordId, kind: 'CompleteRecord', headVersionId: null }, { artifactId: analysisId, kind: 'IndependentAnalysis', headVersionId: null }], issues: [], steps: [{ key: 'image:0001', kind: 'ImageEvidence', status: 'Succeeded', attempts: [{ requestId: randomUUID(), logId: randomUUID(), status: 'Succeeded', errorCode: null, errorMessage: null }] }, { key: 'record:0001', kind: 'RecordSynthesis', status: 'Succeeded', attempts: [{ requestId: randomUUID(), logId: randomUUID(), status: 'Succeeded', errorCode: null, errorMessage: null }] }, { key: 'analysis:0001', kind: 'AnalysisSynthesis', status: 'Succeeded', attempts: [{ requestId: randomUUID(), logId: randomUUID(), status: 'Succeeded', errorCode: null, errorMessage: null }] }], planVersion: 'v1', createdAt: '2026-10-29T00:00:00Z', updatedAt: '2026-10-29T00:01:00Z', stopRequested: false, backgroundProcessingAvailable: true }
const artifact = (artifactId, kind, version, headVersionId) => ({ artifactId, noteId, kind, revision: artifactId === recordId ? recordRevision : analysisRevision, headVersionId, versions: [{ versionId: version, author: 'Model', sourceVersionId: versionId, parentVersionId: null, sourceCompleteRecordVersionId: kind === 'IndependentAnalysis' ? recordVersion : null, generationTaskId: taskId, contentSha256: 'c'.repeat(64), hasConflict: false, createdAt: '2026-10-29T00:01:00Z', isHead: headVersionId === version }] })
const artifactVersion = (artifactId, kind, id, content) => ({ versionId: id, artifactId, kind, author: 'Model', parentVersionId: null, observedHeadVersionId: null, sourceCompleteRecordVersionId: kind === 'IndependentAnalysis' ? recordVersion : null, sourceVersionId: versionId, generationTaskId: taskId, contentMarkdown: content, contentSha256: 'c'.repeat(64), inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), planVersion: 'v1', promptVersion: 'p1', aiCallLogId: null, modelProfileId: modelId, modelRevision, modelEndpoint: destination.endpoint, coverage, hasConflict: false, createdAt: '2026-10-29T00:01:00Z', isHead: false })

const report = { boundary: 'Built UI, isolated synthetic API; real Test API/loopback and real model quality are separate.', viewports: [] }
let browser

try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'synthetic-note-derived-token'))
    const violations = [], errors = [], adminCalls = []
    let actorId = owner, created = false, recordHead = null, analysisHead = null, creates = 0, binds = 0

    await context.route('**/*', async (route) => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname
      if (url.origin !== base) { violations.push(path); return route.abort() }
      if (!path.startsWith('/api/')) return route.continue()
      const ok = (data) => route.fulfill({ json: { code: 0, message: 'synthetic', data } })
      if (path === '/api/v1/ai/models' || path === '/api/v1/ai/model-settings' || path === '/api/v1/ai/asr-presets') adminCalls.push(path)
      if (path === '/api/v1/auth/me') return ok({ id: actorId, username: 'synthetic-user', displayName: '普通账号', role: 'user' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'synthetic', capabilities: [] } })
      if (path === '/api/v1/status/summary') return ok({ status: 0, label: 'synthetic', checkedAt: new Date().toISOString() })
      if (path === '/api/v1/native-notes/derived-tasks/limits') return ok({ maxImages: 20, maxImagesPerVisionCall: 4, maxImageBytes: 5242880, maxVisionCallBytes: 8388608, maxVisionCalls: 40, maxEvidenceCharsPerStep: 50000, maxEvidenceSteps: 4096, maxSynthesisInputChars: 500000, maxDerivedContentCharacters: 1000000 })
      if (path === '/api/v1/native-notes/derived-tasks/destinations') return ok({ total: 1, items: [destination] })
      if (path === '/api/v1/native-notes/derived-tasks/sources') return ok({ total: 1, offset: 0, items: [source] })
      if (path === '/api/v1/native-notes/derived-tasks' && request.method() === 'POST') {
        creates++; const body = request.postDataJSON(); assert.equal(body.expectedUserId, actorId); assert.equal(body.versionId, versionId); assert.equal(body.asrTaskId, null); assert.equal(body.consent.confirmed, true)
        created = true; return ok(task)
      }
      if (path === '/api/v1/native-notes/derived-tasks' && request.method() === 'GET') return ok(created ? [task] : [])
      if (path === `/api/v1/native-notes/derived-tasks/${taskId}/steps`) return ok({ total: 3, offset: 0, items: task.steps })
      if (path === `/api/v1/native-notes/derived-tasks/${taskId}`) return ok(task)
      if (path === '/api/v1/native-notes/derived-artifacts' && request.method() === 'GET') return ok(created ? [artifact(recordId, 'CompleteRecord', recordVersion, recordHead), artifact(analysisId, 'IndependentAnalysis', analysisVersion, analysisHead)] : [])
      if (path === `/api/v1/native-notes/derived-artifacts/${recordId}/versions/${recordVersion}`) return ok(artifactVersion(recordId, 'CompleteRecord', recordVersion, '合成完整记录'))
      if (path === `/api/v1/native-notes/derived-artifacts/${analysisId}/versions/${analysisVersion}`) return ok(artifactVersion(analysisId, 'IndependentAnalysis', analysisVersion, '合成独立分析'))
      if (path === `/api/v1/native-notes/derived-artifacts/${recordId}/select`) { binds++; recordHead = recordVersion; return ok(artifact(recordId, 'CompleteRecord', recordVersion, recordHead)) }
      if (path === `/api/v1/native-notes/derived-artifacts/${analysisId}/select`) { binds++; analysisHead = analysisVersion; return ok(artifact(analysisId, 'IndependentAnalysis', analysisVersion, analysisHead)) }
      if (path === `/api/v1/native-notes/derived-artifacts/${recordId}/manual-revisions`) return ok({ artifact: artifact(recordId, 'CompleteRecord', recordVersion, recordHead), versionId: randomUUID(), hasConflict: true })
      return ok([])
    })

    const page = await context.newPage()
    page.on('pageerror', (error) => errors.push(error.message))
    await page.goto(base + '/native-notes')
    await page.getByRole('heading', { name: '笔记处理', exact: true }).waitFor()
    await page.getByRole('combobox', { name: '材料版本' }).click(); await page.getByRole('option', { name: /零录音合成材料/ }).click()
    await page.getByRole('combobox', { name: '本次生成目标' }).click(); await page.getByRole('option', { name: '合成共享目标' }).click()
    await page.getByRole('checkbox', { name: /确认将固定版本的完整正文/ }).check()
    await page.getByRole('button', { name: /创建派生任务/ }).click()
    await page.getByText('任务步骤与覆盖', { exact: true }).waitFor()
    assert.equal(creates, 1)
    await page.getByRole('button', { name: '明确设为当前版本' }).first().click()
    await page.getByLabel('完整记录正文').waitFor()
    await page.getByLabel('完整记录正文').fill('合成人工修订：保留并等待选择')
    const saveManual = page.getByRole('button', { name: '保存为人工修订' }).first()
    for (let attempt = 0; attempt < 40 && !(await saveManual.isEnabled()); attempt++) await page.waitForTimeout(250)
    assert.equal(await saveManual.isEnabled(), true)
    await saveManual.click()
    await page.getByText(/候选已保存，当前选用未改变/).waitFor()
    assert.ok(binds >= 1)

    await page.reload()
    await page.getByRole('heading', { name: '笔记处理', exact: true }).waitFor()
    await page.getByRole('combobox', { name: '材料版本' }).click(); await page.getByRole('option', { name: /零录音合成材料/ }).click()
    await page.getByLabel('完整记录正文').waitFor()
    assert.equal(await page.getByLabel('完整记录正文').inputValue(), '合成人工修订：保留并等待选择')

    actorId = randomUUID()
    await page.reload()
    await page.getByRole('heading', { name: '笔记处理', exact: true }).waitFor()
    await page.getByRole('combobox', { name: '材料版本' }).click(); await page.getByRole('option', { name: /零录音合成材料/ }).click()
    await page.getByLabel('完整记录正文').waitFor()
    assert.equal(await page.getByLabel('完整记录正文').inputValue(), '合成完整记录')

    const draftStorage = await page.evaluate(() => Object.keys(localStorage).filter((key) => key.startsWith('pim.noteDerivedDraft.v1/')).map((key) => localStorage.getItem(key) ?? ''))
    assert.ok(draftStorage.every((value) => !value.includes('synthetic-note-derived-token')))
    assert.deepEqual(adminCalls, [])
    const width = await page.evaluate(() => ({ content: document.documentElement.scrollWidth, viewport: innerWidth }))
    assert.ok(width.content <= width.viewport + 1, JSON.stringify(width))
    assert.deepEqual(violations, []); assert.deepEqual(errors, [])
    await page.screenshot({ path: resolve(out, `${tag}-network.png`), fullPage: true })
    report.viewports.push({ ...viewport, creates, binds, states: ['ordinary-nav', 'zero-recording-source', 'consent', 'task-steps', 'artifact-select', 'manual-conflict', 'refresh-restore', 'account-isolated'], adminCalls: 0, horizontalOverflow: false })
    await context.close()
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
  process.stdout.write(JSON.stringify(report, null, 2) + '\n')
} finally {
  await browser?.close()
  await new Promise((resolveClose) => server.close(resolveClose))
}
