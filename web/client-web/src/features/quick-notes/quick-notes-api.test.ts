/*
 * PW-19：quick-notes 域的契约形状测试（真实 method/path/query/body，含 multipart）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { quickNotesApi } from './api'

const BASE = 'http://192.0.2.10:5858'
let calls: { url: string; method: string; body: unknown; contentType: string | null }[] = []

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
        body: init?.body instanceof FormData ? 'formdata' : typeof init?.body === 'string' ? JSON.parse(init.body) : null,
        contentType: new Headers(init?.headers).get('content-type'),
      })
      return envelope({ id: 'n-1', items: [], totalCount: 0 })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('quick-notes 域真实请求形状', () => {
  it('list：默认分页参数按契约发送，status=all 时不发送 status', async () => {
    await quickNotesApi.list({ status: 'all', search: '审计' })
    const call = calls.at(-1)!
    expect(call.method).toBe('GET')
    expect(call.url.startsWith(`${BASE}/api/v1/quick-notes?`)).toBe(true)
    const q = new URL(call.url).searchParams
    expect(q.has('status')).toBe(false)
    expect(q.get('search')).toBe('审计')
    expect(q.get('page')).toBe('1')
    expect(q.get('pageSize')).toBe('50')
  })

  it('create：POST /api/v1/quick-notes，正文是契约字段', async () => {
    await quickNotesApi.create({ contentMarkdown: '# 标题' })
    expect(calls.at(-1)).toMatchObject({
      method: 'POST',
      url: `${BASE}/api/v1/quick-notes`,
      body: { contentMarkdown: '# 标题' },
      contentType: 'application/json',
    })
  })

  it('update：PUT 到带 id 的路径', async () => {
    await quickNotesApi.update('n-1', { contentMarkdown: 'x' })
    expect(calls.at(-1)).toMatchObject({
      method: 'PUT',
      url: `${BASE}/api/v1/quick-notes/n-1`,
    })
  })

  it('uploadAttachment：multipart 不得带 JSON Content-Type', async () => {
    const file = new File(['x'], 'a.txt', { type: 'text/plain' })
    await quickNotesApi.uploadAttachment(file)
    const call = calls.at(-1)!
    expect(call.url).toBe(`${BASE}/api/v1/quick-notes/attachments`)
    expect(call.method).toBe('POST')
    expect(call.body).toBe('formdata')
    expect(call.contentType, 'multipart 请求被错误地标成 JSON').not.toBe('application/json')
  })
})
