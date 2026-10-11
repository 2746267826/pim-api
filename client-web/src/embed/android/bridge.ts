/*
 * Android 原生桥（03 §6 / PW-07a reviewed-spec §8）：
 * 通道每次发送时现判，不在模块加载时缓存，也不把合成对象写进 `globalThis.pimAndroid`：
 * 1. `Capacitor.Plugins.PimBridge.handleMessage` 是函数 → Capacitor 旧信封 `{type,requestId,payload}`，
 *    回复仍走 `__pimBridgeReceive`；
 * 2. 否则 `pimAndroid.postMessage` 是函数 → 原生 v1 信封 `{version:1,id,type}`，回复走
 *    `pimAndroid.onmessage`（赋值必须发生在第一次 postMessage 之前）；
 * 3. 否则不发消息，`hasNativeBridge()` 为 false，页面回退 `apiFetch` 的 localStorage 路径。
 *
 * 令牌退役（§7）：本模块**不再导出**任何返回令牌的东西（旧 `requestNativeToken` 已删除），
 * 内嵌页的 3 条 GET 由原生 OkHttp 代发，令牌不进页面。
 */

export interface NativeCollectionState {
  continuousCollection?: boolean
  triggerReason?: string | null
  nextLocationAt?: string | null
  pendingUpload?: number
  batteryOptimized?: boolean
}

/** §8.4 交给原生状态条的页面数据状态。 */
export interface PageReport {
  hasServerData: boolean | null
  generatedAt: string | null
  error: string | null
}

const BRIDGE_TIMEOUTMS = 5_000

interface PimAndroidBridge {
  postMessage: (payload: string) => void
  onmessage?: ((event: { data: string }) => void) | null
}

interface BridgeMessage {
  type: string
  requestId?: string
  payload?: unknown
}

/** 原生 v1 成功回复没有 `type`；页面只按 `id` 配对，并要求 `version === 1` 且 `ok` 是布尔。 */
interface NativeV1Reply {
  version?: unknown
  id?: unknown
  ok?: unknown
  errorCode?: unknown
  nativeState?: unknown
}

type CapacitorHandleMessage = (options: { raw: string }) => unknown

type Channel =
  | { kind: 'capacitor'; handleMessage: CapacitorHandleMessage }
  | { kind: 'native'; bridge: PimAndroidBridge }

let seq = 0
const pendingLegacy = new Map<string, (value: unknown) => void>()
const pendingNative = new Map<string, (value: NativeV1Reply) => void>()

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/** 每次现判：Capacitor 优先，其次原生 `pimAndroid`，都没有则 null。 */
function currentChannel(): Channel | null {
  const cap = (globalThis as { Capacitor?: { Plugins?: Record<string, unknown> } | undefined }).Capacitor
  const plugins = cap?.Plugins as Record<string, { handleMessage?: unknown } | undefined> | undefined
  const handleMessage = plugins?.PimBridge?.handleMessage
  if (typeof handleMessage === 'function') {
    return { kind: 'capacitor', handleMessage: handleMessage as CapacitorHandleMessage }
  }
  const direct = (globalThis as { pimAndroid?: PimAndroidBridge }).pimAndroid
  if (direct && typeof direct.postMessage === 'function') {
    return { kind: 'native', bridge: direct }
  }
  return null
}

/** 是否运行于 Android 壳内（桥可用） */
export function hasNativeBridge(): boolean {
  return currentChannel() != null
}

/* ── Capacitor 旧通道 ─────────────────────────────────────── */

function installLegacyReceiver(): void {
  ;(globalThis as { __pimBridgeReceive?: (raw: string) => void }).__pimBridgeReceive = (raw: string) => {
    try {
      const msg = JSON.parse(raw) as BridgeMessage
      if (msg.requestId) {
        const resolve = pendingLegacy.get(msg.requestId)
        pendingLegacy.delete(msg.requestId)
        resolve?.(msg.payload)
      }
    } catch {
      // 忽略无法解析的消息
    }
  }
}

function callLegacy(type: string, payload: unknown, timeoutMs: number): Promise<unknown | null> {
  const channel = currentChannel()
  if (!channel || channel.kind !== 'capacitor') return Promise.resolve(null)
  installLegacyReceiver()
  const requestId = `r${++seq}`
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pendingLegacy.delete(requestId)
      resolve(null)
    }, timeoutMs)
    pendingLegacy.set(requestId, (value) => {
      clearTimeout(timer)
      resolve(value)
    })
    void channel.handleMessage({ raw: JSON.stringify({ type, requestId, payload }) })
  })
}

/* ── 原生 v1 通道 ─────────────────────────────────────────── */

/** 只按 `id` 配对，并要求 `version === 1` 且 `ok` 是布尔；对不上的消息丢掉。 */
function handleNativeMessage(raw: string): void {
  let reply: NativeV1Reply
  try {
    reply = JSON.parse(raw) as NativeV1Reply
  } catch {
    return
  }
  if (!isRecord(reply)) return
  if (reply.version !== 1) return
  if (typeof reply.ok !== 'boolean') return
  if (typeof reply.id !== 'string') return
  const resolve = pendingNative.get(reply.id)
  if (!resolve) return
  pendingNative.delete(reply.id)
  resolve(reply)
}

function nativeEnvelopePost(channel: Extract<Channel, { kind: 'native' }>, raw: string): void {
  // §8.2：`onmessage` 赋值必须发生在第一次 `postMessage` 之前。
  channel.bridge.onmessage = (event) => {
    if (typeof event?.data === 'string') handleNativeMessage(event.data)
  }
  channel.bridge.postMessage(raw)
}

function callNative(type: string, timeoutMs: number): Promise<NativeV1Reply | null> {
  const channel = currentChannel()
  if (!channel || channel.kind !== 'native') return Promise.resolve(null)
  const id = `r${++seq}`
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pendingNative.delete(id)
      resolve(null)
    }, timeoutMs)
    pendingNative.set(id, (value) => {
      clearTimeout(timer)
      resolve(value)
    })
    nativeEnvelopePost(channel, JSON.stringify({ version: 1, id, type }))
  })
}

/* ── 状态映射（§8.3） ─────────────────────────────────────── */

/** 原生 v1 `nativeState`：`collectionMode` / `pending` / `triggerReason` / `nextLocationAt` / `batteryOptimized`。 */
function mapNativeState(raw: Record<string, unknown>): NativeCollectionState {
  return {
    continuousCollection: typeof raw.collectionMode === 'boolean' ? raw.collectionMode : undefined,
    triggerReason: typeof raw.triggerReason === 'string' ? raw.triggerReason : null,
    nextLocationAt: typeof raw.nextLocationAt === 'string' ? raw.nextLocationAt : null,
    pendingUpload: typeof raw.pending === 'number' ? raw.pending : undefined,
    batteryOptimized: raw.batteryOptimized === true ? true : undefined,
  }
}

/** Capacitor 壳的旧 payload（同名即 NativeCollectionState 字段）。 */
function mapLegacyPayload(raw: Record<string, unknown>): NativeCollectionState {
  return {
    continuousCollection: typeof raw.continuousCollection === 'boolean' ? raw.continuousCollection : undefined,
    triggerReason: typeof raw.triggerReason === 'string' ? raw.triggerReason : null,
    nextLocationAt: typeof raw.nextLocationAt === 'string' ? raw.nextLocationAt : null,
    pendingUpload: typeof raw.pendingUpload === 'number' ? raw.pendingUpload : undefined,
    batteryOptimized: raw.batteryOptimized === true ? true : undefined,
  }
}

/**
 * 原生采集状态（30 秒轮询由调用方驱动）。
 * `ok: false` 不映射成采集状态，返回 null → 状态卡保持「原生未返回采集状态」。
 */
export async function requestNativeState(timeoutMs = BRIDGE_TIMEOUTMS): Promise<NativeCollectionState | null> {
  const channel = currentChannel()
  if (!channel) return null
  if (channel.kind === 'capacitor') {
    const payload = await callLegacy('native.state.request', undefined, timeoutMs)
    return isRecord(payload) ? mapLegacyPayload(payload) : null
  }
  const reply = await callNative('native.state.request', timeoutMs)
  if (!reply || reply.ok !== true) return null
  const state = reply.nativeState
  return mapNativeState(isRecord(state) ? state : {})
}

/** 向壳回报页面数据状态（成功/失败/空；壳侧 30 秒重试）。 */
export function reportPageState(report: PageReport): void {
  const channel = currentChannel()
  if (!channel) return
  const id = `r${++seq}`
  if (channel.kind === 'capacitor') {
    installLegacyReceiver()
    void channel.handleMessage({
      raw: JSON.stringify({
        type: 'page.report',
        requestId: id,
        payload: {
          hasServerData: report.hasServerData,
          generatedAt: report.generatedAt,
          error: report.error,
        },
      }),
    })
    return
  }
  nativeEnvelopePost(
    channel,
    JSON.stringify({
      version: 1,
      id,
      type: 'page.report',
      report: {
        hasServerData: report.hasServerData,
        generatedAt: report.generatedAt,
        error: report.error,
      },
    }),
  )
}

/** 内嵌页 URL 参数（?embed=1 等） */
export function isEmbedded(): boolean {
  if (typeof window === 'undefined') return false
  return window.location.pathname.startsWith('/embed/') || new URLSearchParams(window.location.search).get('embed') === '1'
}
