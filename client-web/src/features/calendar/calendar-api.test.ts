/*
 * PW-19：calendar 域的契约形状测试（真实 method/path/query）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearTokens } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import { calendarsApi, eventsApi } from './api'

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
      return envelope({ items: [], totalCount: 0, id: 'x' })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  setApiBase('')
  clearTokens()
})

describe('calendar 域真实请求形状', () => {
  it('calendarsApi.list：GET /api/v1/calendar/calendars，kind 走查询串', async () => {
    await calendarsApi.list('calendar')
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/calendar/calendars?kind=calendar`)
    await calendarsApi.list()
    expect(calls.at(-1)!.url).toBe(`${BASE}/api/v1/calendar/calendars`)
  })

  it('calendarsApi.setMutePolicy：PUT 到 /calendars/{id}/mute-policy，body 只有 isMuted', async () => {
    await calendarsApi.setMutePolicy('cal-9', false)
    const call = calls.at(-1)!
    expect(call.url).toBe(`${BASE}/api/v1/calendar/calendars/cal-9/mute-policy`)
    expect(call.method).toBe('PUT')
    expect(call.body).toEqual({ isMuted: false })
  })

  it('eventsApi.listPaged：必须带 start/end 与 page/pageSize（双态端点的分页分支）', async () => {
    await eventsApi.listPaged({ start: '2026-10-01T00:00:00Z', end: '2026-10-02T00:00:00Z' })
    const q = new URL(calls.at(-1)!.url).searchParams
    expect(calls.at(-1)!.url.startsWith(`${BASE}/api/v1/calendar/events?`)).toBe(true)
    expect(q.get('start')).toBe('2026-10-01T00:00:00Z')
    expect(q.get('end')).toBe('2026-10-02T00:00:00Z')
    expect(q.get('page')).toBe('1')
    expect(q.get('pageSize')).toBe('100')
  })

  it('eventsApi.update：PUT 到带 id 的路径，scope 走查询串且正文是调用方对象', async () => {
    // 契约的 UpdateEventRequest 要求 title/calendarId/dtStart/dtEnd 必填，
    // 这里给一个契约合法的完整草稿，断言仍检查「正文原样透传」。
    const draft = {
      title: 'x',
      calendarId: '00000000-0000-0000-0000-0000000000aa',
      dtStart: '2026-10-01T00:00:00Z',
      dtEnd: '2026-10-01T01:00:00Z',
      description: null,
    }
    await eventsApi.update('ev-1', draft, { scope: 'series' })
    expect(calls.at(-1)).toMatchObject({
      method: 'PUT',
      url: `${BASE}/api/v1/calendar/events/ev-1?scope=series`,
      body: draft,
    })
  })

  it('eventsApi.remove：DELETE 到契约路径', async () => {
    await eventsApi.remove('ev-1')
    expect(calls.at(-1)).toMatchObject({
      method: 'DELETE',
      url: `${BASE}/api/v1/calendar/events/ev-1`,
    })
  })
})
