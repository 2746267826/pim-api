/*
 * PW-19：mobile 域的契约形状测试——断言真实请求的 method/path/query 与返回解包，
 * 不是「文件存在就算覆盖」。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens, setTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { mobileApi } from './api'

const BASE = 'http://192.0.2.10:5858'
let calls: { url: string; method: string; auth: string | null }[] = []

function envelope(data: unknown): Response {
  return new Response(JSON.stringify({ code: 0, message: 'ok', data }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}

beforeEach(() => {
  calls = []
  setApiBase(BASE)
  setTokens('audit-access', 'audit-refresh')
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({
        url: String(input),
        method: init?.method ?? 'GET',
        auth: new Headers(init?.headers).get('authorization'),
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

describe('mobile 域真实请求形状', () => {
  it('analyticsOverview：GET /api/v1/mobile/analytics/overview，查询参数按契约传递且带 Bearer', async () => {
    await mobileApi.analyticsOverview({ rangeStartUtc: '2026-10-01T00:00:00Z', deviceId: 'd-1' })
    const call = calls.at(-1)!
    expect(call.method).toBe('GET')
    expect(call.url.startsWith(`${BASE}/api/v1/mobile/analytics/overview?`)).toBe(true)
    const q = new URL(call.url).searchParams
    expect(q.get('rangeStartUtc')).toBe('2026-10-01T00:00:00Z')
    expect(q.get('deviceId')).toBe('d-1')
    expect(call.auth, 'mobile 请求必须带统一 Bearer').toBe('Bearer audit-access')
  })

  it('devicesManage(sortBy=data)：查询参数只认契约值', async () => {
    await mobileApi.devicesManage('data')
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/mobile/devices/manage?sortBy=data`)
    await mobileApi.devicesManage('active')
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/mobile/devices/manage`)
  })

  it('sessionEvents：路径参数必须做 encodeURIComponent（注入的 ../ 不得原样进路径）', async () => {
    await mobileApi.sessionEvents('../admin')
    const call = calls.at(-1)!
    expect(call.url.startsWith(`${BASE}/api/v1/mobile/analytics/sessions/`)).toBe(true)
    const segment = new URL(call.url).pathname.split('/').slice(-2)[0]!
    expect(segment, '把 ../ 原样拼进路径会产生遍历').not.toBe('..')
    expect(decodeURIComponent(segment)).toBe('../admin')
    expect(call.url).not.toContain('/../')
  })

  it('livenessEvents：返回体经统一解包后字段可直接读取', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        envelope({ items: [{ id: 'e1', eventType: 'alive', eventTypeLabel: '存活', occurredAtUtc: 'x', reasonLabel: null, payloadJson: '{}' }], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 }),
      ),
    )
    const res = await mobileApi.livenessEvents('d-1', { page: 1 })
    expect(res.totalCount).toBe(1)
    expect(res.items[0]!.eventType).toBe('alive')
  })
})
