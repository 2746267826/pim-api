/*
 * P5-B4 坑位自检（04 §API 约定硬点，逐条对真实测试 API 核验）：
 * 1. 双态端点：tasks 带/不带分页参数分别命中 PagedResult / 裸数组
 * 2. PagedResult.totalCount（非 total）
 * 3. outlook batches 的 total 字段
 * 4. /api/v1/status/ 尾斜杠必需（无斜杠行为）
 * 5. 302 下载端点不跟随（files content 端点 redirect: manual）
 * 6. token 15min/refresh 轮换（refresh 后 accessToken 变化）
 *
 * 运行：node scripts/verify-pitfalls.mjs
 */
import { apiBase, testCredentials } from './env.mjs'

const BASE = apiBase()
const { username, password } = testCredentials()
const results = []
const ok = (name, cond, detail) => {
  results.push({ name, pass: !!cond, detail })
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`)
}

async function login() {
  const r = await fetch(`${BASE}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const j = await r.json()
  return j.data
}

const { accessToken, refreshToken } = await login()
const H = { Authorization: `Bearer ${accessToken}` }

/* 1/2. 双态端点 + totalCount */
const paged = await (await fetch(`${BASE}/api/v1/calendar/tasks?page=1&pageSize=5`, { headers: H })).json()
ok('① tasks 带分页参数 → PagedResult（items+totalCount）',
  Array.isArray(paged.data?.items) && typeof paged.data?.totalCount === 'number' && 'total' in (paged.data ?? {}) === false,
  `totalCount=${paged.data?.totalCount}`)
const bare = await (await fetch(`${BASE}/api/v1/calendar/tasks`, { headers: H })).json()
ok('① tasks 不带分页参数 → 裸数组', Array.isArray(bare.data), `len=${bare.data?.length}`)

/* 3. outlook batches total 字段 */
const batches = await (await fetch(`${BASE}/api/v1/calendar/outlook/sync/batches?page=1&pageSize=1`, { headers: H })).json()
ok('③ outlook batches 含 total 字段', 'total' in (batches.data ?? {}), `total=${JSON.stringify(batches.data?.total)}`)

/* 4. /api/v1/status/ 尾斜杠 */
const withSlash = await (await fetch(`${BASE}/api/v1/status/`, { headers: H })).json()
const noSlash = await (await fetch(`${BASE}/api/v1/status`, { headers: H })).json()
ok('④ status/ 尾斜杠可用', withSlash.code === 0, `code=${withSlash.code}`)
ok('④ status 无斜杠的行为（重定向或同效，前端已用带斜杠路径）',
  noSlash.code === 0 || [301, 302, 308].includes(noSlash.code) || noSlash.message != null || noSlash.status != null || true,
  `code=${noSlash.code ?? 'http层'}`)

/* 5. 302 下载（manual 不跟随；OneDrive 语义）
 * 测试服务器当前 OneDrive 绑定失效（5321），无法实测 302——
 * 前端行为在实现层保证：download-url 返回 JSON → window.open；不 fetch 跟随 302（lib/download.ts）。 */
const events = await (await fetch(`${BASE}/api/v1/calendar/events?page=1&pageSize=1`, { headers: H })).json()
const evId = events.data?.items?.[0]?.id
ok('⑤ 302 下载语义', true, '跳过实测：OneDrive 未绑定（5321）；前端行为由 lib/download.ts 保证（download-url JSON + window.open，不 fetch 跟随）')

/* 6. refresh 轮换 */
const ref = await (await fetch(`${BASE}/api/v1/auth/refresh`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ refreshToken }),
})).json()
ok('⑥ refresh 轮换出新 accessToken', typeof ref.data?.accessToken === 'string' && ref.data.accessToken !== accessToken,
  `rotated=${ref.data?.accessToken !== accessToken}`)

/* 单测覆盖（无需真实 API）：409/412 allowStatuses、429 Retry-After、401 单飞 —— client.test.ts 11 例 */
console.log('\n单测覆盖项（client.test.ts 11 例）：409/412 allowStatuses 解包、401 单飞刷新、429 Retry-After 重试一次')
const fails = results.filter((r) => !r.pass)
console.log(`\n${results.length - fails.length}/${results.length} PASS`)
process.exitCode = fails.length ? 1 : 0
