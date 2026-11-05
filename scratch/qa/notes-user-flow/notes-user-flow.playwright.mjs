import assert from 'node:assert/strict'
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { createRequire } from 'node:module'
import { dirname, isAbsolute, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const require = createRequire(resolve(here, '..', '..', '..', 'web', 'client-web', 'package.json'))
const { chromium } = require('playwright')
const baseURL = parseLoopbackBase(process.argv[2])
const outputRoot = resolve(process.argv[3] ?? resolve(here, 'artifacts'))
const browserExecutable = resolve(process.argv[4] ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe')
const viewportSelection = process.argv[5] ?? 'all'
const staticDistRoot = process.argv[6] ? resolve(process.argv[6]) : null
assert.ok(['all', 'desktop', 'narrow'].includes(viewportSelection), 'viewport must be all, desktop, or narrow')
const runId = new Date().toISOString().replaceAll(':', '-').replaceAll('.', '-')
const outputDir = resolve(outputRoot, `run-${runId}`)
await mkdir(outputDir, { recursive: true })

const ids = {
  user: '11111111-1111-4111-8111-111111111111',
  audioNote: '22222222-2222-4222-8222-222222222222',
  audioVersion: '33333333-3333-4333-8333-333333333333',
  textNote: '44444444-4444-4444-8444-444444444444',
  textVersion: '55555555-5555-4555-8555-555555555555',
  task: '66666666-6666-4666-8666-666666666666',
  taskRevision: '77777777-7777-4777-8777-777777777777',
  attempt: '88888888-8888-4888-8888-888888888888',
  model: '99999999-9999-4999-8999-999999999999',
  modelRevision: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  recordArtifact: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  analysisArtifact: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  recordVersion: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  analysisVersion: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  photoBlock: 'abababab-abab-4bab-8bab-abababababab',
  photoAsset: 'acacacac-acac-4cac-8cac-acacacacacac',
  inkBlock: 'adadadad-adad-4dad-8dad-adadadadadad',
}
const timestamp = '2026-11-03T08:00:00.000Z'
const photoBytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ5QAAAAASUVORK5CYII=', 'base64')
const inkPreviewBytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ5QAAAAASUVORK5CYII=', 'base64')
const photoSha256 = createHash('sha256').update(photoBytes).digest('hex')
const inkSha256 = createHash('sha256').update(inkPreviewBytes).digest('hex')
const inkDocument = { width: 320, height: 240, strokes: [{ width: 2.5, points: [{ x: 12, y: 16, pressure: 1 }] }] }
const inkDocumentSha256 = createHash('sha256').update(JSON.stringify(inkDocument)).digest('hex')
const generatedMedia = {
  [ids.photoAsset]: photoBytes,
  [ids.inkBlock]: inkPreviewBytes,
}
const sourceRows = [
  { noteId: ids.audioNote, versionId: ids.audioVersion, title: '合成课堂录音：访谈方法', createdAt: timestamp, isCurrent: true, hasConflict: false, recordings: 1, totalSamples: 1920000, sampleRate: 16000 },
  { noteId: ids.textNote, versionId: ids.textVersion, title: '合成项目复盘：纯文字', createdAt: '2026-11-02T08:00:00.000Z', isCurrent: true, hasConflict: false, recordings: 0, totalSamples: 0, sampleRate: 16000 },
]
const task = {
  taskId: ids.task, revision: ids.taskRevision, noteId: ids.audioNote, versionId: ids.audioVersion,
  inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), phase: 'Running', language: 'zh',
  destination: { modelId: ids.model, modelRevision: ids.modelRevision, endpoint: 'https://synthetic.invalid/asr', presetId: 'synthetic-asr', presetVersion: '1', confirmed: true },
  createdAt: timestamp, updatedAt: timestamp, totalSegments: 2, completedSegments: 1, failedSegments: 0,
  totalSamples: 1920000, completedSamples: 960000, sampleRate: 16000, stopRequested: false,
  backgroundProcessingAvailable: true, route: 'cloud-asr', executionMode: 'realtime',
  routeConfigRevision: ids.modelRevision, routeConsentVersion: 'synthetic-consent-v1', attemptId: ids.attempt,
}
const succeededTask = {
  ...task,
  phase: 'Succeeded', completedSegments: 2, completedSamples: 1920000, updatedAt: '2026-11-03T08:05:00.000Z',
}
let currentAudioTask
let asrSuccessRequested = false
const attempts = [{
  attemptId: ids.attempt, taskId: ids.task, noteId: ids.audioNote, versionId: ids.audioVersion, ownerId: ids.user,
  route: 'cloud-asr', configRevision: ids.modelRevision, consentVersion: 'synthetic-consent-v1',
  executionMode: 'realtime', language: 'zh', inputSha256: 'a'.repeat(64), expectedSegments: 2,
  completedSegments: 1, failedSegments: 0, stopRequested: false, fencingToken: 1, modelId: ids.model,
  modelRevision: ids.modelRevision, model: 'synthetic-asr-model', endpoint: 'https://synthetic.invalid/asr',
  presetId: 'synthetic-asr', presetVersion: '1', providerHandle: null, localModelId: null, localModelRevision: null,
  promptRevision: null, promptTemplateRevision: null, state: 'Running', lastErrorCode: null, lastErrorMessage: null,
  createdAt: timestamp, updatedAt: timestamp,
}]
const reprocessHistory = { parentTaskId: ids.task, total: 0, offset: 0, limit: 10, items: [] }
const sourceMaterial = {
  inputJson: JSON.stringify({
    schemaVersion: 1, createdAtMillis: Date.parse(timestamp), id: ids.audioVersion,
    note: { schemaVersion: 1, id: ids.audioNote, revision: 3, title: sourceRows[0].title, createdAtMillis: Date.parse(timestamp), blocks: [
      { id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', kind: 'markdown', text: '# 访谈原稿\n\n合成原稿：受访者说，先把观察记录与结论分开。第二段补充：下周三前完成样本核对。' },
      { id: ids.photoBlock, kind: 'photo', assetId: ids.photoAsset, mimeType: 'image/png', sha256: photoSha256 },
      { id: ids.inkBlock, kind: 'ink', ink: inkDocument },
    ] },
    recordings: [{ id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', sampleRate: 16000, channels: 1, totalSamples: 1920000, segments: [{ index: 0, samples: 1920000, path: 'recordings/synthetic.wav' }] }],
  }),
  manifestJson: JSON.stringify({ schemaVersion: 1, versionId: ids.audioVersion, noteId: ids.audioNote, inputSha256: 'a'.repeat(64), revision: 3, files: [
    { path: 'note.json', kind: 'Document', sha256: 'c'.repeat(64), sizeBytes: 520 },
    { path: 'document.md', kind: 'Markdown', sha256: 'd'.repeat(64), sizeBytes: 160 },
    { path: 'recordings/synthetic.wav', kind: 'Recording', sha256: '2'.repeat(64), sizeBytes: 240000 },
    { path: `assets/${ids.photoAsset}.png`, kind: 'Photo', sha256: photoSha256, sizeBytes: photoBytes.length, blockId: ids.photoBlock },
    { path: `assets/${ids.inkBlock}-${inkDocumentSha256}.png`, kind: 'InkPreview', sha256: inkSha256, sizeBytes: inkPreviewBytes.length, blockId: ids.inkBlock },
  ] }),
  upload: { versionId: ids.audioVersion, noteId: ids.audioNote, baseVersionId: null, currentVersionId: ids.audioVersion, phase: 'Uploaded', createdAt: timestamp, expiresAt: '2026-11-03T09:00:00.000Z', isCurrent: true, hasConflict: false,
    limits: { partSizeBytes: 1048576, maxMetadataCharacters: 8388608, maxVersionBytes: 68719476736, maxUserReservedBytes: 68719476736, maxFiles: 100000, maxPendingVersions: 10, maxVersionsPerUser: 1000, uploadExpiryHours: 1 }, files: [] },
}
const textMaterial = {
  ...sourceMaterial,
  inputJson: JSON.stringify({ schemaVersion: 1, createdAtMillis: Date.parse(timestamp), id: ids.textVersion, note: { schemaVersion: 1, id: ids.textNote, revision: 1, title: sourceRows[1].title, createdAtMillis: Date.parse(timestamp), blocks: [
    { id: 'ffffffff-ffff-4fff-8fff-ffffffffffff', kind: 'markdown', text: '# 项目复盘\n\n合成正文：先整理目标、决定和后续行动。负责人周五前补齐验收记录。' },
  ] }, recordings: [] }),
  manifestJson: JSON.stringify({ schemaVersion: 1, versionId: ids.textVersion, noteId: ids.textNote, inputSha256: 'a'.repeat(64), revision: 1, files: [
    { path: 'note.json', kind: 'Document', sha256: 'c'.repeat(64), sizeBytes: 300 },
    { path: 'document.md', kind: 'Markdown', sha256: 'd'.repeat(64), sizeBytes: 120 },
  ] }),
  upload: { ...sourceMaterial.upload, versionId: ids.textVersion, noteId: ids.textNote },
}
const derivedArtifacts = [
  { artifactId: ids.recordArtifact, noteId: ids.audioNote, kind: 'CompleteRecord', revision: ids.taskRevision, headVersionId: ids.recordVersion,
    versions: [{ versionId: ids.recordVersion, author: 'Model', sourceVersionId: ids.audioVersion, parentVersionId: null, sourceCompleteRecordVersionId: null, generationTaskId: ids.task, contentSha256: 'f'.repeat(64), hasConflict: false, createdAt: timestamp, isHead: true }] },
  { artifactId: ids.analysisArtifact, noteId: ids.audioNote, kind: 'IndependentAnalysis', revision: ids.modelRevision, headVersionId: ids.analysisVersion,
    versions: [{ versionId: ids.analysisVersion, author: 'Model', sourceVersionId: ids.audioVersion, parentVersionId: null, sourceCompleteRecordVersionId: ids.recordVersion, generationTaskId: ids.task, contentSha256: '1'.repeat(64), hasConflict: false, createdAt: timestamp, isHead: true }] },
]
const materialVersions = new Map([
  [ids.audioVersion, sourceMaterial],
  [ids.textVersion, textMaterial],
])
const versionHistory = new Map([
  [ids.audioNote, [sourceMaterial.upload]],
  [ids.textNote, [textMaterial.upload]],
])
const pendingUploads = new Map()
const uploadedParts = new Map()
const uploadedPartHashes = new Map()
const cloneRequests = []
const completedUploads = []
let destinationRequests = 0

function resetFixtureState() {
  sourceRows[0].versionId = ids.audioVersion
  sourceRows[1].versionId = ids.textVersion
  sourceMaterial.upload.isCurrent = true
  sourceMaterial.upload.currentVersionId = ids.audioVersion
  textMaterial.upload.isCurrent = true
  textMaterial.upload.currentVersionId = ids.textVersion
  materialVersions.clear()
  materialVersions.set(ids.audioVersion, sourceMaterial)
  materialVersions.set(ids.textVersion, textMaterial)
  versionHistory.clear()
  versionHistory.set(ids.audioNote, [sourceMaterial.upload])
  versionHistory.set(ids.textNote, [textMaterial.upload])
  pendingUploads.clear()
  uploadedParts.clear()
  uploadedPartHashes.clear()
  cloneRequests.length = 0
  completedUploads.length = 0
  destinationRequests = 0
  asrSuccessRequested = false
  currentAudioTask = task
}

async function assertNoHorizontalOverflow(page, label, states) {
  const metrics = await page.evaluate(() => ({
    width: innerWidth,
    documentWidth: document.documentElement.scrollWidth,
    bodyWidth: document.body?.scrollWidth ?? 0,
  }))
  states?.push({ state: label, ...metrics, overflow: metrics.documentWidth > metrics.width + 1 || metrics.bodyWidth > metrics.width + 1 })
  assert.ok(metrics.documentWidth <= metrics.width + 1 && metrics.bodyWidth <= metrics.width + 1,
    `${label}: horizontal overflow ${JSON.stringify(metrics)}`)
  return metrics
}

function disclosure(page, summaryText) {
  return page.locator('summary').filter({ hasText: summaryText }).first().locator('xpath=ancestor::details[1]')
}

function fileBytesFor(path) {
  if (path === `assets/${ids.photoAsset}.png`) return photoBytes
  if (path.endsWith('.png') && path.includes(ids.inkBlock)) return inkPreviewBytes
  return null
}

function generatedFileBytes(path) {
  if (path === 'note.json' || path === 'document.md') return Buffer.from(`synthetic generated ${path}`)
  if (path.startsWith('assets/')) {
    const stem = path.split('/')[1].split('.')[0]
    return generatedMedia[stem] ?? null
  }
  return null
}

function uploadFileDtos(manifest, pending = new Set(), complete = new Set()) {
  const partSizeBytes = sourceMaterial.upload.limits.partSizeBytes
  return manifest.files.map((file, index) => {
    const partCount = Math.ceil(file.sizeBytes / partSizeBytes)
    return {
      index,
      path: file.path,
      kind: file.kind,
      sha256: file.sha256,
      sizeBytes: file.sizeBytes,
      partCount,
      uploadedParts: complete.has(index)
        ? Array.from({ length: partCount }, (_, part) => part)
        : pending.has(index) ? (uploadedParts.get(index) ?? []) : [],
    }
  })
}

function uploadReceipt(material, phase, { isCurrent = false, hasConflict = false, currentVersionId = null } = {}) {
  return {
    versionId: material.upload.versionId,
    noteId: material.upload.noteId,
    baseVersionId: material.upload.baseVersionId ?? null,
    currentVersionId,
    phase,
    createdAt: timestamp,
    expiresAt: '2026-11-03T09:00:00.000Z',
    isCurrent,
    hasConflict,
    limits: sourceMaterial.upload.limits,
    files: [],
  }
}
const artifactVersions = {
  [ids.recordVersion]: { versionId: ids.recordVersion, artifactId: ids.recordArtifact, kind: 'CompleteRecord', author: 'Model', parentVersionId: null, observedHeadVersionId: ids.recordVersion, sourceCompleteRecordVersionId: null, sourceVersionId: ids.audioVersion, generationTaskId: ids.task, contentMarkdown: '# 完整记录：访谈方法\n\n## 原始观察\n受访者强调先区分现场观察与研究结论。\n\n## 后续事项\n下周三前完成样本核对，并保留逐字原文供复查。', contentSha256: 'f'.repeat(64), inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), planVersion: 'synthetic-plan-v1', promptVersion: 'synthetic-prompt-v1', aiCallLogId: null, modelProfileId: ids.model, modelRevision: ids.modelRevision, modelEndpoint: 'https://synthetic.invalid/derived', coverage: { complete: true, textBlocks: 1, transcriptSegments: 2, completedTranscriptSegments: 2, photos: 0, completedPhotos: 0, inkPreviews: 0, completedInkPreviews: 0, visionBatches: 0, completedVisionBatches: 0, issues: [] }, hasConflict: false, createdAt: timestamp, isHead: true },
  [ids.analysisVersion]: { versionId: ids.analysisVersion, artifactId: ids.analysisArtifact, kind: 'IndependentAnalysis', author: 'Model', parentVersionId: null, observedHeadVersionId: ids.analysisVersion, sourceCompleteRecordVersionId: ids.recordVersion, sourceVersionId: ids.audioVersion, generationTaskId: ids.task, contentMarkdown: '# 独立分析：访谈方法\n\n## 主题\n受访者将证据记录与解释判断分开，提示分析流程需要显式区分观察、推论和结论。\n\n## 待验证假设\n样本核对是否会改变当前分类，需在下周三核对后再评估。', contentSha256: '1'.repeat(64), inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), planVersion: 'synthetic-plan-v1', promptVersion: 'synthetic-prompt-v1', aiCallLogId: null, modelProfileId: ids.model, modelRevision: ids.modelRevision, modelEndpoint: 'https://synthetic.invalid/derived', coverage: { complete: true, textBlocks: 1, transcriptSegments: 2, completedTranscriptSegments: 2, photos: 0, completedPhotos: 0, inkPreviews: 0, completedInkPreviews: 0, visionBatches: 0, completedVisionBatches: 0, issues: [] }, hasConflict: false, createdAt: timestamp, isHead: true },
}

function parseLoopbackBase(value) {
  assert.ok(value, 'Usage: node notes-user-flow.playwright.mjs <loopback-baseURL> [output-directory] [chrome-executable] [all|desktop|narrow] [dist-root]')
  const url = new URL(value)
  assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname), 'baseURL must use localhost/127.0.0.1/::1')
  assert.ok(['http:', 'https:'].includes(url.protocol), 'baseURL must use HTTP(S)')
  assert.equal(url.username + url.password, '', 'baseURL must not contain credentials')
  return url.origin
}

const spaRoutes = new Set(['/', '/native-notes', '/native-notes/processing'])
const contentTypes = {
  '.css': 'text/css', '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json',
  '.map': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.webp': 'image/webp',
}

async function fulfillStaticDist(route, url) {
  assert.ok(staticDistRoot, 'static dist root is required for offline static routing')
  let pathname
  try {
    pathname = decodeURIComponent(url.pathname)
  } catch {
    return route.fulfill({ status: 400, contentType: 'text/plain', body: 'invalid synthetic dist path' })
  }
  if (pathname.includes('\0')) return route.fulfill({ status: 400, contentType: 'text/plain', body: 'invalid synthetic dist path' })
  const requested = spaRoutes.has(pathname) ? 'index.html' : pathname.replace(/^\/+/, '')
  const candidate = resolve(staticDistRoot, requested)
  const withinRoot = relative(staticDistRoot, candidate)
  if (isAbsolute(withinRoot) || withinRoot.startsWith('..\\') || withinRoot === '..') {
    return route.fulfill({ status: 403, contentType: 'text/plain', body: 'synthetic dist path escapes root' })
  }
  try {
    const info = await stat(candidate)
    if (!info.isFile()) throw new Error('not a file')
    return route.fulfill({ status: 200, contentType: contentTypes[resolve(candidate).slice(resolve(candidate).lastIndexOf('.')).toLowerCase()] ?? 'application/octet-stream', body: await readFile(candidate) })
  } catch {
    return route.fulfill({ status: 404, contentType: 'text/plain', body: `synthetic dist file not found: ${pathname}` })
  }
}

const report = {
  boundary: 'Isolated synthetic browser fixtures and API mocks only; no real note, provider, sync, billing, or device acceptance.',
  baseURL,
  staticDistRoot,
  viewports: [],
  mockViolations: [],
  diagnostics: [],
}
let browser
let activePage
let activeViewport

try {
  browser = await chromium.launch({ headless: true, executablePath: browserExecutable })
  const viewports = [{ name: 'desktop', width: 1440, height: 1000 }, { name: 'narrow', width: 390, height: 844 }]
    .filter((viewport) => viewportSelection === 'all' || viewport.name === viewportSelection)
  for (const viewport of viewports) {
    resetFixtureState()
    activeViewport = viewport.name
    const context = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height }, serviceWorkers: 'block' })
    await context.addInitScript(({ token }) => localStorage.setItem('pim.accessToken', token), { token: 'synthetic-notes-qa-only' })
    const unmatched = []
    const external = []
    const pageErrors = []
    const apiCounts = new Map()
    const overflowStates = []
    const consoleErrors = []
    const viewportDiagnostics = { viewport: viewport.name, unmatched, external, pageErrors, consoleErrors, overflowStates }
    report.diagnostics.push(viewportDiagnostics)
    await context.route('**/*', async (route) => {
      const request = route.request()
      const url = new URL(request.url())
      if (url.origin !== baseURL) {
        external.push(`${request.method()} ${url.origin}${url.pathname}`)
        await route.abort('blockedbyclient')
        return
      }
      if (!url.pathname.startsWith('/api/')) {
        if (staticDistRoot) return fulfillStaticDist(route, url)
        return route.continue()
      }

      const key = `${request.method()} ${url.pathname}`
      apiCounts.set(key, (apiCounts.get(key) ?? 0) + 1)
      const ok = (data) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ code: 0, message: 'synthetic fixture', data, timestamp }) })
      const path = url.pathname

      if (request.method() === 'GET' && path === '/api/v1/auth/me') return ok({ id: ids.user, username: 'synthetic-reader', displayName: '合成 QA 用户', role: 'user' })
      if (request.method() === 'GET' && path === '/api/version') return route.fulfill({ status: 200, contentType: 'text/plain', body: 'synthetic-local-ui' })
      if (request.method() === 'GET' && path === '/api/v1/status/summary') return ok({ status: 1, label: '合成状态', message: '仅用于本地 UI 流程验证', checkedAt: timestamp })
      if (request.method() === 'GET' && path === '/api/v1/calendar/calendars') return ok([])
      if (request.method() === 'GET' && path === '/api/v1/calendar/task-books') return ok([])
      if (request.method() === 'GET' && path === '/api/v1/native-notes/upload-limits') return ok(sourceMaterial.upload.limits)
      if (request.method() === 'GET' && path === '/api/v1/native-notes') {
        const offset = Number(url.searchParams.get('offset') ?? 0)
        const items = sourceRows.map((row) => ({
          noteId: row.noteId,
          title: JSON.parse(materialVersions.get(row.versionId).inputJson).note.title,
          currentVersionId: row.versionId,
          hasConflict: false,
        }))
        return ok({ offset, total: items.length, items })
      }
      const currentMatch = path.match(/^\/api\/v1\/native-notes\/([^/]+)\/current$/)
      if (request.method() === 'GET' && currentMatch) {
        const matching = [...materialVersions.values()].filter((item) => item.upload.noteId === currentMatch[1])
        const material = matching.find((item) => item.upload.isCurrent) ?? matching.at(-1)
        if (material) return ok(material)
      }
      const historyMatch = path.match(/^\/api\/v1\/native-notes\/([^/]+)\/versions$/)
      if (request.method() === 'GET' && historyMatch) return ok(versionHistory.get(historyMatch[1]) ?? [])
      if (request.method() === 'GET' && path === '/api/v1/native-notes/derived-tasks/sources') return ok({ total: sourceRows.length, offset: Number(url.searchParams.get('offset') ?? 0), items: sourceRows })
      if (request.method() === 'GET' && path === `/api/v1/native-notes/versions/${ids.audioVersion}`) return ok(sourceMaterial)
      if (request.method() === 'GET' && path === `/api/v1/native-notes/versions/${ids.textVersion}`) return ok(textMaterial)
      const versionMaterialMatch = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)$/)
      if (request.method() === 'GET' && versionMaterialMatch) {
        const material = materialVersions.get(versionMaterialMatch[1])
        if (material) return ok(material)
      }
      const materialFileMatch = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/files\/(\d+)$/)
      if (request.method() === 'GET' && materialFileMatch) {
        const material = materialVersions.get(materialFileMatch[1])
        const file = material && JSON.parse(material.manifestJson).files[Number(materialFileMatch[2])]
        const bytes = file && (fileBytesFor(file.path) ?? generatedFileBytes(file.path))
        if (bytes) return route.fulfill({ status: 200, contentType: 'application/octet-stream', body: bytes })
      }
      if (request.method() === 'GET' && path === '/api/v1/quick-notes') return ok({ items: [{ id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', contentPreview: '旧记录合成条目：仅在主动展开后显示', status: 'inbox', source: 'web', attachmentCount: 0, attachments: null, createdAt: timestamp, updatedAt: timestamp, archivedAt: null }], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 })
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-tasks') {
        if (asrSuccessRequested) currentAudioTask = { ...succeededTask, versionId: currentAudioTask.versionId, noteId: currentAudioTask.noteId }
        return ok([currentAudioTask])
      }
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-tasks/sources') return ok({ total: 1, offset: Number(url.searchParams.get('offset') ?? 0), items: [sourceRows[0]] })
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-tasks/destinations') return ok({ total: 0, items: [] })
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-routing/config') return ok({ cloudConfigured: false, cloudModelId: null, consentVersion: 'synthetic-consent-v1', defaultRoute: 'local', localModelId: null, localModelRevision: null, revision: ids.modelRevision, updatedAt: timestamp })
      if (request.method() === 'GET' && path === '/api/v1/native-notes/derived-tasks/destinations') {
        destinationRequests += 1
        return ok({ total: 0, items: [] })
      }
      if (request.method() === 'GET' && path === '/api/v1/native-notes/derived-tasks/limits') return ok({ maxDerivedContentCharacters: 1000000, maxEvidenceCharsPerStep: 50000, maxEvidenceSteps: 4096, maxImageBytes: 5242880, maxImages: 20, maxImagesPerVisionCall: 4, maxSynthesisInputChars: 500000, maxVisionCallBytes: 8388608, maxVisionCalls: 40 })
      if (request.method() === 'GET' && path === '/api/v1/native-notes/derived-tasks') return ok([])
      if (request.method() === 'GET' && path === '/api/v1/native-notes/derived-artifacts') return ok(url.searchParams.get('noteId') === ids.audioNote ? derivedArtifacts : [])
      const versionMatch = path.match(/^\/api\/v1\/native-notes\/derived-artifacts\/[^/]+\/versions\/([^/]+)$/)
      if (request.method() === 'GET' && versionMatch && artifactVersions[versionMatch[1]]) return ok(artifactVersions[versionMatch[1]])
      if (request.method() === 'GET' && path === '/api/v1/native-notes/task-suggestions') return ok({ total: 0, items: [] })
      if (request.method() === 'GET' && path === `/api/v1/native-notes/asr-tasks/${ids.task}`) return ok(currentAudioTask)
      if (request.method() === 'GET' && path === `/api/v1/native-notes/asr-tasks/${ids.task}/segments`) return ok({ total: 2, offset: 0, items: [
        { index: 0, recordingId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', recordingSegment: 1, chunkIndex: 1, sourceFileIndex: 0, sourcePath: 'recordings/synthetic.wav', sourceSha256: 'e'.repeat(64), sourceOffsetSamples: 0, recordingOffsetSamples: 0, timelineOffsetSamples: 0, samples: 960000, status: 'Succeeded', transcript: '合成逐字原文：先把观察记录与结论分开。', attempts: [{ requestId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', logId: null, status: 'Succeeded', errorMessage: null, evidenceId: null, providerHandle: null, receiptKind: null }] },
        { index: 1, recordingId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', recordingSegment: 1, chunkIndex: 2, sourceFileIndex: 0, sourcePath: 'recordings/synthetic.wav', sourceSha256: 'e'.repeat(64), sourceOffsetSamples: 960000, recordingOffsetSamples: 960000, timelineOffsetSamples: 960000, samples: 960000, status: asrSuccessRequested ? 'Succeeded' : 'Running', transcript: asrSuccessRequested ? '合成逐字原文：下周三前完成样本核对。' : null, attempts: [{ requestId: 'ffffffff-ffff-4fff-8fff-ffffffffffff', logId: null, status: asrSuccessRequested ? 'Succeeded' : 'Running', errorMessage: null, evidenceId: null, providerHandle: null, receiptKind: null }] },
      ] })
      if (request.method() === 'GET' && path === `/api/v1/native-notes/asr-tasks/${ids.task}/reprocess-history`) return ok(reprocessHistory)
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-routing/attempts') return ok(attempts)
      if (request.method() === 'GET' && path === '/api/v1/native-notes/asr-routing/batches') return ok([])

      const cloneMatch = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/clone$/)
      if (request.method() === 'POST' && cloneMatch) {
        const body = request.postDataJSON()
        const material = body.material
        assert.ok(material && typeof material === 'object', 'clone request must contain the submitted material')
        const source = materialVersions.get(cloneMatch[1])
        assert.ok(source, 'clone source version must be an existing synthetic version')
        assert.equal(material.storageConsent, true, 'clone must require explicit storage authorization')
        assert.equal(body.expectedCurrentVersionId, source.upload.versionId, 'clone must fence against the current version')
        const original = JSON.parse(source.inputJson)
        const cloned = JSON.parse(material.inputJson)
        assert.deepEqual(cloned.recordings, original.recordings, 'clone must preserve original recording references')
        const originalBlocks = new Map(original.note.blocks.map((block) => [block.id, block]))
        for (const id of [ids.photoBlock, ids.inkBlock]) {
          assert.deepEqual(cloned.note.blocks.find((block) => block.id === id), originalBlocks.get(id), `clone must preserve source ${id} block references`)
        }
        assert.notEqual(cloneMatch[1], material.versionId, 'clone must create a new immutable version')
        cloneRequests.push({ sourceVersionId: cloneMatch[1], material, originalInputJson: source.inputJson })
        const manifest = JSON.parse(material.manifestJson)
        const preservedIndexes = new Set(manifest.files.flatMap((file, index) => {
          const sourceFile = JSON.parse(source.manifestJson).files.find((candidate) => candidate.path === file.path && candidate.kind === file.kind && candidate.sha256 === file.sha256 && candidate.sizeBytes === file.sizeBytes)
          return sourceFile ? [index] : []
        }))
        const changedIndexes = new Set(manifest.files.map((_, index) => index).filter((index) => !preservedIndexes.has(index)))
        const pending = { material, manifest, sourceVersionId: cloneMatch[1], preservedIndexes, changedIndexes }
        pendingUploads.set(material.versionId, pending)
        const receipt = uploadReceipt({ upload: { versionId: material.versionId, noteId: material.noteId, baseVersionId: material.baseVersionId } }, 'Uploading')
        receipt.baseVersionId = material.baseVersionId
        receipt.currentVersionId = source.upload.currentVersionId
        pending.preservedIndexes = preservedIndexes
        receipt.files = uploadFileDtos(manifest, new Set(), preservedIndexes)
        return ok(receipt)
      }
      if (request.method() === 'PUT' && /^\/api\/v1\/native-notes\/versions\/[^/]+\/files\/\d+\/parts\/\d+$/.test(path)) {
        const match = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/files\/(\d+)\/parts\/(\d+)$/)
        const pending = pendingUploads.get(match[1])
        assert.ok(pending, 'part upload must have an explicit begin/clone fixture')
        const fileIndex = Number(match[2])
        const partIndex = Number(match[3])
        assert.ok(!pending.preservedIndexes.has(fileIndex), 'preserved source files must not be re-uploaded')
        const bytes = request.postDataBuffer()
        assert.equal(createHash('sha256').update(bytes).digest('hex'), url.searchParams.get('sha256'), 'uploaded part must match its declared digest')
        const file = pending.manifest.files[fileIndex]
        assert.ok(file, 'part upload file index must exist in submitted manifest')
        uploadedPartHashes.set(`${match[1]}:${fileIndex}:${partIndex}`, url.searchParams.get('sha256'))
        const partKey = `${match[1]}:${fileIndex}`
        const current = uploadedParts.get(partKey) ?? []
        if (!current.includes(partIndex)) current.push(partIndex)
        uploadedParts.set(partKey, current)
        return ok('synthetic-part-accepted')
      }
      if (request.method() === 'POST' && (path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/complete$/))) {
        const versionId = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/complete$/)[1]
        const pending = pendingUploads.get(versionId)
        assert.ok(pending, 'complete requires an explicit clone/begin fixture')
        for (const [index, file] of pending.manifest.files.entries()) {
          if (pending.preservedIndexes.has(index)) continue
          assert.equal((uploadedParts.get(`${versionId}:${index}`) ?? []).length, Math.ceil(file.sizeBytes / sourceMaterial.upload.limits.partSizeBytes), `all chunks for ${file.path} must be acknowledged before complete`)
        }
        const material = { inputJson: pending.material.inputJson, manifestJson: pending.material.manifestJson, upload: null }
        const createsConflict = pending.material.noteId === ids.audioNote
        const existingCurrentVersionId = materialVersions.get(pending.material.noteId)?.upload.currentVersionId ?? null
        const current = uploadReceipt({ upload: { versionId, noteId: pending.material.noteId, baseVersionId: pending.material.baseVersionId } }, createsConflict ? 'Conflict' : 'Uploaded', {
          isCurrent: !createsConflict,
          hasConflict: createsConflict,
          currentVersionId: createsConflict ? existingCurrentVersionId : versionId,
        })
        current.files = uploadFileDtos(pending.manifest, new Set(), new Set(pending.manifest.files.map((_, index) => index)))
        material.upload = current
        materialVersions.set(versionId, material)
        for (const [existingVersionId, existing] of materialVersions) {
          if (existing.upload.noteId !== material.upload.noteId) continue
          existing.upload.currentVersionId = current.currentVersionId
          if (!createsConflict && existingVersionId !== versionId) existing.upload.isCurrent = false
        }
        const history = versionHistory.get(pending.material.noteId) ?? []
        versionHistory.set(pending.material.noteId, [...history, current])
        const sourceRow = sourceRows.find((row) => row.noteId === pending.material.noteId)
        if (sourceRow) {
          sourceRow.hasConflict = createsConflict
          if (!createsConflict) sourceRow.versionId = versionId
        }
        if (pending.material.noteId === ids.audioNote) {
          if (!createsConflict) {
            currentAudioTask = { ...task, versionId, noteId: ids.audioNote }
            sourceRows[0].versionId = versionId
          }
        }
        completedUploads.push({
          versionId,
          noteId: material.upload.noteId,
          sourceVersionId: pending.sourceVersionId,
          excludedPreservedIndexes: [...pending.changedIndexes],
          changedPaths: pending.manifest.files.filter((_, index) => pending.changedIndexes.has(index)).map((file) => file.path),
          files: pending.manifest.files,
          inputJson: material.inputJson,
          manifestJson: material.manifestJson,
        })
        pendingUploads.delete(versionId)
        return ok(current)
      }

      const promoteMatch = path.match(/^\/api\/v1\/native-notes\/versions\/([^/]+)\/promote$/)
      if (request.method() === 'POST' && promoteMatch) {
        const promotedVersionId = promoteMatch[1]
        const candidate = materialVersions.get(promotedVersionId)
        assert.ok(candidate, 'promotion requires an existing synthetic version')
        const body = request.postDataJSON()
        assert.equal(candidate.upload.hasConflict, true, 'promotion fixture should be an explicit conflict candidate')
        assert.equal(body.expectedCurrentVersionId, candidate.upload.currentVersionId, 'promotion must fence against the observed current version')
        for (const material of materialVersions.values()) {
          if (material.upload.noteId !== candidate.upload.noteId) continue
          material.upload.isCurrent = material.upload.versionId === promotedVersionId
          material.upload.currentVersionId = promotedVersionId
          material.upload.hasConflict = false
          if (material.upload.versionId === promotedVersionId) material.upload.phase = 'Uploaded'
        }
        const sourceRow = sourceRows.find((row) => row.noteId === candidate.upload.noteId)
        if (sourceRow) {
          sourceRow.versionId = promotedVersionId
          sourceRow.hasConflict = false
        }
        if (candidate.upload.noteId === ids.audioNote) currentAudioTask = { ...task, versionId: promotedVersionId, noteId: ids.audioNote }
        return ok(candidate.upload)
      }

      unmatched.push(`${key}${url.search}`)
      await route.fulfill({ status: 501, contentType: 'application/json', body: JSON.stringify({ code: -1, message: `QA mock has no explicit fixture for ${key}`, data: null }) })
    })

    const page = await context.newPage()
    activePage = page
    page.on('pageerror', (error) => pageErrors.push(error.message))
    page.on('console', (message) => { if (message.type() === 'error') consoleErrors.push(message.text()) })
    await context.addInitScript(() => window.sessionStorage.clear())
    await page.goto(`${baseURL}/native-notes`, { waitUntil: 'domcontentloaded' })
    await page.getByRole('heading', { name: '笔记库' }).waitFor()
    await page.getByText(sourceRows[0].title, { exact: true }).waitFor()
    await page.getByText(sourceRows[1].title, { exact: true }).waitFor()
    await assertNoHorizontalOverflow(page, 'note-library', overflowStates)
    const search = page.getByRole('textbox', { name: '搜索笔记标题' })
    await search.fill('课堂录音')
    await page.getByText(sourceRows[0].title, { exact: true }).waitFor()
    assert.equal(await page.getByText(sourceRows[1].title, { exact: true }).count(), 0, 'note search must hide non-matching library entries')
    await search.fill('')
    await page.getByText(sourceRows[1].title, { exact: true }).waitFor()
    await page.getByRole('button', { name: '新建笔记' }).click()
    await page.getByRole('heading', { name: '新建笔记' }).waitFor()
    await page.goto(`${baseURL}/native-notes`, { waitUntil: 'domcontentloaded' })
    await page.getByRole('heading', { name: '笔记库' }).waitFor()
    await page.getByText(sourceRows[0].title, { exact: true }).waitFor()

    const firstViewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, documentWidth: document.documentElement.scrollWidth }))
    assert.ok(firstViewport.documentWidth <= firstViewport.width + 1, `${viewport.name}: note library overflows horizontally: ${JSON.stringify(firstViewport)}`)
    assert.ok(await page.getByRole('button', { name: '新建笔记' }).count(), 'new-note action should be visible in the library first screen')
    assert.ok(await page.getByRole('textbox', { name: '搜索笔记标题' }).count(), 'note search should be visible in the library first screen')
    const engineeringCopy = page.getByText(/NATIVE NOTES|M2\/M3|旧 HTML 不是原生 Markdown/)
    const engineeringMatches = await engineeringCopy.all()
    const visibleEngineeringCopy = []
    for (const element of engineeringMatches) {
      if (await element.isVisible()) visibleEngineeringCopy.push(await element.innerText())
    }
    assert.deepEqual(visibleEngineeringCopy, [], `engineering/legacy banner must not be visible on the library first screen: ${visibleEngineeringCopy.join(' | ')}`)
    const moreDetails = disclosure(page, '更多：旧记录与导入')
    assert.equal(await moreDetails.evaluate((element) => element.open), false, 'legacy records and import must start collapsed')
    assert.equal(await page.getByText(/旧记录合成条目/).isVisible().catch(() => false), false, 'legacy rows must not be shown before expansion')
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-01-library.png`), fullPage: true })

    await page.goto(`${baseURL}/native-notes?note=${ids.audioNote}`, { waitUntil: 'domcontentloaded' })
    await page.getByRole('heading', { name: '笔记库' }).waitFor()
    await page.getByRole('button', { name: '转写录音', exact: true }).waitFor()
    await assertNoHorizontalOverflow(page, 'direct-audio-note', overflowStates)
    assert.ok(await page.getByLabel('正文块 1').inputValue().then((value) => /受访者说/.test(value)),
      'direct noteId URL must resolve current material after the async list is loaded')
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-01b-direct-note-url.png`), fullPage: true })
    await page.goto(`${baseURL}/native-notes`, { waitUntil: 'domcontentloaded' })
    await page.getByRole('heading', { name: '笔记库' }).waitFor()
    await page.getByText(sourceRows[0].title, { exact: true }).waitFor()

    await page.getByRole('button', { name: new RegExp(sourceRows[0].title) }).click()
    await page.getByRole('heading', { name: '继续写这篇笔记' }).waitFor()
    await assertNoHorizontalOverflow(page, 'audio-note-editor', overflowStates)
    const noteTitle = page.getByRole('textbox', { name: '笔记标题', exact: true })
    await noteTitle.waitFor()
    assert.equal(await noteTitle.inputValue(), sourceRows[0].title)
    assert.match(await page.getByLabel('正文块 1').inputValue(), /受访者说/)
    const audioPreservationNotice = page.getByText(/已有录音、照片和手写材料会完整保留/)
    await audioPreservationNotice.waitFor()
    assert.equal(await audioPreservationNotice.isVisible(), true, 'audio-bearing note must show the visible preservation notice in its fixed-material editor')
    const audioAction = page.getByRole('button', { name: '转写录音', exact: true })
    await audioAction.waitFor()
    assert.equal(await audioAction.isVisible(), true, 'audio-bearing note must expose the transcription entry')
    const originalBody = await page.getByLabel('正文块 1').inputValue()
    const editedBody = `${originalBody}\n\n合成 QA 续写：复查后补充说明。`
    await page.getByLabel('正文块 1').fill(editedBody)
    const saveConsent = page.getByRole('checkbox', { name: '确认保存到当前服务器' })
    assert.equal(await saveConsent.isChecked(), false, 'server storage must not be authorized by default')
    const saveButtons = page.getByRole('button', { name: '保存笔记' })
    assert.equal(await saveButtons.first().isDisabled(), true, 'save must remain disabled until server storage is explicitly authorized')
    assert.equal(await saveButtons.last().isDisabled(), true, 'the in-editor save action must remain disabled until explicit authorization')
    await saveConsent.check()
    assert.equal(await saveConsent.isChecked(), true, 'server storage authorization must be explicit')
    assert.equal(await saveButtons.first().isEnabled(), true, 'explicit authorization must enable the header save action')
    assert.equal(await saveButtons.last().isEnabled(), true, 'explicit authorization must enable the in-editor save action')
    await saveButtons.last().click()
    await page.getByText(/另一端同时更新了当前版本/).waitFor()
    assert.equal(cloneRequests.length, 1, 'editing an existing note must use one explicitly authorized immutable clone')
    assert.equal(completedUploads.length, 1, 'clone must complete after changed-file uploads')
    const completed = completedUploads[0]
    assert.notEqual(completed.versionId, ids.audioVersion)
    assert.equal(completed.sourceVersionId, ids.audioVersion)
    assert.match(JSON.parse(completed.inputJson).note.blocks[0].text, /合成 QA 续写/)
    const originalManifest = JSON.parse(sourceMaterial.manifestJson)
    const completedFiles = JSON.parse(completed.manifestJson).files
    for (const kind of ['Recording', 'Photo', 'InkPreview']) {
      for (const originalFile of originalManifest.files.filter((file) => file.kind === kind)) {
        assert.ok(completedFiles.some((file) => file.path === originalFile.path && file.kind === kind && file.sha256 === originalFile.sha256),
          `${kind} path and hash must remain unchanged across clone-save`)
      }
    }
    assert.ok(completed.changedPaths.includes('note.json') && completed.changedPaths.includes('document.md'),
      'changed note metadata and Markdown must be uploaded')
    assert.ok(!completed.changedPaths.some((filePath) => filePath.startsWith('recordings/') || filePath.includes(ids.photoAsset) || filePath.includes(ids.inkBlock)),
      'original audio, photo and Ink files must be cloned, not re-uploaded')
    assert.ok(await page.getByText(/另一端同时更新了当前版本/).count(), 'a concurrent save should explain that both versions were retained')
    const versionHistoryDetails = disclosure(page, '版本历史')
    assert.equal(await versionHistoryDetails.evaluate((element) => element.open), false, 'version history should start collapsed')
    await versionHistoryDetails.locator('summary').click()
    const conflictCandidate = page.getByRole('button', { name: /查看版本 .*历史版 冲突候选/ }).last()
    await conflictCandidate.waitFor()
    await conflictCandidate.click()
    await page.getByText('这是一份历史版本，仅供查看。请从当前版本继续编辑。').waitFor()
    await assertNoHorizontalOverflow(page, 'conflict-candidate', overflowStates)
    assert.equal(await page.getByRole('checkbox', { name: '确认保存到当前服务器' }).isDisabled(), true, 'a conflict candidate must not be editable before explicit promotion')
    const historicalSaveButtons = page.getByRole('button', { name: '保存笔记' })
    assert.equal(await historicalSaveButtons.first().isDisabled(), true, 'header save must remain disabled for a conflict candidate')
    assert.equal(await historicalSaveButtons.last().isDisabled(), true, 'editor save must remain disabled for a conflict candidate')
    const materialDetails = disclosure(page, '更多：历史材料、下载、导出与诊断')
    await materialDetails.locator('summary').click()
    await page.getByRole('button', { name: '明确设为当前版本' }).click()
    await page.getByRole('heading', { name: '继续写这篇笔记' }).waitFor()
    assert.equal(await page.getByLabel('正文块 1').inputValue(), editedBody, 'explicit promotion should make the selected candidate current')
    assert.ok(await page.getByText(/已有录音、照片和手写材料会完整保留/).count())
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-01c-promoted-conflict-version.png`), fullPage: true })
    await assertNoHorizontalOverflow(page, 'promoted-conflict-version', overflowStates)
    await page.getByRole('button', { name: new RegExp(sourceRows[1].title) }).click()
    await page.getByLabel('正文块 1').waitFor()
    await page.getByRole('button', { name: new RegExp(sourceRows[0].title) }).click()
    await page.getByRole('heading', { name: '继续写这篇笔记' }).waitFor()
    assert.equal(await page.getByLabel('正文块 1').inputValue(), editedBody, 'reopened current note must show the saved content')
    assert.ok(await page.getByText(/已有录音、照片和手写材料会完整保留/).count())
    const transcribeNavigation = page.waitForURL((url) => url.pathname === '/native-notes/processing' && url.searchParams.get('noteId') === ids.audioNote && url.searchParams.get('versionId') === completed.versionId && url.searchParams.get('action') === 'transcribe')
    await page.getByRole('button', { name: '转写录音', exact: true }).click()
    await transcribeNavigation
    await page.getByRole('heading', { name: '继续处理这篇笔记' }).waitFor()
    await assertNoHorizontalOverflow(page, 'audio-processing', overflowStates)
    const processingSource = page.getByRole('combobox', { name: '材料版本' })
    await processingSource.waitFor()
    assert.match(await processingSource.innerText(), /合成课堂录音：访谈方法/, 'transcription route must carry its fixed version into processing selection')
    await page.getByText(/暂无可用的共享生成目标/).waitFor()
    assert.ok(await page.getByText(/普通账号不能查看或配置模型凭据/).count(), 'ordinary users must not be offered model credential configuration')
    assert.ok(await page.getByText(/联系管理员启用共享目标/).count(), 'ordinary users should be told who can enable the shared target')
    const scopeAndCost = page.getByRole('group', { name: '本次生成目标、材料范围与费用' })
    await scopeAndCost.waitFor()
    assert.match(await scopeAndCost.innerText(), /本次目标：尚未选择可用的共享生成目标/)
    assert.match(await scopeAndCost.innerText(), /完整正文、照片和手写渲染图/)
    assert.match(await scopeAndCost.innerText(), /可能产生服务商费用/)
    assert.equal(await page.getByRole('button', { name: '生成完整记录与分析', exact: true }).count(), 1, 'generation action must use the user-facing label')
    const privacyDetails = disclosure(page, '隐私与处理说明')
    assert.equal(await privacyDetails.evaluate((element) => element.open), false, 'privacy and processing details must start collapsed')
    assert.equal(new URL(page.url()).searchParams.has('action'), false, 'transcribe action must be consumed after returning to its intended section')
    await page.getByRole('region', { name: '录音转写' }).waitFor()
    const taskSummary = page.getByRole('status', { name: '当前转写任务摘要' })
    await taskSummary.waitFor()
    assert.match(await taskSummary.innerText(), /1\/2 片/, 'ASR progress summary should be visible before details are expanded')
    assert.equal(await page.getByText('启动录音转写').count(), 0, 'active ASR must hide the duplicate start panel')
    assert.equal(await page.getByText('重新转写（可选）').count(), 0, 'active ASR must not expose optional restart controls')
    asrSuccessRequested = true
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await page.getByText(/已有可复用的成功转写，可直接整理/).waitFor()
    const reprocess = disclosure(page, '重新转写（可选）')
    assert.equal(await reprocess.count(), 1, 'successful ASR may expose an optional re-transcription disclosure')
    assert.equal(await reprocess.evaluate((element) => element.open), false, 'optional re-transcription must start collapsed')
    await taskSummary.filter({ hasText: /2\/2 片/ }).waitFor()
    assert.match(await taskSummary.innerText(), /2\/2 片/, 'the same task must transition to a successful reusable result')
    const taskSummaryToggle = page.locator('summary').filter({ hasText: '更多：转写任务与恢复' }).first()
    const taskDetails = taskSummaryToggle.locator('xpath=ancestor::details[1]')
    if (await taskDetails.evaluate((element) => element.open)) await taskSummaryToggle.click()
    assert.equal(await taskDetails.evaluate((element) => element.open), false, 'ASR history and recovery can be collapsed after entering the processing flow')
    await taskSummaryToggle.click()
    const selectedTaskRow = taskDetails.getByRole('button', { name: /2\/2 片/ }).first()
    await selectedTaskRow.waitFor()
    await page.getByText('逐片进度与恢复').waitFor()
    await page.getByText('合成逐字原文：先把观察记录与结论分开。').waitFor()
    await page.getByRole('heading', { name: '完整记录' }).waitFor()
    await page.getByRole('heading', { name: '独立分析' }).waitFor()
    await page.getByLabel('完整记录正文').waitFor()
    await page.getByLabel('独立分析正文').waitFor()
    assert.match(await page.getByLabel('完整记录正文').inputValue(), /下周三前完成样本核对/)
    assert.match(await page.getByLabel('独立分析正文').inputValue(), /待验证假设/)
    assert.notEqual(await page.getByLabel('完整记录正文').inputValue(), await page.getByLabel('独立分析正文').inputValue(), 'complete record and independent analysis should be distinct readable outputs')
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-02-transcription.png`), fullPage: true })
    await assertNoHorizontalOverflow(page, 'transcription-results', overflowStates)
    const advanced = disclosure(page, '高级选项：重处理与历史')
    assert.equal(await advanced.count(), 1, 'ASR advanced reprocessing/history controls need an explicit disclosure')
    assert.equal(await advanced.evaluate((element) => element.open), false, 'ASR advanced controls must start collapsed')
    const asrAdvanced = disclosure(page, '高级选项：识别语言')
    assert.equal(await asrAdvanced.count(), 1, 'ASR language options need an explicit disclosure')
    assert.equal(await asrAdvanced.evaluate((element) => element.open), false, 'ASR language options must start collapsed')
    const derivedAdvanced = disclosure(page, '诊断与高级限制')
    assert.equal(await derivedAdvanced.count(), 0, 'diagnostics are unavailable until a shared destination is selected')
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-03-task-progress.png`), fullPage: true })
    await assertNoHorizontalOverflow(page, 'task-progress-details', overflowStates)
    await page.locator('summary').filter({ hasText: '高级选项：重处理与历史' }).first().click()
    assert.equal(await advanced.evaluate((element) => element.open), true, 'task recovery history should reveal only after the user expands it')
    await page.getByText('重处理历史（不可变）').waitFor()

    const returnFromProcessing = page.waitForURL((url) => url.pathname === '/native-notes')
    await page.getByRole('link', { name: '返回所选笔记' }).click()
    await returnFromProcessing
    await page.getByRole('button', { name: new RegExp(sourceRows[0].title) }).click()
    await page.getByRole('heading', { name: '继续写这篇笔记' }).waitFor()
    const reopenTranscription = page.waitForURL((url) => url.pathname === '/native-notes/processing' && url.searchParams.get('versionId') === completed.versionId)
    await page.getByRole('button', { name: '转写录音', exact: true }).click()
    await reopenTranscription
    await page.getByRole('combobox', { name: '材料版本' }).waitFor()
    assert.match(await page.getByRole('combobox', { name: '材料版本' }).innerText(), /合成课堂录音：访谈方法/, 're-entry must restore the original note context')
    const reentryTaskSummary = page.locator('summary').filter({ hasText: '更多：转写任务与恢复' }).first()
    const reentryTaskDetails = reentryTaskSummary.locator('xpath=ancestor::details[1]')
    assert.equal(await reentryTaskDetails.evaluate((element) => element.open), false, 'ASR history and recovery should start collapsed on re-entry')
    await reentryTaskSummary.click()
    await reentryTaskDetails.getByRole('button', { name: /2\/2 片/ }).waitFor()
    await page.getByText('合成逐字原文：先把观察记录与结论分开。').waitFor()
    assert.ok(await page.getByRole('button', { name: /云端转写/ }).isVisible(), 'returning to the same version should restore its existing synthetic task')
    await assertNoHorizontalOverflow(page, 'transcription-reentry', overflowStates)

    const returnForTextNote = page.waitForURL((url) => url.pathname === '/native-notes')
    await page.getByRole('link', { name: '返回所选笔记' }).click()
    await returnForTextNote
    await page.getByRole('button', { name: new RegExp(sourceRows[1].title) }).click()
    await page.getByRole('heading', { name: '继续写这篇笔记' }).waitFor()
    assert.ok(await page.getByRole('button', { name: '整理笔记', exact: true }).count(), 'zero-audio note should offer the organize path')
    assert.equal(await page.getByRole('button', { name: '转写录音', exact: true }).count(), 0, 'zero-audio note must not offer transcription')
    const organizeNavigation = page.waitForURL((url) => url.pathname === '/native-notes/processing' && url.searchParams.get('noteId') === ids.textNote && url.searchParams.get('versionId') === ids.textVersion && url.searchParams.get('action') === 'organize')
    await page.getByRole('button', { name: '整理笔记', exact: true }).click()
    await organizeNavigation
    await page.getByRole('heading', { name: '继续处理这篇笔记' }).waitFor()
    await page.getByRole('combobox', { name: '材料版本' }).waitFor()
    assert.match(await page.getByRole('combobox', { name: '材料版本' }).innerText(), /合成项目复盘：纯文字/, 'organize path must retain the selected text-only material version')
    assert.equal(await page.getByRole('combobox', { name: '同版本已完成 ASR' }).count(), 0, 'zero-audio material must not require an ASR selection')
    assert.equal(await page.getByRole('region', { name: '录音转写' }).count(), 0, 'zero-audio material must not render the transcription section')
    await assertNoHorizontalOverflow(page, 'zero-audio-organize', overflowStates)
    const zeroAudioScopeAndCost = page.getByRole('group', { name: '本次生成目标、材料范围与费用' })
    await zeroAudioScopeAndCost.getByText(/本次目标：尚未选择可用的共享生成目标/).waitFor()
    await zeroAudioScopeAndCost.getByText(/可能产生服务商费用/).waitFor()
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-04-zero-audio-organize.png`), fullPage: true })
    const returnForLegacy = page.waitForURL((url) => url.pathname === '/native-notes')
    await page.getByRole('link', { name: '返回所选笔记' }).click()
    await returnForLegacy
    await page.locator('summary').filter({ hasText: '更多：旧记录与导入' }).first().click()
    await page.getByText(/旧记录合成条目/).waitFor()
    await assertNoHorizontalOverflow(page, 'expanded-legacy', overflowStates)
    await page.screenshot({ path: resolve(outputDir, `${viewport.name}-05-expanded-legacy.png`), fullPage: true })

    const finalViewport = await page.evaluate(() => ({ width: innerWidth, documentWidth: document.documentElement.scrollWidth }))
    assert.ok(finalViewport.documentWidth <= finalViewport.width + 1, `${viewport.name}: narrow viewport horizontally overflows: ${JSON.stringify(finalViewport)}`)
    report.mockViolations.push(...unmatched)
    assert.deepEqual(external, [], `external request blocked: ${external.join(', ')}`)
    assert.deepEqual(unmatched, [], `unmatched mock requests: ${unmatched.join(', ')}`)
    assert.deepEqual(pageErrors, [], `uncaught browser errors: ${pageErrors.join('; ')}`)
    assert.deepEqual(consoleErrors, [], `browser console errors: ${consoleErrors.join('; ')}`)

    assert.equal(destinationRequests > 0, true, 'derived shared destination availability must be read and visibly absent')
    assert.equal(cloneRequests.length, 1, 'one explicit clone-save must have been exercised')
    assert.equal(completedUploads.length, 1, 'one clone-save must have completed')
    report.viewports.push({ name: viewport.name, width: viewport.width, height: viewport.height, states: ['note-library', 'direct-noteId-current-load', 'current-audio-note-edit', 'explicit-consent-clone-save', 'conflict-candidate-readonly-explicit-promotion', 'recording-photo-ink-preservation', 'transcribe-action-context', 'missing-derived-target', 'visible-scope-and-cost', 'collapsed-history-language-and-optional-retranscription', 'running-asr-no-duplicate-start', 'successful-asr-reuse', 'running-transcription-and-transcript', 'zero-audio-organize-entry', 'expanded-legacy-import'], horizontalOverflow: false, apiCounts: Object.fromEntries(apiCounts), cloneCount: cloneRequests.length, completedUploadCount: completedUploads.length })
    await context.close()
  }
  await writeFile(resolve(outputDir, 'report.json'), `${JSON.stringify(report, null, 2)}\n`)
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`)
} catch (error) {
  await activePage?.screenshot({ path: resolve(outputDir, `${activeViewport ?? 'unknown'}-failure.png`), fullPage: true }).catch(() => {})
  await writeFile(resolve(outputDir, 'failure.json'), `${JSON.stringify({ ...report, error: error instanceof Error ? error.stack : String(error) }, null, 2)}\n`).catch(() => {})
  throw error
} finally {
  await browser?.close()
}
