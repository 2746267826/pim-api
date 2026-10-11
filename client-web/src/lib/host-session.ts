/*
 * 宿主会话客户端（页面侧）：握手、单飞刷新、退出与导航代次（规格 §1/§2/§6）。
 *
 * 唯一规格：`remediation/host-session/reviewed-spec.md`
 * （sha256 8964935021f69ff2e44c6d8932fa7a5f00645ddc5f249661b88f93694312ccf8）。
 *
 * 页面侧义务（本文件的边界）：
 * - 挂监听器**之后**发一条 `page.ready`；500ms 内没有合法的 `host.state` → `unavailable`
 *   （仍是 host 通道，可重试；重试只再发一条新的 `page.ready`）；
 * - `host.state` 必须同时对上 `requestId` 与 `clientNonce`，`product`/`apiOrigin` 也要对；
 * - 401 的单飞刷新：一条 `page.refresh`，等匹配 `requestId` 的 `host.refresh.result`，最长 8 秒；
 * - `page.logout`：等匹配当前 `handshakeId` 的 `host.session.ended`，最长 2 秒；
 * - 只有匹配当前 `handshakeId` 的 `host.session.ended` 才结束会话（导航代次级失效）。
 *
 * 令牌不在本文件里出现：页面既不读也不写 token storage，出站请求也不带 `Authorization`
 * （Bearer 由 Windows 宿主的请求头过滤器加）。标记与 nonce 都不是身份认证——同源脚本
 * 可以改这个全局；本文件不声称能阻止同源脚本控制 UI。
 */
import { detectHostChannel } from './host-channel'
import {
  HOST_PROTOCOL_VERSION,
  documentOrigin,
  originOf,
  parseHostFrame,
  type EndedReason,
  type HostFrame,
  type PageFrame,
  type RefreshReason,
} from './host-wire'

/** 宿主桥面（页面可见的 `chrome.webview` 子集） */
export interface WebviewBridge {
  postMessage(message: string): void
  addEventListener(type: 'message', handler: (ev: MessageEvent) => void): void
  removeEventListener(type: 'message', handler: (ev: MessageEvent) => void): void
}

/** 页面侧通道状态（规格 §1 的四行表） */
export type HostStatus = 'pending' | 'ready' | 'unavailable'

/** 合法 `host.state` 建立的会话代次 */
export interface HostHandshake {
  handshakeId: string
  apiOrigin: string
  authenticated: boolean
}

/** 一次 `page.refresh` 的结果（页面侧只关心这四种） */
export type RefreshOutcome = 'ok' | 'invalid' | 'transient' | 'unauthenticated'

/** 500ms：没有合法 host.state → unavailable（规格 §1） */
export const HANDSHAKE_TIMEOUT_MS = 500
/** 8s：没有匹配的 host.refresh.result → 这一次请求按 transient 失败（规格 §6.2） */
export const REFRESH_TIMEOUT_MS = 8000
/** 2s：没有匹配的 host.session.ended → 退出超时，用户与会话都保持（规格 §6.2） */
export const LOGOUT_TIMEOUT_MS = 2000

const ID_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-'

function randomId(length: number): string {
  const cryptoObj = (globalThis as { crypto?: Crypto }).crypto
  const bytes = new Uint8Array(length)
  if (cryptoObj && typeof cryptoObj.getRandomValues === 'function') {
    cryptoObj.getRandomValues(bytes)
  } else {
    for (let i = 0; i < length; i++) bytes[i] = Math.floor(Math.random() * 256)
  }
  let out = ''
  for (let i = 0; i < length; i++) out += ID_ALPHABET[bytes[i]! % ID_ALPHABET.length]
  return out
}

/** `requestId`：1..64 个 [A-Za-z0-9_-] */
export function newRequestId(): string {
  return `req-${randomId(20)}`
}

/** `clientNonce`：8..64 个同一字符集。它不是密钥，只用来丢掉过期回复。 */
export function newClientNonce(): string {
  return `nonce-${randomId(20)}`
}

type StateListener = (session: HostSession) => void
type EndedListener = (reason: EndedReason) => void

interface RefreshWaiter {
  resolve: (outcome: RefreshOutcome) => void
  timer: ReturnType<typeof setTimeout>
}

/**
 * 一条文档生命周期内的宿主会话。页面侧单例由 `getHostSession()` 管理。
 */
export class HostSession {
  status: HostStatus = 'pending'
  handshake: HostHandshake | null = null

  private readonly bridge: WebviewBridge
  private listenerInstalled = false
  private handshakeTimer: ReturnType<typeof setTimeout> | null = null
  /** 尚未被合法 host.state 回应的 page.ready */
  private pendingReady: { requestId: string; clientNonce: string } | null = null
  private readonly refreshWaiters = new Map<string, RefreshWaiter>()
  private readonly logoutWaiters = new Map<string, () => void>()
  private refreshInFlight: Promise<RefreshOutcome> | null = null
  private readonly stateListeners = new Set<StateListener>()
  private readonly endedListeners = new Set<EndedListener>()

  constructor(bridge: WebviewBridge) {
    this.bridge = bridge
    /*
     * 监听器在构造时就装上：`apiFetch` / `fetchBlob` 也走同一条通道，它们自己
     * 不发 page.ready，但必须已经能收到 `host.refresh.result`（规格 §6.2）。
     * 握手仍然是「监听器装好之后」才发（`start()`）。
     */
    this.installListener()
  }

  /* ── 订阅 ───────────────────────────────────────────────── */

  /** 状态/代次变化时回调（订阅时不会立刻回调） */
  subscribe(listener: StateListener): () => void {
    this.stateListeners.add(listener)
    return () => this.stateListeners.delete(listener)
  }

  /** 收到匹配当前 handshakeId 的 `host.session.ended` 时回调 */
  onEnded(listener: EndedListener): () => void {
    this.endedListeners.add(listener)
    return () => this.endedListeners.delete(listener)
  }

  private notifyState(): void {
    for (const l of [...this.stateListeners]) l(this)
  }

  /* ── 握手 ───────────────────────────────────────────────── */

  /**
   * 装监听器并发一条新的 `page.ready`，同时启动 500ms 超时。
   * 已经在 pending（同一次握手还没结果）时不重复发送——React StrictMode 会双调用
   * effect，重试路径则会先把状态推进到 unavailable，因此不会被这里挡住。
   */
  start(): void {
    if (this.status === 'pending' && this.pendingReady) return
    this.installListener()
    this.clearHandshakeTimer()
    // 新的 page.ready = 新的导航代次：旧 handshakeId 立即作废
    this.handshake = null
    this.status = 'pending'
    this.pendingReady = { requestId: newRequestId(), clientNonce: newClientNonce() }
    this.post({
      v: HOST_PROTOCOL_VERSION,
      type: 'page.ready',
      requestId: this.pendingReady.requestId,
      clientNonce: this.pendingReady.clientNonce,
    })
    this.handshakeTimer = setTimeout(() => {
      this.handshakeTimer = null
      if (this.status !== 'pending') return
      this.status = 'unavailable'
      this.notifyState()
    }, HANDSHAKE_TIMEOUT_MS)
    this.notifyState()
  }

  private clearHandshakeTimer(): void {
    if (this.handshakeTimer !== null) {
      clearTimeout(this.handshakeTimer)
      this.handshakeTimer = null
    }
  }

  private installListener(): void {
    if (this.listenerInstalled) return
    this.listenerInstalled = true
    this.bridge.addEventListener('message', this.onMessage)
  }

  private readonly onMessage = (ev: MessageEvent): void => {
    const frame = parseHostFrame(ev.data)
    if (frame) this.handleFrame(frame)
  }

  private handleFrame(frame: HostFrame): void {
    if (frame.type === 'host.state') {
      // 必须同时对上这一次 page.ready 的 requestId 与 clientNonce
      if (!this.pendingReady) return
      if (frame.requestId !== this.pendingReady.requestId) return
      if (frame.clientNonce !== this.pendingReady.clientNonce) return
      const origin = documentOrigin()
      // apiOrigin 必须与文档 origin 用**同一套解析**得到相同的 scheme+IdnHost+port
      if (origin === null || origin !== originOf(frame.apiOrigin)) return
      this.pendingReady = null
      this.clearHandshakeTimer()
      this.handshake = {
        handshakeId: frame.handshakeId,
        apiOrigin: frame.apiOrigin,
        authenticated: frame.authenticated,
      }
      this.status = 'ready'
      this.notifyState()
      return
    }

    if (frame.type === 'host.refresh.result') {
      const waiter = this.refreshWaiters.get(frame.requestId)
      if (!waiter) return
      this.refreshWaiters.delete(frame.requestId)
      clearTimeout(waiter.timer)
      waiter.resolve(frame.ok ? 'ok' : ((frame.reason ?? 'transient') as RefreshReason))
      return
    }

    // host.session.ended：只有匹配当前 handshakeId 才作用于本文档（导航代次）
    if (this.handshake === null || frame.handshakeId !== this.handshake.handshakeId) return
    this.handshake = { ...this.handshake, authenticated: false }
    this.notifyState()
    for (const [requestId, resolve] of [...this.logoutWaiters]) {
      this.logoutWaiters.delete(requestId)
      resolve()
    }
    for (const l of [...this.endedListeners]) l(frame.reason)
  }

  /* ── 刷新（单飞） ────────────────────────────────────────── */

  /**
   * 发一条 `page.refresh` 并等匹配结果，最长 8 秒。
   * 并发调用共用同一条请求与同一次结果（页面单飞）。
   */
  refresh(): Promise<RefreshOutcome> {
    if (this.refreshInFlight) return this.refreshInFlight
    const round = this.requestRefresh().finally(() => {
      if (this.refreshInFlight === round) this.refreshInFlight = null
    })
    this.refreshInFlight = round
    return round
  }

  private requestRefresh(): Promise<RefreshOutcome> {
    const requestId = newRequestId()
    return new Promise<RefreshOutcome>((resolve) => {
      const timer = setTimeout(() => {
        this.refreshWaiters.delete(requestId)
        // 8 秒没有匹配回复：页面按 transient 处理这一次请求（不退出会话）
        resolve('transient')
      }, REFRESH_TIMEOUT_MS)
      this.refreshWaiters.set(requestId, { resolve, timer })
      if (!this.post({ v: HOST_PROTOCOL_VERSION, type: 'page.refresh', requestId })) {
        this.refreshWaiters.delete(requestId)
        clearTimeout(timer)
        resolve('transient')
      }
    })
  }

  /* ── 退出 ───────────────────────────────────────────────── */

  /**
   * 发一条 `page.logout`，等匹配当前 `handshakeId` 的 `host.session.ended`，最长 2 秒。
   * 返回 false = 超时（用户与会话都保持，页面显示可重试错误）。
   */
  logout(): Promise<boolean> {
    const requestId = newRequestId()
    return new Promise<boolean>((resolve) => {
      const timer = setTimeout(() => {
        this.logoutWaiters.delete(requestId)
        resolve(false)
      }, LOGOUT_TIMEOUT_MS)
      this.logoutWaiters.set(requestId, () => {
        clearTimeout(timer)
        resolve(true)
      })
      if (!this.post({ v: HOST_PROTOCOL_VERSION, type: 'page.logout', requestId })) {
        this.logoutWaiters.delete(requestId)
        clearTimeout(timer)
        resolve(false)
      }
    })
  }

  /* ── 发送 ───────────────────────────────────────────────── */

  private post(frame: PageFrame): boolean {
    try {
      // 页面 → 宿主发**字符串**：Windows 侧必须用 TryGetWebMessageAsString()
      this.bridge.postMessage(JSON.stringify(frame))
      return true
    } catch {
      return false
    }
  }
}

/* 单例：每条文档一个会话；桥对象换了（新文档/新夹具）就重建。 */

let current: HostSession | null = null
let currentBridge: WebviewBridge | null = null

/** 页面可见的 `chrome.webview` 桥面；没有则 null（有标记也必须能起会话才算 host 可用） */
export function getWebviewBridge(): WebviewBridge | null {
  const w = globalThis as { chrome?: { webview?: Partial<WebviewBridge> } }
  const wv = w.chrome?.webview
  if (!wv || typeof wv.postMessage !== 'function' || typeof wv.addEventListener !== 'function') {
    return null
  }
  return wv as WebviewBridge
}

/**
 * 取当前文档的宿主会话；不是 host 通道或没有桥面时返回 null。
 * 本函数**不**主动握手——握手由 `AuthProvider` 在挂载时装监听器后调用 `start()`。
 * `apiFetch`/`fetchBlob` 也走这里取同一条通道（它们自己不装监听器）。
 */
export function getHostSession(): HostSession | null {
  if (detectHostChannel() !== 'host') return null
  const bridge = getWebviewBridge()
  if (!bridge) return null
  if (current === null || currentBridge !== bridge) {
    current = new HostSession(bridge)
    currentBridge = bridge
  }
  return current
}

/** 测试辅助：丢掉单例（桥对象换了本来也会重建） */
export function resetHostSession(): void {
  current = null
  currentBridge = null
}
