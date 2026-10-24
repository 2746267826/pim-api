/*
 * PW-19：operations 域的契约形状测试（真实 method/path/body）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { auditApi, operationsApi } from './api'

const BASE = 'http://192.0.2.10:5858'
let calls: { url: string; method: string; body: unknown }[] = []

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
      calls.push({
        url: String(input),
        method: init?.method ?? 'GET',
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
      })
      return envelope([])
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('operations 域真实请求形状', () => {
  it('listPending：GET /api/v1/operations/confirmations/pending', async () => {
    await operationsApi.listPending()
    expect(calls.at(-1)).toMatchObject({
      method: 'GET',
      url: `${BASE}/api/v1/operations/confirmations/pending`,
    })
  })

  it('confirm：POST 到该确认单的 confirm 子路径，空体也是 JSON 对象', async () => {
    await operationsApi.confirm('c-1')
    expect(calls.at(-1)).toMatchObject({
      method: 'POST',
      url: `${BASE}/api/v1/operations/confirmations/c-1/confirm`,
      body: {},
    })
  })

  it('auditApi.timeline：objectType/objectId 必须 encodeURIComponent（注入 ../ 不得进路径）', async () => {
    await auditApi.timeline('event', '../secret')
    const call = calls.at(-1)!
    expect(call.url.startsWith(`${BASE}/api/v1/operations/audit/event/`)).toBe(true)
    expect(call.url).not.toContain('/../')
    const last = new URL(call.url).pathname.split('/').pop()!
    expect(decodeURIComponent(last)).toBe('../secret')
  })

  it('auditApi.export：只有传了 start/end 才拼查询串', async () => {
    await auditApi.export()
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/operations/audit/export`)
    await auditApi.export({ start: '2026-10-01', end: '2026-10-02' })
    const q = new URL(calls.at(-1)!.url).searchParams
    expect(q.get('start')).toBe('2026-10-01')
    expect(q.get('end')).toBe('2026-10-02')
  })
})
