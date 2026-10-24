/*
 * P5-B3 三态复查（loading / error / empty）——用 Playwright 路由拦截确定性制造：
 * - loading：全部 API 延迟 2s → 骨架屏可见
 * - error：全部 API abort → 错误提示可见（InlineAlert / EmptyState）
 * - empty：按页注入空数据（或使用真实空数据日）
 * 代表页：today / tasks / files / pc-tracker / calendar(week)
 * 产出 docs/qa/screenshots/state-*.png + three-states.md 清单
 */
import { chromium } from 'playwright'
import { mkdirSync, writeFileSync } from 'node:fs'
import { testCredentials } from './env.mjs'

const BASE = 'http://127.0.0.1:3000'
const { username, password } = testCredentials()
const OUT = 'docs/qa/screenshots'
mkdirSync(OUT, { recursive: true })

const PAGES = [
  { name: 'today', url: '/today' },
  { name: 'tasks', url: '/tasks' },
  { name: 'files', url: '/files' },
  { name: 'pc-tracker', url: '/pc-tracker' },
  { name: 'calendar-week', url: '/calendar?view=week' },
]

const EMPTY_PAGED = { code: 0, message: 'success', data: { items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 1 } }
const EMPTY_NULL = { code: 0, message: 'success', data: null }

const browser = await chromium.launch()
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } })
const page = await context.newPage()

/* 登录复用 */
await page.goto(`${BASE}/login`)
await page.getByLabel('用户名或邮箱').fill(username)
await page.getByLabel('密码').fill(password)
await page.getByRole('button', { name: '登录' }).click()
await page.waitForURL('**/today', { timeout: 15000 })

const rows = []
for (const p of PAGES) {
  for (const state of ['loading', 'error', 'empty']) {
    await page.unroute('**/api/v1/**')
    if (state === 'loading') {
      await page.route('**/api/v1/**', async (route) => {
        try {
          await new Promise((r) => setTimeout(r, 2000))
          await route.continue()
        } catch {
          /* 页面已切换时路由可能已被处理，忽略 */
        }
      })
    } else if (state === 'error') {
      // 500 页面级失败：放行 /auth/me（登录态保持），其余 500 → 页面 InlineAlert crit 可见
      await page.route('**/api/v1/**', (route) => {
        if (route.request().url().includes('/auth/me')) return route.fallback()
        return route.fulfill({
          status: 500,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50001, message: '服务器内部错误（验收模拟）', data: null }),
        })
      })
    } else {
      await page.route('**/api/v1/**', (route) => {
        const url = route.request().url()
        const body = /calendar\/tasks|files\/items/.test(url)
          ? EMPTY_PAGED
          : EMPTY_NULL
        route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
      })
    }
    try {
      await page.goto(`${BASE}${p.url}`, { timeout: 20000 })
      await page.waitForLoadState('domcontentloaded')
      await page.waitForTimeout(state === 'loading' ? 900 : 3000)
      const shot = `${OUT}/state-${p.name}-${state}.png`
      await page.screenshot({ path: shot })
      rows.push({ page: p.name, state, shot, error: null })
      process.stdout.write(`${p.name} ${state} ✓\n`)
    } catch (e) {
      rows.push({ page: p.name, state, shot: null, error: String(e.message || e).slice(0, 100) })
      process.stdout.write(`${p.name} ${state} ERROR\n`)
    }
  }
}
await page.unroute('**/api/v1/**')
await browser.close()
writeFileSync('docs/qa/three-states.json', JSON.stringify(rows, null, 1))
console.log(`\n三态截图：${rows.filter((r) => !r.error).length}/${rows.length}`)
