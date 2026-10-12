/*
 * PW-19：pc 域的契约形状测试（真实 method/path/query/body）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { pcApi } from './api'

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
      return envelope({ items: [], totalCount: 0 })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('pc 域真实请求形状', () => {
  it('summary：GET /api/v1/pc/summary，date 与 force 走查询串', async () => {
    await pcApi.summary('2026-10-11', true)
    const q = new URL(calls.at(-1)!.url).searchParams
    expect(calls.at(-1)!.url.startsWith(`${BASE}/api/v1/pc/summary?`)).toBe(true)
    expect(q.get('date')).toBe('2026-10-11')
    expect(q.get('force')).toBe('true')
  })

  it('browserTimeline：date 必须出现在查询串里', async () => {
    await pcApi.browserTimeline('2026-10-11')
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/pc/browser-tt/timeline?date=2026-10-11`)
  })

  it('browserImport：POST 正文带契约必填字段 content', async () => {
    await pcApi.browserImport({ content: '{"a":1}', mode: 'add' })
    expect(calls.at(-1)).toMatchObject({
      method: 'POST',
      url: `${BASE}/api/v1/pc/browser-tt/import`,
      body: { content: '{"a":1}', mode: 'add' },
    })
  })

  it('categoryDictionary：GET 契约路径', async () => {
    await pcApi.categoryDictionary()
    expect(calls.at(-1)).toMatchObject({
      method: 'GET',
      url: `${BASE}/api/v1/pc/categories/dictionary`,
    })
  })
})
