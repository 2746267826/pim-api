import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  apiDelete,
  apiFetch,
  clearTokens,
  resetRefreshState,
  setTokens,
} from './client'
import { STORAGE_KEYS, getString, setString } from '@/lib/storage'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

beforeEach(() => {
  resetRefreshState()
  clearTokens()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('apiFetch 基础行为', () => {
  it('解包 ApiResponse 并返回 data 字段', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ code: 0, message: 'ok', data: { a: 1 }, timestamp: 't' })),
    )
    await expect(apiFetch('/api/v1/x')).resolves.toEqual({ a: 1 })
  })

  it('业务错误抛出 ApiError（code=40001, status=400）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse({ code: 40001, message: '客户端名称不能为空', data: null, timestamp: 't' }, 400),
      ),
    )
    const err = await apiFetch('/api/v1/x').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).code).toBe(40001)
    expect((err as ApiError).status).toBe(400)
  })

  it('204 返回 undefined', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 204 })),
    )
    await expect(apiDelete('/api/v1/x')).resolves.toBeUndefined()
  })

  it('HTML 响应（SPA fallback 兜底）抛出可读错误', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response('<html><body>index</body></html>', {
          status: 200,
          headers: { 'content-type': 'text/html' },
        }),
      ),
    )
    await expect(apiFetch('/api/v1/wrong-path')).rejects.toThrow('HTML')
  })

  it('裸对象端点（非 ApiResponse 封装）直接返回 body', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ version: '1.0.0', capabilities: [] })),
    )
    await expect(apiFetch('/api/version', {}, { auth: false })).resolves.toEqual({
      version: '1.0.0',
      capabilities: [],
    })
  })

  it('allowStatuses：409 + Ok 封装正常解包（Outlook 写回冲突语义）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(
          {
            code: 0,
            message: 'ok',
            data: { status: 'conflict', errorCode: 'CONFLICT' },
            timestamp: 't',
          },
          409,
        ),
      ),
    )
    await expect(
      apiFetch('/api/v1/calendar/outlook/events/writeback', { method: 'POST', body: {} }, { allowStatuses: [409, 412] }),
    ).resolves.toMatchObject({ status: 'conflict' })
  })

  it('429 不再自动二次请求：抛 ApiError 并带出服务端 Retry-After 的真实时长', async () => {
    let requests = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        requests += 1
        return jsonResponse({ code: 42901, message: 'too many', data: null }, 429)
      }),
    )
    const err = (await apiFetch('/api/v1/x').catch((e) => e)) as ApiError
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(429)
    // 缺 Retry-After 时也必须是「不重试、时长未知」
    expect(requests).toBe(1)
    expect(err.retryAfterMs).toBeNull()
  })

  it('429 + Retry-After: 900 → 只发一次请求，错误里带 900 秒的真实时长', async () => {
    let requests = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        requests += 1
        return new Response(JSON.stringify({ code: 42901, message: 'too many attempts', data: null }), {
          status: 429,
          headers: { 'content-type': 'application/json', 'retry-after': '900' },
        })
      }),
    )
    const err = (await apiFetch('/api/v1/auth/login').catch((e) => e)) as ApiError
    expect(err).toBeInstanceOf(ApiError)
    expect(err.retryAfterMs).toBe(900_000)
    expect(err.message).toContain('15 分钟')
    expect(requests, '被锁定 15 分钟时又打了一次请求，会再消耗一次失败计数').toBe(1)
  })

  it('Retry-After 支持 HTTP-date（RFC 9110），并被换算成剩余毫秒', async () => {
    const now = Date.now()
    const { parseRetryAfter } = await import('./client')
    expect(parseRetryAfter('120')).toBe(120_000)
    expect(parseRetryAfter(null)).toBeNull()
    expect(parseRetryAfter('not-a-value')).toBeNull()
    expect(parseRetryAfter(new Date(now + 90_000).toUTCString(), now)).toBeGreaterThan(85_000)
    expect(parseRetryAfter(new Date(now - 90_000).toUTCString(), now)).toBe(0)
  })

  it('429 在 allowStatuses 里时不带等待时长（当作正常业务体解包）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse({ code: 0, message: 'ok', data: { limited: true }, timestamp: 't' }, 429),
      ),
    )
    await expect(apiFetch('/api/v1/x', {}, { allowStatuses: [429] })).resolves.toEqual({
      limited: true,
    })
  })
})

describe('401 单飞刷新', () => {
  it('并发 401 只触发一次 refresh，重放均成功', async () => {
    setTokens('tok1', 'rt1')
    let refreshCount = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const headers = new Headers(init?.headers)
      const auth = headers.get('Authorization') ?? ''
      if (url.includes('/auth/refresh')) {
        refreshCount++
        return jsonResponse({
          code: 0,
          message: 'ok',
          data: { accessToken: 'tok2', refreshToken: 'rt2' },
          timestamp: 't',
        })
      }
      if (auth === 'Bearer tok2') {
        return jsonResponse({ code: 0, message: 'ok', data: { ok: true }, timestamp: 't' })
      }
      return new Response(null, { status: 401 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const [r1, r2] = await Promise.all([apiFetch('/api/v1/a'), apiFetch('/api/v1/b')])
    expect(r1).toEqual({ ok: true })
    expect(r2).toEqual({ ok: true })
    expect(refreshCount).toBe(1)
    expect(getString(STORAGE_KEYS.accessToken)).toBe('tok2')
    expect(getString(STORAGE_KEYS.refreshToken)).toBe('rt2')
  })

  it('刷新失败：清令牌 + 抛"登录已过期"', async () => {
    setString(STORAGE_KEYS.refreshToken, 'rt1')
    setTokens('tok1', 'rt1')
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 401 })),
    )
    const err = await apiFetch('/api/v1/a').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).code).toBe(401)
    expect((err as ApiError).message).toContain('登录已过期')
    expect(getString(STORAGE_KEYS.accessToken)).toBeNull()
  })

  it('无 refreshToken 时不发起刷新', async () => {
    clearTokens()
    const fetchMock = vi.fn(async () => new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)
    await expect(apiFetch('/api/v1/a')).rejects.toMatchObject({ code: 401 })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
