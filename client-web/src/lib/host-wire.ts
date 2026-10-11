/*
 * 宿主通道 wire 编解码（规格 §2）。
 *
 * 唯一规格：`remediation/host-session/reviewed-spec.md`
 * （sha256 8964935021f69ff2e44c6d8932fa7a5f00645ddc5f249661b88f93694312ccf8）。
 *
 * 这里只做**形状**判定，不做业务：
 * - `v` 必须是整数 1；UTF-8 JSON 对象最大 4096 **字节**（`utf8ByteLength` 单一实现，
 *   不是 `String.length`）；
 * - 每种 type 只允许规格里列出的字段：多一个字段就整帧丢弃（**字段白名单**，不是字段名正则黑名单）；
 * - 数组、字符串、非 JSON 一律丢弃。
 *
 * 传输形态（跨端接线，必须两端都按这个来）：
 * - 宿主 → 页面：宿主用 `PostWebMessageAsJson(json)`，页面的 `message` 事件里
 *   `event.data` 是**对象**；测试夹具发的是 JSON **字符串**。所以 `parseHostFrame`
 *   两种都接受（对象 / JSON 字符串）。
 * - 页面 → 宿主：页面 `chrome.webview.postMessage(JSON.stringify(frame))` 发的是
 *   **字符串**。Windows 侧必须用 `e.TryGetWebMessageAsString()` 取原文；
 *   若改用 `e.WebMessageAsJson`，拿到的会是带外层引号的字符串（双重编码）。
 */
import { HOST_PRODUCT } from './host-channel'

export const HOST_PROTOCOL_VERSION = 1
/** 一帧 UTF-8 JSON 的最大字节数 */
export const MAX_FRAME_BYTES = 4096

const REQUEST_ID_RE = /^[A-Za-z0-9_-]{1,64}$/
const CLIENT_NONCE_RE = /^[A-Za-z0-9_-]{8,64}$/
const HANDSHAKE_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/

export const REFRESH_REASONS = ['invalid', 'transient', 'unauthenticated'] as const
export const ENDED_REASONS = ['logout', 'expired'] as const
export type RefreshReason = (typeof REFRESH_REASONS)[number]
export type EndedReason = (typeof ENDED_REASONS)[number]

export interface PageReadyFrame {
  v: 1
  type: 'page.ready'
  requestId: string
  clientNonce: string
}
export interface PageRefreshFrame {
  v: 1
  type: 'page.refresh'
  requestId: string
}
export interface PageLogoutFrame {
  v: 1
  type: 'page.logout'
  requestId: string
}
export type PageFrame = PageReadyFrame | PageRefreshFrame | PageLogoutFrame

export interface HostStateFrame {
  v: 1
  type: 'host.state'
  requestId: string
  clientNonce: string
  handshakeId: string
  product: string
  apiOrigin: string
  authenticated: boolean
}
export interface HostRefreshResultFrame {
  v: 1
  type: 'host.refresh.result'
  requestId: string
  ok: boolean
  reason?: RefreshReason
}
export interface HostSessionEndedFrame {
  v: 1
  type: 'host.session.ended'
  handshakeId: string
  reason: EndedReason
  requestId?: string
}
export type HostFrame = HostStateFrame | HostRefreshResultFrame | HostSessionEndedFrame

/** 每种 type 允许出现的字段（白名单） */
const ALLOWED_FIELDS: Record<string, readonly string[]> = {
  'host.state': [
    'v',
    'type',
    'requestId',
    'clientNonce',
    'handshakeId',
    'product',
    'apiOrigin',
    'authenticated',
  ],
  'host.refresh.result': ['v', 'type', 'requestId', 'ok', 'reason'],
  'host.session.ended': ['v', 'type', 'handshakeId', 'reason', 'requestId'],
}

/**
 * 一帧文本的 UTF-8 字节数 —— 4096 上限的**唯一**判据实现。
 *
 * 刻意**不**依赖 `TextEncoder`，也不留环境相关的退化分支：同一件事有两套实现时，
 * 「字节数被换成 String.length」这类故障只会命中其中一条，另一条仍能让用例变绿
 * （那正是 R5 独立 review 在 `mutation-e2b` 里观察到的假绿）。单一实现可以被
 * 故障对照直接打穿，而这个纯函数就是 `parseHostFrame` 实际调用的那一个。
 *
 * 逐码点计算：ASCII 1、U+0080..U+07FF 2、其余 BMP 3、增补平面 4。
 * 孤立代理项（U+D800..U+DFFF）与 `TextEncoder` 的替换字符同计 3 字节。
 */
export function utf8ByteLength(text: string): number {
  let bytes = 0
  for (const ch of text) {
    const cp = ch.codePointAt(0) ?? 0
    bytes += cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4
  }
  return bytes
}

/**
 * 解析一帧宿主 → 页面的消息；不合法返回 null（调用方直接丢弃，不回错误帧）。
 * `raw` 是 `MessageEvent.data`：真实 WebView2 是对象，测试夹具是 JSON 字符串。
 */
export function parseHostFrame(raw: unknown): HostFrame | null {
  let value: unknown = raw
  if (typeof raw === 'string') {
    if (utf8ByteLength(raw) > MAX_FRAME_BYTES) return null
    try {
      value = JSON.parse(raw)
    } catch {
      return null
    }
  } else if (raw !== null && typeof raw === 'object') {
    // 对象形态拿不到原始字节：用等价的 JSON 文本长度兜底（规格按 UTF-8 字节）
    try {
      if (utf8ByteLength(JSON.stringify(raw)) > MAX_FRAME_BYTES) return null
    } catch {
      return null
    }
  } else {
    return null
  }

  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null
  const frame = value as Record<string, unknown>
  if (frame['v'] !== HOST_PROTOCOL_VERSION) return null
  const type = frame['type']
  if (typeof type !== 'string') return null
  const allowed = ALLOWED_FIELDS[type]
  if (!allowed) return null
  // 字段白名单：多一个字段就整帧丢弃
  if (Object.keys(frame).some((k) => !allowed.includes(k))) return null

  if (type === 'host.state') {
    if (!isRequestId(frame['requestId']) || !isClientNonce(frame['clientNonce'])) return null
    if (typeof frame['handshakeId'] !== 'string' || !HANDSHAKE_ID_RE.test(frame['handshakeId']))
      return null
    if (frame['product'] !== HOST_PRODUCT) return null
    if (typeof frame['apiOrigin'] !== 'string' || !isOriginValue(frame['apiOrigin'])) return null
    if (typeof frame['authenticated'] !== 'boolean') return null
    return {
      v: 1,
      type,
      requestId: frame['requestId'],
      clientNonce: frame['clientNonce'],
      handshakeId: frame['handshakeId'],
      product: frame['product'],
      apiOrigin: frame['apiOrigin'],
      authenticated: frame['authenticated'],
    }
  }

  if (type === 'host.refresh.result') {
    if (!isRequestId(frame['requestId'])) return null
    if (typeof frame['ok'] !== 'boolean') return null
    if (frame['ok']) {
      // `reason` 只允许出现在 ok:false
      if ('reason' in frame) return null
      return { v: 1, type, requestId: frame['requestId'], ok: true }
    }
    const reason = frame['reason']
    /*
     * §2 把 `reason` 标为**可选**（必填只有 v/type/requestId/ok），正文也写明
     * 「可选字段可以不出现」。合法省略不是非法帧：这里按「没有 reason」返回，
     * 由调用侧既有的 `?? 'transient'` 兜底（规格 §6.2：按 transient 失败本请求）。
     * 不新增 wire 枚举值，也不把 reason 变成必填。
     */
    if (reason === undefined) {
      return { v: 1, type, requestId: frame['requestId'], ok: false }
    }
    if (typeof reason !== 'string' || !REFRESH_REASONS.includes(reason as RefreshReason))
      return null
    return { v: 1, type, requestId: frame['requestId'], ok: false, reason: reason as RefreshReason }
  }

  // host.session.ended
  if (typeof frame['handshakeId'] !== 'string' || !HANDSHAKE_ID_RE.test(frame['handshakeId']))
    return null
  const reason = frame['reason']
  if (typeof reason !== 'string' || !ENDED_REASONS.includes(reason as EndedReason)) return null
  const requestId = frame['requestId']
  if (requestId !== undefined && !isRequestId(requestId)) return null
  return {
    v: 1,
    type: 'host.session.ended',
    handshakeId: frame['handshakeId'],
    reason: reason as EndedReason,
    ...(requestId === undefined ? {} : { requestId }),
  }
}

export function isRequestId(value: unknown): value is string {
  return typeof value === 'string' && REQUEST_ID_RE.test(value)
}

export function isClientNonce(value: unknown): value is string {
  return typeof value === 'string' && CLIENT_NONCE_RE.test(value)
}

/**
 * 规格 §3 的 origin 解析：只接受 http(s) 且无 userinfo 的绝对 URL。
 * 返回 `scheme://idnhost[:port]`（缺省端口被 URL 规范化掉，IPv6 主机带方括号）。
 * `Uri.TryCreate` 等价的失败 → null。
 */
export function originOf(raw: string): string | null {
  let url: URL
  try {
    url = new URL(raw)
  } catch {
    return null
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null
  if (url.username !== '' || url.password !== '') return null
  return `${url.protocol}//${url.host}`
}

/** 同一套 origin 比较（scheme + IdnHost + port），不用子字符串/前缀 */
export function sameOrigin(a: string, b: string): boolean {
  const oa = originOf(a)
  const ob = originOf(b)
  return oa !== null && ob !== null && oa === ob
}

/**
 * §2 的**字段值形态**判据：`host.state.apiOrigin` 的值是文档 origin（`scheme://host`）。
 * 带 path / query / hash 的值不是该字段的值（裁决见 `remediation/coordination-round5.md`：
 * 值定义优先；「必须拒绝」清单不是值定义的穷尽授权）。userinfo 也按 §3.2 拒绝。
 *
 * 与 `originOf()` 的分工（不要把两件事混为一谈）：
 * - `originOf()` 是**来源 URL 解析**，允许带 path（只取 scheme+IdnHost+port），不改；
 * - 本函数判**字段值**，要求 URL 里除 origin 之外没有别的东西。
 * `new URL('http://h')` 的 pathname 本来就是 `'/'`，所以不写路径与写一个根斜杠等价；
 * 按 §3.4「缺省端口判等」，这里也**不**额外要求去掉 `:80`/`:443`（不做超出的规范化）。
 */
export function isOriginValue(raw: string): boolean {
  let url: URL
  try {
    url = new URL(raw)
  } catch {
    return false
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false
  if (url.username !== '' || url.password !== '') return false
  if (url.pathname !== '/') return false
  if (url.search !== '' || url.hash !== '') return false
  return true
}

/** 当前文档的 origin（页面用它校验 host.state.apiOrigin） */
export function documentOrigin(): string | null {
  const loc = (globalThis as { location?: { origin?: unknown } }).location
  const origin = loc?.origin
  return typeof origin === 'string' && origin !== '' ? originOf(origin) : null
}
