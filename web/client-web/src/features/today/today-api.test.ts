/*
 * PW-19：today 域的契约形状测试（date 查询参数 + 路径参数编码 + 返回解包）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { todayApi } from './api'

const BASE = 'http://192.0.2.10:5858'
let calls: { url: string; method: string }[] = []

function envelope(data: unknown): Response {
  return new Response(JSON.stringify({ code: 0, message: 'ok', data }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}

beforeEach(() => {
  calls = []
  setApiBase(BASE)
  clearTokens()
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ url: String(input), method: init?.method ?? 'GET' })
      return envelope({ id: 's1', kind: 'brief', status: 'normal', generatedAt: 'x', data: {}, links: [], error: null })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('today 域真实请求形状', () => {
  it('registry：GET /api/v1/today/sections 且带业务日 date 参数', async () => {
    await todayApi.registry('2026-10-11')
    expect(calls.at(-1)).toMatchObject({
      method: 'GET',
      url: `${BASE}/api/v1/today/sections?date=2026-10-11`,
    })
  })

  it('section：路径参数必须 encodeURIComponent，date 走查询串', async () => {
    await todayApi.section('../admin', '2026-10-11')
    const call = calls.at(-1)!
    expect(call.url.startsWith(`${BASE}/api/v1/today/sections/`)).toBe(true)
    expect(call.url, '注入的 ../ 不得原样进入路径').not.toContain('/../')
    const q = new URL(call.url).searchParams
    expect(q.get('date')).toBe('2026-10-11')
  })

  it('返回体经统一解包后可读（缺陷不能靠不解包掩盖）', async () => {
    const res = await todayApi.section('brief', '2026-10-11')
    expect(res.status).toBe('normal')
    expect(res.kind).toBe('brief')
  })
})
