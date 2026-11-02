/*
 * 全站 HTTP 核心（04 §8 的直接实现）：
 * - Bearer 注入；FormData 不带 JSON 头
 * - 统一解包 { code, message, data }；204 → undefined
 * - 任意请求首个 401 → 共享单次刷新（并发 401 只发起一次）→ 重放一次
 * - HTML 响应识别（SPA fallback 兜底保护）
 * - allowStatuses：Outlook 写回 409/412 走正常解包
 */
import { apiUrl, getApiBase } from '@/lib/apiBase'
import { authSessionRevision, markAuthSessionChanged } from '@/lib/auth-session-revision'
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

  /** 已真实收到的网关响应头标识；仅用于传输证据，不推断 provider 结果。 */
  readonly gatewayName: string | null

  constructor(message: string, code = -1, status = 0, retryAfterMs: number | null = null,
    gatewayName: string | null = null) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
    this.retryAfterMs = retryAfterMs
    this.gatewayName = gatewayName
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
  markAuthSessionChanged()
}

export function clearTokens(): void {
  remove(STORAGE_KEYS.accessToken)
  remove(STORAGE_KEYS.refreshToken)
  markAuthSessionChanged()
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
  /** Additional session fence for long-lived flows that must not cross owner/server changes. */
  assertActive?: () => void
}

interface RawInit {
  method?: string
  body?: unknown
  bodyKind?: 'json' | 'form' | 'binary'
  contentType?: string
  headers?: Record<string, string>
}

async function rawFetch(path: string, init: RawInit, opts: FetchOptions): Promise<Response> {
  assertRequestActive(opts)
  const headers = new Headers(init.headers)
  const isForm = init.bodyKind === 'form' ||
    (init.bodyKind == null && typeof FormData !== 'undefined' && init.body instanceof FormData)
  const isBinary = init.bodyKind === 'binary'
  if (init.body !== undefined && !isForm && !isBinary) {
    headers.set('Content-Type', 'application/json')
  } else if (isBinary) {
    headers.set('Content-Type', init.contentType ?? 'application/octet-stream')
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
  assertRequestActive(opts)
  return fetch(apiUrl(path), {
    method: init.method ?? (init.body !== undefined ? 'POST' : 'GET'),
    headers,
    body: isForm
      ? (init.body as BodyInit)
      : isBinary
        ? (init.body as BodyInit)
      : init.body !== undefined
        ? JSON.stringify(init.body)
        : undefined,
    signal: opts.signal,
  })
}

function assertRequestActive(opts: Pick<FetchOptions, 'signal' | 'assertActive'>): void {
  if (opts.signal?.aborted) {
    const error = new Error('请求所属会话已结束')
    error.name = 'AbortError'
    throw error
  }
  opts.assertActive?.()
}

/* ── 401 共享单次刷新 ─────────────────────────────────────── */

type RefreshStatus = 'ok' | 'failed' | 'session-changed'

interface AuthSessionSnapshot {
  revision: number
  channel: 'browser' | 'host'
  binding: string
  accessToken?: string
  refreshToken?: string
}

interface RefreshResult {
  status: RefreshStatus
  session: AuthSessionSnapshot
}

let refreshInFlight: { key: string; promise: Promise<RefreshResult> } | null = null
let completedRefresh: { sourceKey: string; result: RefreshResult } | null = null

/** 测试辅助：复位共享刷新 Promise */
export function resetRefreshState(): void {
  refreshInFlight = null
  completedRefresh = null
}

function authSessionBinding(): string {
  if (detectHostChannel() === 'host') {
    const origin = typeof window === 'undefined' ? 'no-origin' : window.location.origin
    const handshake = getHostSession()?.handshake
    return `host:${origin}:${handshake?.handshakeId ?? 'none'}:${handshake?.authenticated ?? false}`
  }
  return `browser:${getApiBase()}`
}

function captureAuthSession(): AuthSessionSnapshot {
  const channel = detectHostChannel()
  if (channel === 'host') {
    // Host credentials stay outside page storage by contract.
    return {
      revision: authSessionRevision(),
      channel,
      binding: authSessionBinding(),
    }
  }
  return {
    revision: authSessionRevision(),
    channel,
    binding: authSessionBinding(),
    // This opaque pair is the browser-side auth-session identity. It is held in
    // memory only and is never included in errors or logs.
    accessToken: getAccessToken(),
    refreshToken: getRefreshToken(),
  }
}

function sessionStillCurrent(snapshot: AuthSessionSnapshot): boolean {
  if (detectHostChannel() !== snapshot.channel) return false
  return authSessionRevision() === snapshot.revision
    && authSessionBinding() === snapshot.binding
    && (snapshot.channel === 'host'
      || (getAccessToken() === snapshot.accessToken
        && getRefreshToken() === snapshot.refreshToken))
}

function refreshResult(
  status: RefreshStatus,
  snapshot: AuthSessionSnapshot,
): RefreshResult {
  return {
    status: sessionStillCurrent(snapshot) ? status : 'session-changed',
    session: snapshot,
  }
}

async function doRefresh(snapshot: AuthSessionSnapshot): Promise<RefreshResult> {
  /*
   * host 通道（规格 §6.2）：页面没有 refresh token，也不回退浏览器端点。
   * 发一条 `page.refresh`，等匹配 requestId 的 `host.refresh.result`，最长 8 秒。
   * 只有 ok:true 才算刷新成功；invalid/transient/unauthenticated/超时都算失败，
   * 但**不清令牌、不广播浏览器登出**（会话只由 host.session.ended 结束）。
   */
  if (detectHostChannel() === 'host') {
    const session = getHostSession()
    if (!session) return refreshResult('failed', snapshot)
    const outcome = await session.refresh()
    if (!sessionStillCurrent(snapshot)) {
      return { status: 'session-changed', session: snapshot }
    }
    return {
      status: outcome === 'ok' ? 'ok' : 'failed',
      session: captureAuthSession(),
    }
  }
  const refreshToken = snapshot.refreshToken
  if (!refreshToken) return refreshResult('failed', snapshot)
  try {
    const res = await fetch(apiUrl('/api/v1/auth/refresh'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
    if (!res.ok) return refreshResult('failed', snapshot)
    const body = (await res.json()) as ApiResponse<{ accessToken: string; refreshToken: string }>
    if (body.code !== 0) return refreshResult('failed', snapshot)
    // The old refresh may finish after login, logout, or a server switch.
    // Only the exact credential session that issued it may receive its tokens.
    if (!sessionStillCurrent(snapshot)) {
      return { status: 'session-changed', session: snapshot }
    }
    setTokens(body.data.accessToken, body.data.refreshToken)
    return { status: 'ok', session: captureAuthSession() }
  } catch {
    return refreshResult('failed', snapshot)
  }
}

function refreshOnce(snapshot: AuthSessionSnapshot): Promise<RefreshResult> {
  const key = `${snapshot.revision}\0${snapshot.channel}\0${snapshot.binding}`
  if (refreshInFlight?.key === key) return refreshInFlight.promise
  if (completedRefresh?.sourceKey === key
    && completedRefresh.result.status === 'ok'
    && sessionStillCurrent(completedRefresh.result.session)) {
    return Promise.resolve(completedRefresh.result)
  }
  if (!sessionStillCurrent(snapshot)) {
    return Promise.resolve({ status: 'session-changed', session: snapshot })
  }

  const promise = doRefresh(snapshot)
  const flight = { key, promise }
  refreshInFlight = flight
  void promise.then(
    (result) => {
      if (result.status === 'ok') completedRefresh = { sourceKey: key, result }
    },
    () => undefined,
  )
  const clearFlight = (): void => {
    if (refreshInFlight === flight) refreshInFlight = null
  }
  void promise.then(clearFlight, clearFlight)
  return promise
}

function throwRefreshFailure(result: RefreshResult): never {
  if (result.status === 'session-changed' || !sessionStillCurrent(result.session)) {
    // The current credentials belong to a newer session; never clear or replay them.
    throw new ApiError('登录会话已切换，请重新发起请求', 401, 401)
  }
  /*
   * host 通道不得清令牌/广播浏览器登出（规格 §6.2）：页面上的令牌 storage 本来
   * 就不该被碰；会话结束只由匹配当前 handshakeId 的 `host.session.ended` 表示。
   * 请求本身失败，由调用方显示可重试错误或宿主未登录说明。
   */
  if (result.session.channel === 'host') {
    throw new ApiError('宿主会话刷新失败，请重试或重新登录', 401, 401)
  }
  clearTokens()
  broadcastUnauthorized()
  throw new ApiError('登录已过期，请重新登录', 401, 401)
}

/* ── 核心请求 ─────────────────────────────────────────────── */

export async function apiFetch<T>(
  path: string,
  init: RawInit = {},
  opts: FetchOptions = {},
): Promise<T> {
  const allow = new Set(opts.allowStatuses ?? [])
  const requestSession = (opts.auth ?? true) ? captureAuthSession() : undefined
  let res = await rawFetch(path, init, opts)
  assertRequestActive(opts)

  if (res.status === 401 && (opts.auth ?? true) && !allow.has(401)) {
    assertRequestActive(opts)
    const refreshed = await refreshOnce(requestSession ?? captureAuthSession())
    assertRequestActive(opts)
    if (refreshed.status !== 'ok') throwRefreshFailure(refreshed)
    // A refresh can complete while another tab switches account or server.
    // Never replay the old request against credentials it did not refresh.
    if (!sessionStillCurrent(refreshed.session)) throwRefreshFailure(refreshed)
    res = await rawFetch(path, init, opts)
    assertRequestActive(opts)
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
  const gatewayName = (res.headers.get('x-gateway-name') ??
    res.headers.get('via') ?? res.headers.get('server'))?.trim().slice(0, 200) || null
  const withWait = (message: string): string =>
    retryAfterMs === null ? message : `${message}（服务端要求等待约 ${formatRetryAfter(retryAfterMs)}）`

  if (res.status === 204) return undefined as T

  const contentType = res.headers.get('content-type') ?? ''
  const text = await res.text()
  assertRequestActive(opts)
  if (contentType.includes('text/html') || (!contentType.includes('json') && text.trimStart().startsWith('<'))) {
    throw new ApiError('接口返回了 HTML 而非 JSON：服务器地址可能不正确，或后端未启动', -1,
      res.status, retryAfterMs, gatewayName)
  }

  let body: unknown = undefined
  if (text) {
    try {
      body = JSON.parse(text)
    } catch {
      throw new ApiError('响应不是合法 JSON', -1, res.status, retryAfterMs, gatewayName)
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
        gatewayName,
      )
    }
    return envelope.data as T
  }

  // 裸对象端点（/api/version、/api/client/shell/latest 等，非 ApiResponse 封装）
  if (!statusOk) {
    throw new ApiError(withWait(`请求失败（HTTP ${res.status}）`), -1, res.status, retryAfterMs,
      gatewayName)
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
  return apiFetch<T>(path, { method: 'POST', body: form, bodyKind: 'form' }, opts)
}

/** 受认证的二进制 PUT（笔记材料分块等，不能走 JSON.stringify）。 */
export function apiPutBinary<T>(
  path: string,
  body: Blob,
  contentType = 'application/octet-stream',
  opts?: FetchOptions,
): Promise<T> {
  return apiFetch<T>(path, { method: 'PUT', body, bodyKind: 'binary', contentType }, opts)
}

export interface BlobFetchOptions {
  /**
   * 401 时是否复用统一刷新（与 apiFetch 同一套单飞）并重放一次。
   * 默认 false = 浏览器通道今天的行为（只贴一次 Bearer、不刷新），缩略图等场景保持原样；
   * ICS 导出等必须走统一传输的下载通道显式打开（PW-08）。
   * host 通道忽略本开关：页面没有 refresh token，401 一律走同一套宿主刷新（规格 §6.2）。
   */
  refreshOn401?: boolean
  /** Guard the download and any 401 replay against the initiating workspace session. */
  assertActive?: () => void
}

export interface BoundedDownloadOptions extends BlobFetchOptions {
  expectedFilename: string
  maxBytes?: number
  expectedContentType?: string
}

export interface BoundedDownload {
  blob: Blob
  filename: string
}

export const MAX_BOUNDED_DOWNLOAD_BYTES = 128 * 1024 * 1024

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
  const res = await fetchDownloadResponse(path, signal, opts)
  const blob = await res.blob()
  assertRequestActive({ signal, assertActive: opts.assertActive })
  return blob
}

/** Stream a validated download into bounded memory before exposing a Blob. */
export async function fetchBoundedDownload(
  path: string,
  signal: AbortSignal | undefined,
  opts: BoundedDownloadOptions,
): Promise<BoundedDownload> {
  const requestOptions = { signal, assertActive: opts.assertActive }
  const res = await fetchDownloadResponse(path, signal, opts)
  try {
    assertRequestActive(requestOptions)
  } catch (error) {
    discardDownloadBody(res)
    throw error
  }

  const expectedType = (opts.expectedContentType ?? 'application/zip').toLowerCase()
  const actualType = (res.headers.get('content-type') ?? '').split(';', 1)[0]?.trim().toLowerCase()
  if (actualType !== expectedType) {
    discardDownloadBody(res)
    throw new ApiError('下载内容类型不匹配，未保存文件', -1, res.status)
  }

  let filename: string
  try {
    filename = parseDownloadFilename(res.headers.get('content-disposition'))
  } catch (error) {
    discardDownloadBody(res)
    throw error
  }
  if (filename !== opts.expectedFilename || !isSafeDownloadFilename(filename)) {
    discardDownloadBody(res)
    throw new ApiError('下载文件名与固定材料版本不匹配，未保存文件', -1, res.status)
  }

  const maxBytes = opts.maxBytes ?? MAX_BOUNDED_DOWNLOAD_BYTES
  if (!Number.isSafeInteger(maxBytes) || maxBytes <= 0) {
    discardDownloadBody(res)
    throw new RangeError('下载预算必须是正安全整数')
  }
  const rawLength = res.headers.get('content-length')
  let declaredLength: number | undefined
  if (rawLength !== null) {
    if (!/^(0|[1-9]\d*)$/.test(rawLength.trim())) {
      discardDownloadBody(res)
      throw new ApiError('下载长度标头无效，未保存文件', -1, res.status)
    }
    declaredLength = Number(rawLength)
    if (!Number.isSafeInteger(declaredLength) || declaredLength > maxBytes) {
      discardDownloadBody(res)
      throw new ApiError('下载内容超过允许的字节预算', -1, res.status)
    }
  }
  if (!res.body) throw new ApiError('下载流不可用，未保存文件', -1, res.status)

  const reader = res.body.getReader()
  const chunks: Uint8Array<ArrayBuffer>[] = []
  let total = 0
  try {
    while (true) {
      assertRequestActive(requestOptions)
      const { done, value } = await reader.read()
      assertRequestActive(requestOptions)
      if (done) break
      if (!value) continue
      total += value.byteLength
      if (total > maxBytes) {
        void reader.cancel().catch(() => undefined)
        throw new ApiError('下载内容超过允许的字节预算', -1, res.status)
      }
      const chunk = new Uint8Array(value.byteLength)
      chunk.set(value)
      chunks.push(chunk)
    }
  } catch (error) {
    void reader.cancel().catch(() => undefined)
    throw error
  } finally {
    reader.releaseLock()
  }
  assertRequestActive(requestOptions)
  if (total === 0 || (declaredLength !== undefined && total !== declaredLength)) {
    throw new ApiError('下载长度与响应标头不一致，未保存文件', -1, res.status)
  }
  return { blob: new Blob(chunks, { type: expectedType }), filename }
}

async function fetchDownloadResponse(
  path: string,
  signal: AbortSignal | undefined,
  opts: BlobFetchOptions,
): Promise<Response> {
  const requestOptions = { signal, assertActive: opts.assertActive }
  assertRequestActive(requestOptions)
  const host = detectHostChannel() === 'host'
  const requestSession = (opts.refreshOn401 || host) ? captureAuthSession() : undefined
  const authHeaders = (): Record<string, string> => {
    const headers: Record<string, string> = {}
    // host 通道：页面不读令牌也不带 Authorization（Bearer 由宿主请求头过滤器加）
    if (host) return headers
    const token = getAccessToken()
    if (token) headers.Authorization = `Bearer ${token}`
    return headers
  }
  let res = await fetch(apiUrl(path), { headers: authHeaders(), signal })
  try {
    assertRequestActive(requestOptions)
  } catch (error) {
    discardDownloadBody(res)
    throw error
  }
  if (res.status === 401 && (opts.refreshOn401 || host)) {
    discardDownloadBody(res)
    assertRequestActive(requestOptions)
    const refreshed = await refreshOnce(requestSession ?? captureAuthSession())
    assertRequestActive(requestOptions)
    if (refreshed.status !== 'ok') throwRefreshFailure(refreshed)
    if (!sessionStillCurrent(refreshed.session)) throwRefreshFailure(refreshed)
    res = await fetch(apiUrl(path), { headers: authHeaders(), signal })
    try {
      assertRequestActive(requestOptions)
    } catch (error) {
      discardDownloadBody(res)
      throw error
    }
  }
  if (!res.ok) {
    discardDownloadBody(res)
    throw new ApiError(`下载失败（HTTP ${res.status}）`, -1, res.status)
  }
  return res
}

function parseDownloadFilename(disposition: string | null): string {
  if (!disposition) throw new ApiError('下载缺少文件名，未保存文件')
  const encoded = disposition.match(/(?:^|;)\s*filename\*\s*=\s*UTF-8''([^;]+)/i)?.[1]?.trim()
  if (encoded) {
    try {
      return decodeURIComponent(encoded.replace(/^"|"$/g, ''))
    } catch {
      throw new ApiError('下载文件名编码无效，未保存文件')
    }
  }
  const plain = disposition.match(/(?:^|;)\s*filename\s*=\s*(?:"([^"]*)"|([^;]*))/i)
  const filename = (plain?.[1] ?? plain?.[2])?.trim()
  if (!filename) throw new ApiError('下载缺少文件名，未保存文件')
  return filename
}

function isSafeDownloadFilename(filename: string): boolean {
  return filename.length <= 180 && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(filename)
}

function discardDownloadBody(response: Response): void {
  try {
    void response.body?.cancel?.().catch(() => undefined)
  } catch {
    // Discarding a rejected response must not replace the original error.
  }
}
