import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  apiDelete,
  apiFetch,
  apiPutBinary,
  clearTokens,
  fetchBoundedDownload,
  fetchBlob,
  resetRefreshState,
  setTokens,
} from './client'
import { getApiBase, setApiBase } from '@/lib/apiBase'
import { STORAGE_KEYS, getString, setString } from '@/lib/storage'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function deferred<T>(): {
  promise: Promise<T>
  resolve: (value: T) => void
} {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

function responseWithCancelSpy(status: number, headers: HeadersInit = {}): {
  response: Response
  cancel: ReturnType<typeof vi.fn>
} {
  const cancel = vi.fn(() => Promise.resolve())
  return {
    response: {
      ok: status >= 200 && status < 300,
      status,
      headers: new Headers(headers),
      body: { cancel },
    } as unknown as Response,
    cancel,
  }
}

beforeEach(() => {
  resetRefreshState()
  setApiBase('')
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

  it('二进制 PUT 保留原始 Blob，不经过 JSON.stringify', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => jsonResponse({
      code: 0,
      message: 'ok',
      data: '分块已保存',
      timestamp: 't',
    }))
    vi.stubGlobal('fetch', fetchMock)
    const blob = new Blob([new Uint8Array([1, 2, 3])], { type: 'application/octet-stream' })

    await expect(apiPutBinary('/api/v1/native-notes/part', blob)).resolves.toBe('分块已保存')
    const init = fetchMock.mock.calls[0]?.[1] as RequestInit
    expect(init.body).toBe(blob)
    expect(new Headers(init.headers).get('Content-Type')).toBe('application/octet-stream')
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

describe('有界原始材料下载', () => {
  const headers = {
    'content-type': 'application/zip',
    'content-disposition': 'attachment; filename="22222222-2222-4222-8222-222222222222.pimnote.zip"',
  }
  const options = {
    expectedFilename: '22222222-2222-4222-8222-222222222222.pimnote.zip',
    maxBytes: 4,
    refreshOn401: true,
  }

  it('无 Content-Length 时逐块累计并在预算外拒绝', async () => {
    const fetchMock = vi.fn(async () => new Response(new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new Uint8Array([1, 2, 3]))
        controller.enqueue(new Uint8Array([4, 5]))
        controller.close()
      },
    }), { headers }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(fetchBoundedDownload('/api/v1/native-notes/versions/x/bundle', undefined, options))
      .rejects.toThrow('字节预算')
  })

  it('Content-Length 超预算时不读取响应体', async () => {
    const getReader = vi.fn()
    const cancel = vi.fn(async () => undefined)
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      status: 200,
      headers: new Headers({ ...headers, 'content-length': '5' }),
      body: { getReader, cancel },
    } as unknown as Response)))

    await expect(fetchBoundedDownload('/api/v1/native-notes/versions/x/bundle', undefined, options))
      .rejects.toThrow('字节预算')
    expect(getReader).not.toHaveBeenCalled()
    expect(cancel).toHaveBeenCalledOnce()
  })

  it.each([
    ['无效预算', { maxBytes: 0 }, headers],
    ['无效 Content-Length', {}, { ...headers, 'content-length': '1.5' }],
  ])('%s 时取消尚未读取的响应体', async (_label, override, responseHeaders) => {
    const { response, cancel } = responseWithCancelSpy(200, responseHeaders)
    vi.stubGlobal('fetch', vi.fn(async () => response))

    await expect(fetchBoundedDownload('/bundle', undefined, {
      ...options,
      ...override,
    })).rejects.toThrow()
    expect(cancel).toHaveBeenCalledOnce()
  })

  it('401 刷新替换原响应前取消未消费的响应体', async () => {
    setTokens('old-access', 'refresh-token')
    const unauthorized = responseWithCancelSpy(401)
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/auth/refresh')) {
        return jsonResponse({
          code: 0,
          message: 'ok',
          data: { accessToken: 'new-access', refreshToken: 'new-refresh' },
          timestamp: 't',
        })
      }
      if (new Headers(init?.headers).get('Authorization') === 'Bearer new-access') {
        return new Response(new Uint8Array([1]), {
          headers: { ...headers, 'content-length': '1' },
        })
      }
      return unauthorized.response
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(fetchBoundedDownload('/bundle', undefined, options)).resolves.toMatchObject({
      filename: options.expectedFilename,
    })
    expect(unauthorized.cancel).toHaveBeenCalledOnce()
  })

  it('非成功 HTTP 响应抛错前取消未消费的响应体', async () => {
    const failure = responseWithCancelSpy(503)
    vi.stubGlobal('fetch', vi.fn(async () => failure.response))

    await expect(fetchBoundedDownload('/bundle', undefined, options))
      .rejects.toThrow('HTTP 503')
    expect(failure.cancel).toHaveBeenCalledOnce()
  })

  it('拒绝错误 MIME、错误文件名和声明长度不匹配', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(new Uint8Array([1]), {
      headers: { ...headers, 'content-type': 'text/html' },
    })))
    await expect(fetchBoundedDownload('/bundle', undefined, options)).rejects.toThrow('内容类型')

    vi.stubGlobal('fetch', vi.fn(async () => new Response(new Uint8Array([1]), {
      headers: { ...headers, 'content-disposition': 'attachment; filename="other.zip"' },
    })))
    await expect(fetchBoundedDownload('/bundle', undefined, options)).rejects.toThrow('文件名')

    vi.stubGlobal('fetch', vi.fn(async () => new Response(new Uint8Array([1, 2]), {
      headers: { ...headers, 'content-length': '1' },
    })))
    await expect(fetchBoundedDownload('/bundle', undefined, options)).rejects.toThrow('长度')
  })

  it('流读取期间会话切换后丢弃旧响应', async () => {
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    let active = true
    vi.stubGlobal('fetch', vi.fn(async () => new Response(new ReadableStream<Uint8Array>({
      async pull(controller) {
        controller.enqueue(new Uint8Array([1]))
        await gate
        controller.enqueue(new Uint8Array([2]))
        controller.close()
      },
    }), { headers })))

    const download = fetchBoundedDownload('/bundle', undefined, {
      ...options,
      assertActive: () => {
        if (!active) {
          const error = new Error('workspace changed')
          error.name = 'AbortError'
          throw error
        }
      },
    })
    await Promise.resolve()
    active = false
    release()
    await expect(download).rejects.toMatchObject({ name: 'AbortError' })
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

  it('刷新完成后、重放前重新检查会话围栏', async () => {
    setTokens('tok1', 'rt1')
    let active = true
    let apiCalls = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        active = false
        return jsonResponse({
          code: 0,
          message: 'ok',
          data: { accessToken: 'tok2', refreshToken: 'rt2' },
          timestamp: 't',
        })
      }
      apiCalls += 1
      return new Response(null, { status: 401 })
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(apiFetch('/api/v1/a', {}, {
      assertActive: () => {
        if (!active) {
          const error = new Error('workspace changed')
          error.name = 'AbortError'
          throw error
        }
      },
    })).rejects.toMatchObject({ name: 'AbortError' })
    expect(apiCalls).toBe(1)
  })

  it('Blob 下载同样在 401 刷新后、重放前检查围栏', async () => {
    setTokens('tok1', 'rt1')
    let active = true
    let apiCalls = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        active = false
        return jsonResponse({
          code: 0,
          message: 'ok',
          data: { accessToken: 'tok2', refreshToken: 'rt2' },
          timestamp: 't',
        })
      }
      apiCalls += 1
      return new Response(null, { status: 401 })
    }))

    await expect(fetchBlob('/api/v1/blob', undefined, {
      refreshOn401: true,
      assertActive: () => {
        if (!active) {
          const error = new Error('workspace changed')
          error.name = 'AbortError'
          throw error
        }
      },
    })).rejects.toMatchObject({ name: 'AbortError' })
    expect(apiCalls).toBe(1)
  })

  it('旧账号晚到的刷新成功不能覆盖新账号凭据', async () => {
    setApiBase('https://same.example')
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    let apiCalls = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        refreshStarted()
        return refreshResponse.promise
      }
      apiCalls += 1
      return new Response(null, { status: 401 })
    }))

    const request = apiFetch('/api/v1/notes')
    await started
    setTokens('access-new', 'refresh-new')
    refreshResponse.resolve(jsonResponse({
      code: 0,
      message: 'ok',
      data: { accessToken: 'access-stale', refreshToken: 'refresh-stale' },
      timestamp: 't',
    }))

    await expect(request).rejects.toMatchObject({ code: 401 })
    expect(getString(STORAGE_KEYS.accessToken)).toBe('access-new')
    expect(getString(STORAGE_KEYS.refreshToken)).toBe('refresh-new')
    expect(apiCalls).toBe(1)
  })

  it('切换服务器后的旧刷新不能写回空会话', async () => {
    setApiBase('https://old.example')
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        refreshStarted()
        return refreshResponse.promise
      }
      return new Response(null, { status: 401 })
    }))

    const request = apiFetch('/api/v1/notes')
    await started
    setApiBase('https://new.example')
    clearTokens()
    refreshResponse.resolve(jsonResponse({
      code: 0,
      message: 'ok',
      data: { accessToken: 'access-stale', refreshToken: 'refresh-stale' },
      timestamp: 't',
    }))

    await expect(request).rejects.toMatchObject({ code: 401 })
    expect(getApiBase()).toBe('https://new.example')
    expect(getString(STORAGE_KEYS.accessToken)).toBeNull()
    expect(getString(STORAGE_KEYS.refreshToken)).toBeNull()
  })

  it('退出登录后的旧刷新失败不能恢复会话', async () => {
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        refreshStarted()
        return refreshResponse.promise
      }
      return new Response(null, { status: 401 })
    }))

    const request = apiFetch('/api/v1/notes')
    await started
    clearTokens()
    refreshResponse.resolve(new Response(null, { status: 500 }))

    await expect(request).rejects.toMatchObject({ code: 401 })
    expect(getString(STORAGE_KEYS.accessToken)).toBeNull()
    expect(getString(STORAGE_KEYS.refreshToken)).toBeNull()
  })

  it('旧刷新失败不能清除已经切换的新账号凭据', async () => {
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/auth/refresh')) {
        refreshStarted()
        return refreshResponse.promise
      }
      return new Response(null, { status: 401 })
    }))

    const request = apiFetch('/api/v1/notes')
    await started
    setTokens('access-new', 'refresh-new')
    refreshResponse.resolve(new Response(null, { status: 500 }))

    await expect(request).rejects.toMatchObject({ code: 401 })
    expect(getString(STORAGE_KEYS.accessToken)).toBe('access-new')
    expect(getString(STORAGE_KEYS.refreshToken)).toBe('refresh-new')
  })

  it('同账号自然刷新成功后按新 token 重放', async () => {
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    let apiCalls = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/auth/refresh')) {
        refreshStarted()
        return refreshResponse.promise
      }
      apiCalls += 1
      const auth = new Headers(init?.headers).get('Authorization')
      if (auth === 'Bearer access-new') {
        return jsonResponse({ code: 0, message: 'ok', data: { ok: true }, timestamp: 't' })
      }
      return new Response(null, { status: 401 })
    }))

    const request = apiFetch('/api/v1/notes')
    await started
    refreshResponse.resolve(jsonResponse({
      code: 0,
      message: 'ok',
      data: { accessToken: 'access-new', refreshToken: 'refresh-new' },
      timestamp: 't',
    }))

    await expect(request).resolves.toEqual({ ok: true })
    expect(getString(STORAGE_KEYS.accessToken)).toBe('access-new')
    expect(getString(STORAGE_KEYS.refreshToken)).toBe('refresh-new')
    expect(apiCalls).toBe(2)
  })

  it('并发请求共享同一个等待中的刷新结果', async () => {
    setTokens('access-old', 'refresh-old')
    const refreshResponse = deferred<Response>()
    let refreshStarted!: () => void
    const started = new Promise<void>((resolve) => {
      refreshStarted = resolve
    })
    let refreshCount = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/auth/refresh')) {
        refreshCount += 1
        refreshStarted()
        return refreshResponse.promise
      }
      const auth = new Headers(init?.headers).get('Authorization')
      if (auth === 'Bearer access-new') {
        return jsonResponse({ code: 0, message: 'ok', data: { ok: true }, timestamp: 't' })
      }
      return new Response(null, { status: 401 })
    }))

    const first = apiFetch('/api/v1/notes/a')
    const second = apiFetch('/api/v1/notes/b')
    await started
    expect(refreshCount).toBe(1)
    refreshResponse.resolve(jsonResponse({
      code: 0,
      message: 'ok',
      data: { accessToken: 'access-new', refreshToken: 'refresh-new' },
      timestamp: 't',
    }))

    await expect(Promise.all([first, second])).resolves.toEqual([{ ok: true }, { ok: true }])
    expect(refreshCount).toBe(1)
    expect(getString(STORAGE_KEYS.accessToken)).toBe('access-new')
  })
})
