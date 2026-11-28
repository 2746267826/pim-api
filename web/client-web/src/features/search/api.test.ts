import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { searchApi } from './api'

const BASE = 'https://192.0.2.10:5858'
let urls: string[] = []

beforeEach(() => {
  urls = []
  setApiBase(BASE)
  clearTokens()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    urls.push(String(input))
    return new Response(JSON.stringify({
      code: 0,
      message: 'ok',
      data: {
        items: [],
        page: 1,
        pageSize: 20,
        totalCount: 0,
        totalPages: 0,
        query: '研究',
        scope: 'all',
        match: 'all',
        sort: 'relevance',
        groups: [],
        generatedAt: '2026-11-25T00:00:00Z',
      },
    }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  }))
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('searchApi', () => {
  it('sends the unified global-search contract without legacy-only parameters', async () => {
    await searchApi.query({
      q: '研究 方法',
      scope: 'event',
      match: 'any',
      sort: 'recent_edited',
      page: 3,
      pageSize: 50,
    })

    const url = new URL(urls.at(-1)!)
    expect(url.origin + url.pathname).toBe(`${BASE}/api/v1/search`)
    expect(url.searchParams.get('q')).toBe('研究 方法')
    expect(url.searchParams.get('scope')).toBe('event')
    expect(url.searchParams.get('match')).toBe('any')
    expect(url.searchParams.get('sort')).toBe('recent_edited')
    expect(url.searchParams.get('page')).toBe('3')
    expect(url.searchParams.get('pageSize')).toBe('50')
    expect(url.searchParams.has('type')).toBe(false)
    expect(url.searchParams.has('limit')).toBe(false)
  })
})
