// Real built UI, isolated fixture API. Does not read .env or contact a real service.
// Run after npm run build: node scripts/qa-due-urgency.mjs [output-directory]
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dist = resolve(root, 'dist')
assert.ok(existsSync(resolve(dist, 'index.html')), 'Run npm run build first')
const out = resolve(process.argv[2] ?? resolve(root, '../../../.codex-tmp/mute-deadline/web/browser'))
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
await new Promise((resolveListen) => server.listen(0, '127.0.0.1', resolveListen))
const base = `http://127.0.0.1:${server.address().port}`
const envelope = (data) => ({ code: 0, message: 'fixture', data })

// Fixed wall clock. Due values are chosen relative to this instant so the test
// does not depend on the real current date or the host timezone.
const NOW = Date.parse('2026-10-24T12:00:00.000Z')
const HOUR = 60 * 60 * 1000

function task(id, overrides = {}) {
  return {
    id, calendarId: null, uid: `uid-${id}`, title: id, description: null, priority: 0,
    estimatedDuration: null, minimumSegment: null, dtStart: null,
    due: null, status: 'NEEDS-ACTION', isInbox: true, sortOrder: 0, subTasks: [],
    plannedEnd: null, taskBookId: null, percentComplete: 0, ...overrides,
  }
}

const tasks = [
  task('逾期任务', { due: new Date(NOW - HOUR).toISOString(), isInbox: true }),
  task('临近任务', { due: new Date(NOW + 3 * HOUR).toISOString(), isInbox: true }),
  task('远期任务', { due: new Date(NOW + 8 * 24 * HOUR).toISOString(), isInbox: true }),
  task('完成任务', { due: new Date(NOW - HOUR).toISOString(), status: 'COMPLETED', isInbox: true }),
  task('取消任务', { due: new Date(NOW - HOUR).toISOString(), status: 'CANCELLED', isInbox: true }),
  task('坏日期任务', { due: 'not-a-date', isInbox: true }),
]

const report = {
  boundary: 'Built frontend and fixture API only; not backend persistence, real device or production acceptance.',
  fixedNow: new Date(NOW).toISOString(),
  viewports: [],
}
let browser

try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block', reducedMotion: 'reduce' })
    await context.addInitScript((nowMs) => {
      localStorage.setItem('pim.accessToken', 'isolated-fixture-not-a-real-token')
      const OriginalDate = Date
      const shift = nowMs - OriginalDate.now()
      // Keep Date a constructor; only shift the clock used for now()/getTime().
      class FixedDate extends OriginalDate {
        constructor(...args) {
          if (args.length === 0) args = [OriginalDate.now() + shift]
          super(...args)
        }
        static now() { return OriginalDate.now() + shift }
      }
      window.Date = FixedDate
    }, NOW)

    const violations = []
    const consoleErrors = []
    await context.route('**/*', async (route) => {
      const url = new URL(route.request().url())
      if (url.origin !== base) {
        violations.push(`External request blocked: ${url.origin}`)
        return route.abort()
      }
      if (!url.pathname.startsWith('/api/')) return route.continue()
      const reply = (data) => route.fulfill({ json: envelope(data) })
      const path = url.pathname
      if (path === '/api/v1/auth/me') return reply({ id: 'fixture-user', username: 'fixture', displayName: 'Due fixture', role: 'user' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'fixture', capabilities: [], latestVersion: null } })
      if (path === '/api/v1/status/summary') return reply({ status: 0, label: 'fixture', message: '', checkedAt: new Date(NOW).toISOString() })
      if (path === '/api/v1/calendar/calendars' || path === '/api/v1/calendar/task-books') return reply([])
      if (path === '/api/v1/calendar/tasks') {
        let items = tasks
        if (url.searchParams.has('priority')) {
          items = items.filter((item) => item.priority === Number(url.searchParams.get('priority')))
        }
        if (url.searchParams.has('status')) {
          items = items.filter((item) => item.status === url.searchParams.get('status'))
        }
        if (url.searchParams.has('page')) {
          const pageNumber = Number(url.searchParams.get('page'))
          const pageSize = Number(url.searchParams.get('pageSize') ?? 100)
          return reply({
            items: items.slice((pageNumber - 1) * pageSize, pageNumber * pageSize),
            totalCount: items.length, page: pageNumber, pageSize,
            totalPages: Math.ceil(items.length / pageSize),
          })
        }
        return reply(items)
      }
      if (path === '/api/v1/calendar/layers') return reply({ start: url.searchParams.get('start'), end: url.searchParams.get('end'), items: [] })
      if (path === '/api/v1/calendar/events') return reply({ items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 1 })
      if (path === '/api/v1/calendar/ai-placeholders') return reply([])
      if (path === '/api/v1/operations/confirmations/pending') return reply([])
      if (path === '/api/v1/pc/summary') return reply({ keystats: null, metrics: null, categories: [] })
      if (path === '/api/v1/calendar/outlook/settings') return reply({ uiStatus: 'not-configured', tokenHealth: '—', lastSyncedAt: null, lastError: null })
      if (path.startsWith('/api/v1/calendar/tasks/') && path.endsWith('/segments')) return reply([])
      if (path.startsWith('/api/v1/calendar/tasks/') && path.endsWith('/timer')) return reply({ taskId: 'fixture', taskStatus: 'NEEDS-ACTION', runningSegmentId: null, runningSince: null, accumulatedSeconds: 0, segmentCount: 0 })
      violations.push(`Unconfigured fixture: ${route.request().method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'Unconfigured fixture', data: null } })
    })

    const page = await context.newPage()
    page.on('pageerror', (error) => violations.push(`Page error: ${error.message}`))
    page.on('console', (message) => {
      if (message.type() === 'error') consoleErrors.push(message.text())
    })

    const capture = async (state) => {
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)
      assert.ok(overflow <= 1, `${tag}/${state} horizontal overflow: ${overflow}`)
    }

    // 1) Tasks page: all six fixture rows are present with the shared classification.
    await page.goto(`${base}/tasks`)
    await page.getByText('逾期任务').first().waitFor()
    await page.getByText('已逾期').first().waitFor()
    const titleRow = async (name) => page.getByText(name, { exact: true }).locator('xpath=ancestor::button[1]')
    assert.ok((await (await titleRow('逾期任务')).getByText('已逾期').getAttribute('class')).includes('text-crit'), `${tag}: overdue not crit`)
    assert.ok(await (await titleRow('临近任务')).getByText('即将到期').count() > 0, `${tag}: due-soon label missing`)
    assert.equal(await (await titleRow('远期任务')).getByText(/已逾期|即将到期/).count(), 0, `${tag}: distant must stay calm`)
    assert.equal(await (await titleRow('完成任务')).getByText('已逾期').count(), 0, `${tag}: completed must not claim overdue`)
    assert.equal(await (await titleRow('取消任务')).getByText('已逾期').count(), 0, `${tag}: cancelled must not claim overdue`)
    assert.ok(await page.getByText('截止 时间无效').count() > 0, `${tag}: invalid due must be visible`)
    await capture('tasks-classification')

    // 2) Inbox panel on the calendar page.
    await page.goto(`${base}/calendar`)
    if (tag === 'mobile') await page.getByRole('button', { name: '收件箱' }).click()
    const inboxScope = tag === 'mobile' ? page.locator('.pim-drawer-right') : page
    await inboxScope.getByText('逾期任务').first().waitFor()
    assert.ok(await inboxScope.getByText(/已逾期/).first().isVisible(), `${tag}: inbox overdue missing`)
    assert.ok(await inboxScope.getByText(/即将到期/).first().isVisible(), `${tag}: inbox due-soon missing`)
    await capture('calendar-inbox')

    // 3) Workbench todos.
    await page.goto(`${base}/workbench`)
    await page.getByText('待办任务').waitFor()
    const todoDone = page.locator('li', { has: page.getByText('完成任务', { exact: true }) })
    assert.equal(await todoDone.count(), 0, `${tag}: completed task must not be a todo`)
    assert.ok(await page.getByText('已逾期').first().isVisible(), `${tag}: workbench overdue missing`)
    await capture('workbench-todos')

    // 4) Task detail dialog.
    await page.goto(`${base}/tasks`)
    await page.getByText('逾期任务').first().click()
    await page.getByRole('dialog').getByText('已逾期').waitFor()
    assert.ok(await page.getByRole('dialog').getByText(/已逾期/).isVisible(), `${tag}: detail overdue missing`)
    await capture('task-detail')

    // 5) Long-lived page crosses the deadline while open.
    await page.goto(`${base}/tasks`)
    await page.getByText('临近任务').first().waitFor()
    assert.ok(await (await titleRow('临近任务')).getByText('即将到期').count() > 0)
    await page.clock.install({ time: NOW })
    await page.clock.fastForward(4 * HOUR)
    await (await titleRow('临近任务')).getByText('已逾期').waitFor()
    await capture('tasks-time-crossed')

    assert.deepEqual(violations, [])
    assert.deepEqual(consoleErrors, [])
    report.viewports.push({
      tag, viewport, pass: true,
      states: ['tasks-classification', 'calendar-inbox', 'workbench-todos', 'task-detail', 'tasks-time-crossed'],
    })
    await context.close()
    console.log(`PASS ${tag}: tasks/inbox/workbench/detail classification, invalid due, long-open crossing, no overflow/console errors`)
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally {
  await browser?.close()
  await new Promise((resolveClose) => server.close(resolveClose))
}
