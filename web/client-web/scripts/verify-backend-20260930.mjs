/* 后端《PC记录接口变更与修复说明_20260930.md》§5 自验脚本（node 直跑，无依赖） */
import { apiBase, testCredentials } from './env.mjs'

const BASE = apiBase()
const { username, password } = testCredentials()

async function login() {
  const res = await fetch(`${BASE}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const json = await res.json()
  if (!json.data?.accessToken) throw new Error(`login failed: ${JSON.stringify(json).slice(0, 200)}`)
  return json.data.accessToken
}

async function get(token, path) {
  const res = await fetch(`${BASE}${path}`, { headers: { Authorization: `Bearer ${token}` } })
  return { status: res.status, json: await res.json().catch(() => null) }
}

const ok = (name, cond, detail) => console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`)

const token = await login()
console.log('logged in ok\n')

/* ① 覆盖：activity-analysis 非零块数 == summary.heatmap 非零小时数；且字段为 intensityLevel */
const aa = await get(token, '/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=60')
const sum = await get(token, '/api/v1/pc/summary?date=2026-09-27')
const aaBlocks = aa.json?.data?.blocks ?? []
const hm = sum.json?.data?.heatmap ?? []
const aaNonZero = aaBlocks.filter((b) => b.activeDurationSeconds > 0)
const hmNonZero = hm.filter((b) => b.activeMinutes > 0)
ok('① activity-analysis 非零块 = heatmap 非零小时', aaNonZero.length === hmNonZero.length,
  `aa=${aaNonZero.length} hm=${hmNonZero.length}`)
ok('① summary.heatmap 用 intensityLevel/intensityMax', hm.length > 0 && hm.every((b) => typeof b.intensityLevel === 'number' && b.intensityMax === 5 && !('intensityScore' in b)),
  `buckets=${hm.length}`)
ok('① activity-analysis blocks 用 intensityLevel', aaBlocks.length > 0 && aaBlocks.every((b) => typeof b.intensityLevel === 'number' && b.intensityMax === 5 && !('intensityScore' in b)),
  `blocks=${aaBlocks.length}`)

/* ② 块内时长不超块：activeDurationSeconds ≤ 3600；apps/categories 合计也 ≤ 3600 */
const overBlock = aaBlocks.filter((b) => b.activeDurationSeconds > 3600.01)
const overApps = aaBlocks.filter((b) => (b.apps ?? []).reduce((a, x) => a + (x.durationSeconds ?? 0), 0) > 3600.01)
ok('② 所有块 activeDurationSeconds ≤ 3600', aaBlocks.length > 0 && overBlock.length === 0, `over=${overBlock.length}`)
ok('② 所有块 apps 合计 ≤ 3600', aaBlocks.length > 0 && overApps.length === 0, `over=${overApps.length}`)

/* ③ grid：keyPressCount + intensityLevel + activeMinutes（day 维度） */
const grid = await get(token, '/api/v1/pc/heatmap/grid?start=2026-09-21&end=2026-09-30&dimension=day')
const cells = (grid.json?.data?.grid ?? []).flat()
ok('③ grid 单元格含 keyPressCount/intensityLevel/activeMinutes',
  cells.length > 0 && cells.every((c) => typeof c.keyPressCount === 'number' && typeof c.intensityLevel === 'number' && typeof c.activeMinutes === 'number' && !('intensityScore' in c)),
  `cells=${cells.length}`)
/* 业务日窗口：9/27 的桶起点应为本地 04:00 = UTC 前一日 20:00 */
const d27 = cells.find((c) => (c.start ?? '').startsWith('2026-09-26T20:00'))
ok('③ day 桶按业务日窗口切分（9/27 起点为本地 04:00）', !!d27, d27 ? d27.start : '未找到 2026-09-26T20:00 桶')

/* ④ hour + 跨日 → 400 */
const hr = await fetch(`${BASE}/api/v1/pc/heatmap/grid?start=2026-09-26&end=2026-09-28&dimension=hour`, { headers: { Authorization: `Bearer ${token}` } })
ok('④ hour 维度跨日返回 400', hr.status === 400, `status=${hr.status}`)

/* ⑤ 建议：generatedForDate 存在、无 __IDLE__ / __idle__ */
const sug = await get(token, '/api/v1/pc/classification/suggestions?date=2026-09-27')
const items = sug.json?.data ?? []
ok('⑤ 建议每条带 generatedForDate', items.length > 0 && items.every((s) => typeof s.generatedForDate === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(s.generatedForDate)),
  `items=${items.length} dates=${[...new Set(items.map((s) => s.generatedForDate))].join(',')}`)
ok('⑤ 无空闲哨兵簇', items.every((s) => !/__idle__/i.test(s.clusterKey ?? '')))

/* ⑥ 键鼠范围聚合：字段同构、量级合理 */
const ks = await get(token, '/api/v1/pc/aggregation/keystats?start=2026-08-29&end=2026-09-27')
const k = ks.json?.data ?? {}
ok('⑥ keystats 范围聚合 code=0 且 totalKeyPresses>0', ks.json?.code === 0 && k.totalKeyPresses > 0,
  `totalKeyPresses=${k.totalKeyPresses}`)
ok('⑥ 字段同构（keyPressCounts/topKeys/左右中侧键/峰值）',
  typeof k.keyPressCounts === 'object' && Array.isArray(k.topKeys)
  && typeof k.leftClicks === 'number' && typeof k.rightClicks === 'number'
  && typeof k.middleClicks === 'number' && typeof k.sideBackClicks === 'number'
  && typeof k.sideForwardClicks === 'number' && typeof k.peakKps === 'number' && typeof k.peakCps === 'number',
  `topKeys=${k.topKeys?.length ?? 0}`)

/* 附加：quality 缺数时段 issue（PC-5 修复） */
const q = await get(token, '/api/v1/pc/quality?days=7')
const issues = q.json?.data?.issues ?? []
const missing = issues.filter((i) => i.code === 'tracker-events-missing-hours')
console.log(`${missing.length ? 'INFO' : 'WARN '}  pc/quality tracker-events-missing-hours issue 数=${missing.length} overallStatus=${q.json?.data?.overallStatus ?? '?'}`)
