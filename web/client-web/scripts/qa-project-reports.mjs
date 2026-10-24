// Real built UI, isolated fixture API. Does not read .env or contact a real service.
// Run after npm run build: node scripts/qa-project-reports.mjs [output-directory]
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const dist = resolve(root, 'dist')
assert.ok(existsSync(resolve(dist, 'index.html')), 'Run npm run build first')
const out = resolve(process.argv[2] ?? resolve(root, '../../../.codex-tmp/project-reports/browser'))
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

const PROJECT_A = { id: 'a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1', name: 'PIM 重构', status: 'Active', description: null }
const PROJECT_B = { id: 'b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2', name: '毕业论文', status: 'Active', description: null }

function projectMetrics() {
  return {
    tasks: 2, completedTasks: 1, plannedEstimatedMinutes: 75, plannedScheduledMinutes: 75,
    actualExecutedMinutes: 30, calendarOccupiedMinutes: 0, calendarActiveDays: 0,
    tasksCreated: 2, tasksCompleted: 1, tasksOverdue: 0,
    mobileUsageEvents: 120, mobileLocationPoints: '本周期无数据', pcEvents: 0, pcMissingHours: 2,
  }
}

function makeReport(id, projectId, overrides = {}) {
  return {
    id, kind: 'Project', projectId, riskLevel: 'L0AutomaticArtifact', status: 'Active',
    generatedAt: '2026-10-24T10:00:00Z',
    contentMarkdown: [
      '# 项目报告', '', '## 结论',
      '- 计划：任务预估 75 分钟，已排入日程 75 分钟',
      '- 实际：执行 30 分钟', '',
      '> 说明：任务与计时段按所选项目过滤；日历占用与收集质量为项目周期内本账号全部日程/采集数据。',
    ].join('\n'),
    metricsJson: JSON.stringify(projectMetrics()),
    ...overrides,
  }
}

const report = {
  boundary: 'Built frontend and fixture API only; not backend persistence, real device or production acceptance.',
  viewports: [],
}
let browser

try {
  browser = await chromium.launch()
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    const tag = viewport.width === 390 ? 'mobile' : 'desktop'
    const context = await browser.newContext({ viewport, serviceWorkers: 'block', reducedMotion: 'reduce' })
    await context.addInitScript(() => localStorage.setItem('pim.accessToken', 'isolated-fixture-not-a-real-token'))
    let projects = [PROJECT_A, PROJECT_B]
    let failProjects = false
    let nextNoTasks = false
    const generatedBodies = []
    const projectQueries = []
    const violations = []
    const consoleErrors = []
    const toastSeen = []
    let reports = []

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
      if (path === '/api/v1/auth/me') return reply({ id: 'fixture-user', username: 'fixture', displayName: 'Reports fixture', role: 'user' })
      if (path === '/api/version') return route.fulfill({ json: { version: 'fixture', capabilities: [], latestVersion: null } })
      if (path === '/api/v1/status/summary') return reply({ status: 0, label: 'fixture', message: '', checkedAt: '2026-10-24T00:00:00Z' })
      if (path === '/api/v1/calendar/calendars' || path === '/api/v1/calendar/task-books') return reply([])
      if (path === '/api/v1/calendar/tasks') return reply([])
      if (path === '/api/v1/calendar/projects') {
        projectQueries.push(req.method())
        if (failProjects) return route.fulfill({ status: 503, json: { code: 503, message: 'fixture: 项目列表读取失败', data: null } })
        return reply(projects)
      }
      if (path === '/api/v1/calendar/reports') return reply(reports)
      if (path === '/api/v1/calendar/reports/generate' && req.method() === 'POST') {
        const body = req.postDataJSON()
        generatedBodies.push(body)
        if (nextNoTasks) {
          nextNoTasks = false
          return route.fulfill({ status: 400, json: { code: 2048, message: '项目下没有任何任务，无法推导项目周期。', data: null } })
        }
        const id = `rep-${generatedBodies.length}`
        const created = makeReport(id, body.projectId ?? null, body.kind !== 'Project'
          ? { kind: body.kind, projectId: null, contentMarkdown: `# ${body.kind} 报告`, metricsJson: JSON.stringify({ tasks: 2, completedTasks: 2 }) }
          : {})
        reports = [created, ...reports]
        return route.fulfill({ status: 201, json: envelope(created) })
      }
      violations.push(`Unconfigured fixture: ${req.method()} ${path}`)
      return route.fulfill({ status: 501, json: { code: 501, message: 'Unconfigured fixture', data: null } })
    })

    const page = await context.newPage()
    page.on('pageerror', (e) => violations.push(`Page error: ${e.message}`))
    page.on('console', (m) => {
      if (m.type() !== 'error') return
      const expectedHttpError = /status of (400|503)/.test(m.text())
        && /\/api\/v1\/calendar\/(projects|reports\/generate)$/.test(m.location().url)
      if (!expectedHttpError) consoleErrors.push(m.text())
    })
    page.on('console', (m) => {
      if (m.type() === 'log' && /已生成|生成报告失败/.test(m.text())) toastSeen.push(m.text())
    })

    const capture = async (state) => {
      await page.screenshot({ path: resolve(out, `${tag}-${state}.png`), fullPage: true })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)
      assert.ok(overflow <= 1, `${tag}/${state} horizontal overflow: ${overflow}`)
    }
    const openProjectKind = async () => page.getByRole('radio', { name: '项目报告' }).click()
    const pickProject = async (name) => {
      await page.getByRole('combobox', { name: '选择项目' }).click()
      await page.getByRole('option', { name }).click()
    }

    await page.goto(`${base}/reports`)

    // 1) 未选项目：生成禁用（02046 前置）
    await openProjectKind()
    await page.getByRole('combobox', { name: '选择项目' }).waitFor()
    assert.equal(await page.getByRole('button', { name: /生成报告/ }).isDisabled(), true)
    await capture('project-unselected')

    // 2) 选择项目→生成→打开实际返回报告，请求带 projectId
    await pickProject('PIM 重构')
    assert.equal(await page.getByRole('button', { name: /生成报告/ }).isDisabled(), false)
    await page.getByRole('button', { name: /生成报告/ }).click()
    await page.getByText('计划 vs 实际').waitFor()
    const lastBody = generatedBodies.at(-1)
    assert.equal(lastBody.kind, 'Project')
    assert.equal(lastBody.projectId, PROJECT_A.id)

    // 14 项中文指标、真实 0 与「本周期无数据」
    for (const label of ['已排期任务', '其中已完成', '任务预估时长', '已排入日程时长', '实际执行时长',
      '日历占用时长', '有日程的天数', '新增任务', '完成任务', '逾期任务', '手机使用事件', '手机定位点', 'PC 采集事件', 'PC 缺数小时']) {
      assert.ok(await page.getByText(label, { exact: true }).count() > 0, `${tag}: missing metric ${label}`)
    }
    assert.ok(await page.getByText('本周期无数据', { exact: true }).count() > 0)
    await capture('project-report-full')
    await page.getByText('PC 缺数小时', { exact: true }).scrollIntoViewIfNeeded()
    await capture('project-report-metrics-bottom')

    // No task count must not turn into one task or a fabricated 0% completion rate.
    reports[0] = { ...reports[0], metricsJson: JSON.stringify({ ...projectMetrics(), tasks: 0, completedTasks: 0 }) }
    await page.reload()
    await openProjectKind()
    await pickProject('PIM 重构')
    await page.getByText('任务数为 0 或无数据，无法计算完成率').waitFor()
    await capture('project-zero-tasks')

    // 3) 切换项目不串报告
    await pickProject('毕业论文')
    await page.getByText('暂无报告').waitFor()
    assert.equal(generatedBodies.filter((b) => b.projectId === PROJECT_B.id).length, 0)
    await capture('project-switched-empty')

    // 4) 02048 失败无假成功
    nextNoTasks = true
    await pickProject('PIM 重构')
    await page.getByRole('button', { name: /生成报告/ }).click()
    await page.getByText('项目下没有任何任务，无法推导项目周期。').first().waitFor()
    assert.equal(reports.length, 1, '02048 must not create a report')
    await capture('project-02048-error')

    // 5) 无项目空态
    projects = []
    await page.reload()
    await openProjectKind()
    await page.getByText('还没有项目').waitFor()
    assert.equal(await page.getByRole('button', { name: /生成报告/ }).isDisabled(), true)
    await capture('project-none')
    projects = [PROJECT_A, PROJECT_B]

    // 6) 项目列表失败态
    failProjects = true
    await page.reload()
    await openProjectKind()
    await page.getByText('项目列表加载失败').waitFor()
    assert.equal(await page.getByRole('button', { name: /生成报告/ }).isDisabled(), true)
    await capture('project-list-error')
    failProjects = false

    // 7) Daily/Weekly/Monthly 可生成
    for (const [kind, label] of [['Daily', '日报'], ['Weekly', '周报'], ['Monthly', '月报']]) {
      await page.reload()
      await page.getByRole('radio', { name: label }).click()
      await page.getByRole('button', { name: /生成报告/ }).click()
      await page.getByText(`${label} · 2026-10-24`).waitFor()
      assert.equal(generatedBodies.at(-1).kind, kind)
      assert.equal(generatedBodies.at(-1).projectId, undefined)
    }
    await capture('daily-weekly-monthly')

    assert.deepEqual(violations, [])
    assert.deepEqual(consoleErrors, [])
    report.viewports.push({
      tag, viewport, pass: true,
      generatedBodies,
      projectQueries: projectQueries.length,
      states: ['project-unselected', 'project-report-full', 'project-zero-tasks', 'project-switched-empty', 'project-02048-error', 'project-none', 'project-list-error', 'daily-weekly-monthly'],
    })
    await context.close()
    console.log(`PASS ${tag}: project selection + projectId generation, 14 metrics, no-data vs 0, project switch isolation, 02048, no/failed projects, D/W/M, no overflow/console errors`)
  }
  writeFileSync(resolve(out, 'report.json'), JSON.stringify(report, null, 2))
} finally {
  await browser?.close()
  await new Promise((r) => server.close(r))
}
