/*
 * PW-19：files 域的契约形状测试（真实 method/path/body）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { filesApi } from './api'

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

describe('files 域真实请求形状', () => {
  it('providers：GET /api/v1/files/providers', async () => {
    await filesApi.providers()
    expect(calls.at(-1)).toMatchObject({
      method: 'GET',
      url: `${BASE}/api/v1/files/providers`,
    })
  })

  it('openLink：只发契约内的路径，不带契约外的 mode=view（PW-N1）', async () => {
    await filesApi.openLink('f-1')
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/files/items/f-1/open-link`)
    expect(calls.at(-1)!.url).not.toContain('mode=')
  })

  it('createFolder：POST 正文是契约字段 path + providerId（多账号必须指明落点）', async () => {
    await filesApi.createFolder('/a/b', 'acc-1')
    expect(calls.at(-1)).toMatchObject({
      method: 'POST',
      url: `${BASE}/api/v1/files/folders`,
      body: { path: '/a/b', providerId: 'acc-1' },
    })
  })

  it('search：关键词与契约内的 mode=keyword 查询串（PW-N1 只删了 open-link 的 mode）', async () => {
    await filesApi.search('审计')
    const url = new URL(calls.at(-1)!.url)
    expect(url.pathname).toBe('/api/v1/files/search')
    expect(url.searchParams.get('q')).toBe('审计')
    expect(url.searchParams.get('mode')).toBe('keyword')
    expect(url.searchParams.get('page')).toBe('1')
    expect(url.searchParams.get('pageSize')).toBe('100')
  })
})
