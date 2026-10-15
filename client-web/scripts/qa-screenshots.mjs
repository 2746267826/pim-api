/*
 * P5-B2 全页截图视觉验收（一次性脚本，可重复执行）：
 * - 登录一次复用 storageState（测试账号）
 * - 47 条静态路由 × 2 档宽度（桌面 1440 / 手机 390）批量截图到 docs/qa/screenshots/
 * - 每页记录可见文本长度，异常短（空白/加载失败）自动标旗
 * - 产出：qa-report.json + contact-sheet.html（对照清单页）
 *
 * 运行：node scripts/qa-screenshots.mjs（dev server 需在 127.0.0.1:3000 运行）
 */
import { chromium } from 'playwright'
import { mkdirSync, writeFileSync } from 'node:fs'
import { testCredentials } from './env.mjs'

const BASE = 'http://127.0.0.1:3000'
const { username, password } = testCredentials()
const OUT = 'docs/qa/screenshots'
const VIEWPORTS = [
  { tag: 'desktop', width: 1440, height: 900 },
  { tag: 'mobile', width: 390, height: 844 },
]

/* 全部静态路由（audit/:id 明细、devices/:id 明细需要真实 ID，跳过） */
const ROUTES = [
  '/today', '/calendar?view=timeline', '/calendar?view=week', '/calendar?view=month', '/calendar?view=list',
  '/calendar-styles', '/workbench', '/tasks', '/confirmations',
  '/pc-tracker', '/pc-tracker/browser', '/mobile-records', '/location-history',
  '/quick-notes', '/files',
  '/data-center', '/reminders', '/reports', '/habits',
  '/status', '/settings', '/settings/server', '/settings/microsoft',
  '/settings/data-reliability', '/settings/mcp', '/settings/calendar-data', '/settings/recycle-bin', '/settings/pc-data',
  '/settings/ai', '/settings/users', '/settings/account',
  '/app-knowledge-base', '/app-knowledge-base/categories', '/endpoint-shell', '/exhibition', '/devices',
]

mkdirSync(OUT, { recursive: true })

const browser = await chromium.launch()
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } })
const page = await context.newPage()

/* 登录一次（token 落 localStorage，storageState 跨上下文复用） */
await page.goto(`${BASE}/login`)
await page.getByLabel('用户名或邮箱').fill(username)
await page.getByLabel('密码').fill(password)
await page.getByRole('button', { name: '登录' }).click()
await page.waitForURL('**/today', { timeout: 15000 })
const storage = await context.storageState()

const report = []
for (const vp of VIEWPORTS) {
  const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height }, storageState: storage })
  const p = await ctx.newPage()
  for (const route of ROUTES) {
    const name = route.replace(/[?&=/]/g, '_')
    const entry = { route, viewport: vp.tag, textLength: 0, screenshot: `${OUT}/${name}-${vp.tag}.png`, error: null }
    try {
      await p.goto(`${BASE}${route}`, { timeout: 20000 })
      await p.waitForLoadState('domcontentloaded')
      await p.waitForTimeout(2600) // 懒加载 chunk + 数据轮询首个周期
      entry.textLength = (await p.evaluate(() => document.body.innerText.replace(/\s/g, '').length)) ?? 0
      await p.screenshot({ path: entry.screenshot, fullPage: false })
    } catch (e) {
      entry.error = String(e.message || e).slice(0, 120)
    }
    report.push(entry)
    process.stdout.write(`${vp.tag} ${route} text=${entry.textLength}${entry.error ? ' ERROR:' + entry.error : ''}\n`)
  }
  await ctx.close()
}

await browser.close()
writeFileSync('docs/qa/qa-report.json', JSON.stringify(report, null, 1))

/* 对照清单页 */
const flagged = (e) => e.error || e.textLength < 20
const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>PIM 截图验收清单</title>
<style>body{font-family:system-ui,sans-serif;margin:16px}table{border-collapse:collapse;width:100%}
td,th{border:1px solid #e2e8f0;padding:6px;font-size:12px}img{width:320px;border:1px solid #e2e8f0}
.flag{background:#fee2e2}.ok{background:#f0fdf4}</style>
<h1>PIM 截图验收清单（${new Date().toLocaleString('zh-CN')}）</h1>
<table><tr><th>路由</th><th>宽度</th><th>文本</th><th>状态</th><th>截图</th></tr>
${report.map((e) => `<tr class="${flagged(e) ? 'flag' : 'ok'}"><td>${e.route}</td><td>${e.viewport}</td><td>${e.textLength}</td><td>${e.error ?? (flagged(e) ? '疑似空白' : 'ok')}</td><td><img src="../${e.screenshot}" loading="lazy"></td></tr>`).join('')}
</table>`
writeFileSync('docs/qa/contact-sheet.html', html)

const bad = report.filter(flagged)
console.log(`\n完成：${report.length} 张截图，${bad.length} 张标旗`)
for (const b of bad) console.log(`  标旗: ${b.viewport} ${b.route} ${b.error ?? 'textLength=' + b.textLength}`)
