import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { recycleBinApi } from './api'

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
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
    })
    return envelope({
      items: [],
      groups: [],
      totalCount: 0,
      page: 1,
      pageSize: 50,
      totalPages: 0,
      receiptId: '99999999-9999-4999-8999-999999999999',
      operationId: null,
      action: 'restore',
      succeeded: 0,
      failed: 0,
      sourcePending: 0,
    })
  }))
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('统一回收站请求形状', () => {
  it('列表使用领域、搜索与分页查询参数', async () => {
    await recycleBinApi.list({
      domain: 'file',
      search: '合同',
      page: 2,
      pageSize: 20,
    })

    const url = new URL(calls[0].url)
    expect(url.pathname).toBe('/api/v1/recycle-bin')
    expect(url.searchParams.get('domain')).toBe('file')
    expect(url.searchParams.get('search')).toBe('合同')
    expect(url.searchParams.get('page')).toBe('2')
    expect(url.searchParams.get('pageSize')).toBe('20')
  })

  it('恢复请求保留跨域项目并携带恢复为副本选项', async () => {
    await recycleBinApi.restore([
      { domain: 'note', id: '11111111-1111-4111-8111-111111111111', expectedDeletedAt: '2026-11-25T02:00:00Z' },
      { domain: 'calendar', id: '22222222-2222-4222-8222-222222222222', expectedDeletedAt: null },
    ], true)

    expect(calls[0].url).toBe(`${BASE}/api/v1/recycle-bin/restore`)
    expect(calls[0].method).toBe('POST')
    expect(calls[0].body).toMatchObject({
      items: [
        { domain: 'note', id: '11111111-1111-4111-8111-111111111111', expectedDeletedAt: '2026-11-25T02:00:00Z' },
        { domain: 'calendar', id: '22222222-2222-4222-8222-222222222222', expectedDeletedAt: null },
      ],
      restoreAsCopy: true,
    })
    expect((calls[0].body as { operationId: string }).operationId).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    )
  })

  it('永久清理走独立端点，绝不混入恢复请求', async () => {
    await recycleBinApi.purge([
      { domain: 'file', id: '33333333-3333-4333-8333-333333333333' },
    ])

    expect(calls[0].url).toBe(`${BASE}/api/v1/recycle-bin/purge`)
    expect(calls[0].body).toMatchObject({
      items: [{ domain: 'file', id: '33333333-3333-4333-8333-333333333333' }],
      restoreAsCopy: false,
    })
  })
})
