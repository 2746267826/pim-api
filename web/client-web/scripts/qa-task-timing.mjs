// Real built UI, isolated fixture API. Does not read .env or contact a real service.
// Run after npm run build: node scripts/qa-task-timing.mjs [output-directory]
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dist = resolve(root, 'dist')
assert.ok(existsSync(resolve(dist, 'index.html')), 'Run npm run build first')
const out = resolve(process.argv[2] ?? resolve(root, '.shots/task-timing'))
mkdirSync(out, { recursive: true })
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.json': 'application/json' }
const server = createServer((req, res) => {
  const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname)
  const candidate = resolve(dist, `.${pathname}`)
  if (!candidate.startsWith(dist + sep) || pathname.startsWith('/api/')) return res.writeHead(404).end()
  const file = existsSync(candidate) && extname(candidate) ? candidate : resolve(dist, 'index.html')
  res.setHeader('Content-Type', mime[extname(file)] ?? 'application/octet-stream')
  res.end(readFileSync(file))
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const base = `http://127.0.0.1:${server.address().port}`
const envelope = (data) => ({ code: 0, message: 'fixture', data })
const paged = (items) => ({ items, totalCount: items.length, page: 1, pageSize: 100, totalPages: 1 })
const report = { boundary: 'Built frontend and fixture API only; not backend persistence, real device or production acceptance.', viewports: [] }
let browser

try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block', reducedMotion: 'reduce' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'isolated-fixture-not-a-real-token'))
    const task = {
      id: '11111111-1111-4111-8111-111111111111', uid: 'fixture-task', calendarId: null, taskBookId: null,
      title: '计时验收任务', description: '刷新后仍从服务端读取累计值', priority: 0, estimatedDuration: null,
      minimumSegment: null, dtStart: null, due: null, status: 'NEEDS-ACTION', isInbox: false,
      sortOrder: 0, subTasks: [], plannedEnd: null, percentComplete: 0,
    }
    const makeSegment = (id, status, startsAt, endsAt) => ({
      id, taskId: task.id, taskTitle: task.title, status, startsAt, endsAt,
      source: status === 'planned' ? 'manual' : 'timer', planningReason: null, confirmationId: null,
    })
    const segments = [
      makeSegment('executed', 'completed', '2026-10-24T00:00:00Z', '2026-10-24T00:02:00Z'),
      makeSegment('planned', 'planned', '2026-10-25T00:00:00Z', '2026-10-25T03:00:00Z'),
    ]
    let accumulated = 120
    let failStart = true
    let failSegmentsRead = false
    let failTimerRead = false
    let holdStart = true
    let releaseStart
    const pendingStart = new Promise((r) => { releaseStart = r })
    const actions = []
    const queries = []
    const violations = []
    let timerReads = 0
    await context.route('**/*', async (route) => {
      const req = route.request()
      const url = new URL(req.url())
      if (url.origin !== base) {
        violations.push(`External request blocked: ${url.origin}`)
        return route.abort()
      }
      if (!url.pathname.startsWith('/api/')) return route.continue()
      const reply = (data) => route.fulfill({ json: envelope(data) })
      const path = url.pathname
      if (path === '/api/v1/auth/me') return reply({ id: 'fixture-user', username: 'fixture', displayName: 'Timing fixture', role: 'user' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'fixture', capabilities: [], latestVersion: null } })
      if (path === '/api/v1/status/summary') return reply({ status: 0, label: 'fixture', message: '', checkedAt: '2026-10-24T00:00:00Z' })
      if (path === '/api/v1/calendar/calendars' || path === '/api/v1/calendar/task-books') return reply([])
      if (path === '/api/v1/calendar/tasks' && req.method() === 'GET') {
        queries.push(Object.fromEntries(url.searchParams))
        const items = !url.searchParams.get('status') || url.searchParams.get('status') === task.status ? [{ ...task }] : []
        return reply(url.searchParams.has('page') ? paged(items) : items)
      }
      if (path === `/api/v1/calendar/tasks/${task.id}/segments`) {
        if (failSegmentsRead) return route.fulfill({ status: 503, json: { code: 503, message: 'fixture: 段列表读取失败', data: null } })
        return reply(segments)
      }
      if (path === `/api/v1/calendar/tasks/${task.id}/timer`) {
        timerReads++
        if (failTimerRead) return route.fulfill({ status: 503, json: { code: 503, message: 'fixture: 计时状态读取失败', data: null } })
        const running = segments.find((s) => s.endsAt == null)
        return reply({ taskId: task.id, taskStatus: task.status, runningSegmentId: running?.id ?? null,
          runningSince: running?.startsAt ?? null, accumulatedSeconds: accumulated, segmentCount: segments.length })
      }
      const action = path.match(new RegExp(`^/api/v1/calendar/tasks/${task.id}/timer/(start|pause|stop)$`))?.[1]
      if (action && req.method() === 'POST') {
        actions.push(action)
        if (action === 'start') {
          if (failStart) {
            failStart = false
            return route.fulfill({ status: 409, json: { code: 2058, message: 'fixture: 无法开始计时', data: null } })
          }
          if (holdStart) { holdStart = false; await pendingStart }
          task.status = 'IN-PROCESS'
          accumulated += 15
          const startsAt = segments.length === 2 ? '2026-10-24T01:00:00Z' : '2026-10-24T02:00:00Z'
          const segment = makeSegment(`run-${segments.length}`, 'active', startsAt, null)
          segments.push(segment)
          return reply(segment)
        }
        const running = segments.find((s) => s.endsAt == null)
        assert.ok(running, 'Cannot pause or stop without an open segment')
        running.endsAt = new Date(Date.parse(running.startsAt) + 30_000).toISOString()
        running.status = action === 'pause' ? 'paused' : 'completed'
        accumulated += 15
        return reply(running)
      }
      violations.push(`Unconfigured fixture: ${req.method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'Unconfigured fixture', data: null } })
    })
    const page = await context.newPage()
    page.on('pageerror', (e) => violations.push(`Page error: ${e.message}`))
    const dialog = page.getByRole('dialog', { name: '任务详情', exact: true })
    const start = dialog.getByRole('button', { name: /(开始|继续)计时/ })
    const pause = dialog.getByRole('button', { name: '暂停', exact: true })
    const stop = dialog.getByRole('button', { name: '结束', exact: true })
    const clock = async (text) => dialog.getByText(text, { exact: true }).waitFor()
    const open = async () => {
      await page.getByRole('button', { name: new RegExp(`^${task.title}`) }).click()
      await dialog.waitFor()
    }
    const capture = async (state) => {
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true })
      const overflow = await page.evaluate(() => {
        const d = document.querySelector('[role="dialog"]')
        return { document: document.documentElement.scrollWidth - innerWidth, dialog: d ? d.scrollWidth - d.clientWidth : 0 }
      })
      assert.ok(overflow.document <= 1 && overflow.dialog <= 1, `${tag}/${state} horizontal overflow: ${JSON.stringify(overflow)}`)
    }
    await page.goto(`${base}/tasks`)
    await open()
    await clock('00:02:00')
    await capture('initial')
    await start.click()
    await dialog.getByText('fixture: 无法开始计时', { exact: true }).waitFor()
    await clock('00:02:00')
    assert.equal(segments.length, 2, 'Failure must not invent an execution segment')
    await capture('error')
    await start.click()
    await page.waitForFunction(() => [...document.querySelectorAll('[role="dialog"] button')].some((b) => /计时/.test(b.textContent) && b.disabled))
    assert.equal(await start.isDisabled(), true)
    await clock('00:02:00')
    releaseStart()
    await clock('00:02:15')
    assert.equal(await start.isDisabled(), true)
    await capture('running')
    await pause.click()
    await clock('00:02:30')
    assert.equal(await pause.isDisabled(), true)
    await capture('paused')
    await start.click()
    await clock('00:02:45')
    await stop.click()
    await clock('00:03:00')
    assert.equal(await stop.isDisabled(), true)
    assert.ok(segments.every((s) => s.endsAt != null))
    await capture('stopped')
    await dialog.getByRole('button', { name: '关闭', exact: true }).first().click()
    await open()
    await clock('00:03:00')
    await page.reload()
    await open()
    await clock('00:03:00')
    await capture('reloaded')
    await dialog.getByRole('button', { name: '关闭', exact: true }).first().click()
    await Promise.all([
      page.waitForResponse((r) => new URL(r.url()).searchParams.get('status') === 'IN-PROCESS'),
      page.getByRole('button', { name: '进行中', exact: true }).click(),
    ])
    assert.ok(queries.some((q) => q.status === 'IN-PROCESS'), 'The filter must send the backend status parameter')
    assert.deepEqual(actions, ['start', 'start', 'pause', 'start', 'stop'])

    for (const failure of ['segments', 'timer']) {
      failSegmentsRead = failure === 'segments'
      failTimerRead = failure === 'timer'
      await page.reload()
      await open()
      await dialog.getByText(failure === 'segments' ? 'fixture: 段列表读取失败' : 'fixture: 计时状态读取失败', { exact: true }).waitFor()
      await dialog.getByText('暂不可用', { exact: true }).waitFor()
      assert.equal(await dialog.getByText('00:00:00', { exact: true }).count(), 0, 'Unknown is not zero')
      assert.equal(await start.isDisabled(), true)
      assert.equal(await pause.isDisabled(), true)
      assert.equal(await stop.isDisabled(), true)
      await capture(`${failure}-read-error`)
    }
    failSegmentsRead = false
    failTimerRead = false
    task.status = 'COMPLETED'
    await page.reload()
    await open()
    await clock('00:03:00')
    assert.equal(await start.count(), 0, 'Completed tasks retain history, not start controls')
    await capture('completed-history')
    assert.ok(timerReads >= 5)
    assert.deepEqual(violations, [])
    report.viewports.push({ tag, viewport, actions, accumulatedSeconds: accumulated, timerReads,
      states: ['initial', 'error', 'pending', 'running', 'paused', 'stopped', 'reopened', 'reloaded', 'in-process-filter', 'segments-read-error', 'timer-read-error', 'completed-history'], pass: true })
    await context.close()
    console.log(`PASS ${tag}: actual detail clicks, failure/pending, server total, pause/continue/stop, reopen/reload, status query, no overflow`)
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally {
  await browser?.close()
  await new Promise((r) => server.close(r))
}
