/*
 * 全站 HTTP 核心（04 §8 的直接实现）：
 * - Bearer 注入；FormData 不带 JSON 头
 * - 统一解包 { code, message, data }；204 → undefined
 * - 任意请求首个 401 → 共享单次刷新（并发 401 只发起一次）→ 重放一次
 * - HTML 响应识别（SPA fallback 兜底保护）
 * - allowStatuses：Outlook 写回 409/412 走正常解包
 */
import { apiUrl } from '@/lib/apiBase'
import { detectHostChannel } from '@/lib/host-channel'
import { getHostSession } from '@/lib/host-session'
import { STORAGE_KEYS, getString, remove, setString } from '@/lib/storage'
import type { ApiResponse } from './types'

export class ApiError extends Error {
  /** 业务错误码（body.code）；非封装响应为 -1 */
  readonly code: number
  /** HTTP 状态码 */
  readonly status: number
  /**
   * 服务端 Retry-After 要求的等待时长（毫秒）。
   * 仅当响应带可解析的 Retry-After 时非空；429 时必须原样透出，
   * 不得在客户端截断成一个更短的值（那会立刻再打一次、继续消耗失败计数）。
   */
  readonly retryAfterMs: number | null

  constructor(message: string, code = -1, status = 0, retryAfterMs: number | null = null) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
    this.retryAfterMs = retryAfterMs
  }
}

/**
 * 解析 Retry-After（RFC 9110：delta-seconds 或 HTTP-date）。
 * 返回毫秒；缺失/非法返回 null。HTTP-date 会减去当前时间（不早于 0）。
 */
export function parseRetryAfter(value: string | null, nowMs = Date.now()): number | null {
  if (value === null) return null
  const raw = value.trim()
  if (raw === '') return null
  if (/^\d+$/.test(raw)) return Number(raw) * 1000
  const at = Date.parse(raw)
  if (Number.isNaN(at)) return null
  return Math.max(0, at - nowMs)
}

/** 把等待时长写成可读文案（登录页/错误信息共用） */
export function formatRetryAfter(ms: number): string {
  if (ms >= 60_000) {
    const minutes = Math.round(ms / 60_000)
    return `${minutes} 分钟`
  }
  return `${Math.max(1, Math.round(ms / 1000))} 秒`
}

export function getAccessToken(): string | undefined {
  return getString(STORAGE_KEYS.accessToken) ?? undefined
}

export function getRefreshToken(): string | undefined {
  return getString(STORAGE_KEYS.refreshToken) ?? undefined
}

export function setTokens(accessToken: string, refreshToken: string): void {
  setString(STORAGE_KEYS.accessToken, accessToken)
  setString(STORAGE_KEYS.refreshToken, refreshToken)
}

export function clearTokens(): void {
  remove(STORAGE_KEYS.accessToken)
  remove(STORAGE_KEYS.refreshToken)
}

/** 刷新失败/登出广播（AuthContext 监听后清用户态，路由守卫自动回登录页） */
export function broadcastUnauthorized(): void {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('pim:unauthorized'))
  }
}

export interface FetchOptions {
  /** 是否携带 Bearer（默认 true；匿名端点可显式 false） */
  auth?: boolean
  /** 视为"可正常解包业务体"的 HTTP 状态（如 Outlook 写回的 409/412） */
  allowStatuses?: number[]
  signal?: AbortSignal
}

interface RawInit {
  method?: string
  body?: unknown
  headers?: Record<string, string>
}

async function rawFetch(path: string, init: RawInit, opts: FetchOptions): Promise<Response> {
  const headers = new Headers(init.headers)
  const isForm = typeof FormData !== 'undefined' && init.body instanceof FormData
  if (init.body !== undefined && !isForm) {
    headers.set('Content-Type', 'application/json')
  }
  /*
   * host 通道（规格 §6.1）：令牌不在页面里，页面**不读 storage、也不加 Authorization**。
   * Bearer 由 Windows 宿主的请求头过滤器加在允许的 /api/ 请求上，所以这里连
   * getAccessToken() 都不能调用（那会读 pim.accessToken）。
   * 基址用文档 origin（见 apiBase.apiUrl 的 host 分支），不用 localStorage 的 pim.apiBase。
   */
  const host = detectHostChannel() === 'host'
  if (!host && (opts.auth ?? true) && !headers.has('Authorization')) {
    const token = getAccessToken()
    if (token) headers.set('Authorization', `Bearer ${token}`)
  }
  return fetch(apiUrl(path), {
    method: init.method ?? (init.body !== undefined ? 'POST' : 'GET'),
    headers,
    body: isForm
      ? (init.body as FormData)
      : init.body !== undefined
        ? JSON.stringify(init.body)
        : undefined,
    signal: opts.signal,
  })
}

/* ── 401 共享单次刷新 ─────────────────────────────────────── */

let refreshPromise: Promise<boolean> | null = null

/** 测试辅助：复位共享刷新 Promise */
export function resetRefreshState(): void {
  refreshPromise = null
}

async function doRefresh(): Promise<boolean> {
  /*
   * host 通道（规格 §6.2）：页面没有 refresh token，也不回退浏览器端点。
   * 发一条 `page.refresh`，等匹配 requestId 的 `host.refresh.result`，最长 8 秒。
   * 只有 ok:true 才算刷新成功；invalid/transient/unauthenticated/超时都算失败，
   * 但**不清令牌、不广播浏览器登出**（会话只由 host.session.ended 结束）。
   */
  if (detectHostChannel() === 'host') {
    const session = getHostSession()
    if (!session) return false
    return (await session.refresh()) === 'ok'
  }
  const refreshToken = getRefreshToken()
  if (!refreshToken) return false
  try {
    const res = await fetch(apiUrl('/api/v1/auth/refresh'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
    if (!res.ok) return false
    const body = (await res.json()) as ApiResponse<{ accessToken: string; refreshToken: string }>
    if (body.code !== 0) return false
    setTokens(body.data.accessToken, body.data.refreshToken)
    return true
  } catch {
    return false
  }
}

function refreshOnce(): Promise<boolean> {
  refreshPromise ??= doRefresh().finally(() => {
    // 让并发等待者都能拿到同一结果，下一个事件循环后复位
    setTimeout(() => {
      refreshPromise = null
    }, 0)
  })
  return refreshPromise
}

/* ── 核心请求 ─────────────────────────────────────────────── */

export async function apiFetch<T>(
  path: string,
  init: RawInit = {},
  opts: FetchOptions = {},
): Promise<T> {
  const allow = new Set(opts.allowStatuses ?? [])
  let res = await rawFetch(path, init, opts)

  if (res.status === 401 && (opts.auth ?? true) && !allow.has(401)) {
    const refreshed = await refreshOnce()
    if (!refreshed) {
      /*
       * host 通道不得清令牌/广播浏览器登出（规格 §6.2）：页面上的令牌 storage 本来
       * 就不该被碰；会话结束只由匹配当前 handshakeId 的 `host.session.ended` 表示。
       * 请求本身失败，由调用方显示可重试错误或宿主未登录说明。
       */
      if (detectHostChannel() === 'host') {
        throw new ApiError('宿主会话刷新失败，请重试或重新登录', 401, 401)
      }
      clearTokens()
      broadcastUnauthorized()
      throw new ApiError('登录已过期，请重新登录', 401, 401)
    }
    res = await rawFetch(path, init, opts)
  }

  /*
   * 429 限流：**不在客户端自动重试**。
   * 服务端给出的是真实等待时长（pim-api AuthEndpoints.cs:118-125：同 IP 15 分钟内
   * 失败 ≥5 次 → 429 + `Retry-After: 900`）。旧实现把它截断到最多 5 秒后立刻再打一次，
   * 既等不够又额外消耗一次失败计数。现在把等待时长原样放进 ApiError，由调用方
   * （如登录页）决定怎么提示用户。
   */
  const retryAfterMs =
    res.status === 429 && !allow.has(429) ? parseRetryAfter(res.headers.get('retry-after')) : null
  const withWait = (message: string): string =>
    retryAfterMs === null ? message : `${message}（服务端要求等待约 ${formatRetryAfter(retryAfterMs)}）`

  if (res.status === 204) return undefined as T

  const contentType = res.headers.get('content-type') ?? ''
  const text = await res.text()
  if (contentType.includes('text/html') || (!contentType.includes('json') && text.trimStart().startsWith('<'))) {
    throw new ApiError('接口返回了 HTML 而非 JSON：服务器地址可能不正确，或后端未启动', -1, res.status)
  }

  let body: unknown = undefined
  if (text) {
    try {
      body = JSON.parse(text)
    } catch {
      throw new ApiError('响应不是合法 JSON', -1, res.status)
    }
  }

  const statusOk = res.ok || allow.has(res.status)

  if (body != null && typeof body === 'object' && 'code' in (body as Record<string, unknown>)) {
    const envelope = body as ApiResponse<unknown>
    if (envelope.code !== 0) {
      throw new ApiError(
        withWait(envelope.message || `请求失败（HTTP ${res.status}）`),
        envelope.code,
        res.status,
        retryAfterMs,
      )
    }
    return envelope.data as T
  }

  // 裸对象端点（/api/version、/api/client/shell/latest 等，非 ApiResponse 封装）
  if (!statusOk) {
    throw new ApiError(withWait(`请求失败（HTTP ${res.status}）`), -1, res.status, retryAfterMs)
  }
  return body as T
}

export function apiGet<T>(path: string, opts?: FetchOptions): Promise<T> {
  return apiFetch<T>(path, {}, opts)
}

export function apiPost<T>(path: string, body: unknown = {}, opts?: FetchOptions): Promise<T> {
  return apiFetch<T>(path, { method: 'POST', body }, opts)
}

export function apiPut<T>(path: string, body: unknown = {}, opts?: FetchOptions): Promise<T> {
  return apiFetch<T>(path, { method: 'PUT', body }, opts)
}

export function apiDelete<T>(path: string, opts?: FetchOptions): Promise<T> {
  return apiFetch<T>(path, { method: 'DELETE' }, opts)
}

/** multipart 上传 */
export function apiUpload<T>(path: string, form: FormData, opts?: FetchOptions): Promise<T> {
  return apiFetch<T>(path, { method: 'POST', body: form }, opts)
}

export interface BlobFetchOptions {
  /**
   * 401 时是否复用统一刷新（与 apiFetch 同一套单飞）并重放一次。
   * 默认 false = 浏览器通道今天的行为（只贴一次 Bearer、不刷新），缩略图等场景保持原样；
   * ICS 导出等必须走统一传输的下载通道显式打开（PW-08）。
   * host 通道忽略本开关：页面没有 refresh token，401 一律走同一套宿主刷新（规格 §6.2）。
   */
  refreshOn401?: boolean
}

/**
 * 带认证的裸 fetch（Blob 下载通道，03 §5）：
 * 用于 img/iframe 带不了认证头的场景（缩略图、导出 JSON、ICS 等）。
 * 刻意不用 apiFetch 解包——响应是二进制或 302 直链。
 */
export async function fetchBlob(
  path: string,
  signal?: AbortSignal,
  opts: BlobFetchOptions = {},
): Promise<Blob> {
  const host = detectHostChannel() === 'host'
  const authHeaders = (): Record<string, string> => {
    const headers: Record<string, string> = {}
    // host 通道：页面不读令牌也不带 Authorization（Bearer 由宿主请求头过滤器加）
    if (host) return headers
    const token = getAccessToken()
    if (token) headers.Authorization = `Bearer ${token}`
    return headers
  }
  let res = await fetch(apiUrl(path), { headers: authHeaders(), signal })
  if (res.status === 401 && (opts.refreshOn401 || host)) {
    const refreshed = await refreshOnce()
    if (!refreshed) {
      if (host) throw new ApiError('宿主会话刷新失败，请重试或重新登录', 401, 401)
      clearTokens()
      broadcastUnauthorized()
      throw new ApiError('登录已过期，请重新登录', 401, 401)
    }
    res = await fetch(apiUrl(path), { headers: authHeaders(), signal })
  }
  if (!res.ok) {
    throw new ApiError(`下载失败（HTTP ${res.status}）`, -1, res.status)
  }
  return res.blob()
}
