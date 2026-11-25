/*
 * API 地址配置（v0.3 计划的结构性新增）。
 * - 存储 localStorage pim.apiBase（协议://主机:端口，不含 /api/v1；空 = 同源）
 * - 所有请求/瓦片/下载 URL 必须经 apiUrl() 拼接，禁止散落拼接
 * - 壳（Capacitor/Tauri）无同源概念：未配置地址时强制进入 /setup
 */
import { detectHostChannel } from './host-channel'
import { markAuthSessionChanged } from './auth-session-revision'
import { STORAGE_KEYS, getJSON, getString, setJSON, setString } from './storage'

const HISTORY_LIMIT = 5

export type ApiBaseReasonCode =
  | 'invalid-url'
  | 'unsupported-scheme'
  | 'address-components'
  | 'remote-http-blocked'

export interface ApiBaseValidation {
  ok: boolean
  value: string
  reasonCode: ApiBaseReasonCode | null
  message: string
  isLoopback: boolean
}

const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '10.0.2.2'])

/** 本机回环（含 Android 模拟器 10.0.2.2）保留 HTTP 作为开发例外。 */
export function isLoopbackHost(hostname: string): boolean {
  const normalized = hostname.toLowerCase().replace(/^\[|\]$/g, '')
  return LOOPBACK_HOSTS.has(normalized)
}

/**
 * 归一化用户输入的 API 地址：base 恒为「源地址」（协议://主机:端口，不含 /api/v1）。
 * 用户常把完整 API URL（如 https://host:5858/api/v1）整个粘贴进来——
 * 不去掉后缀会拼出 /api/v1/api/v1/... 全部 404，故存储与测试拼接前统一剥离。
 */
export function normalizeApiBaseInput(raw: string): string {
  return raw
    .trim()
    .replace(/\/+$/, '')
    .replace(/\/api\/v1$/i, '')
    .replace(/\/api$/i, '')
}

/**
 * Q04：远程服务器只允许 HTTPS；回环 HTTP 仅用于本机开发。
 * 保存与测试连接都必须先过这一层，不能只靠浏览器/WebView 的事后拦截。
 */
export function validateApiBaseInput(raw: string): ApiBaseValidation {
  const value = normalizeApiBaseInput(raw)
  if (!value) {
    return { ok: true, value: '', reasonCode: null, message: '', isLoopback: false }
  }

  let parsed: URL
  try {
    parsed = new URL(value)
  } catch {
    return {
      ok: false,
      value,
      reasonCode: 'invalid-url',
      message: '地址需包含协议，例如 https://pim.example.com',
      isLoopback: false,
    }
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return {
      ok: false,
      value,
      reasonCode: 'unsupported-scheme',
      message: '服务器地址必须使用 HTTPS（本机回环开发除外）。',
      isLoopback: false,
    }
  }

  if (parsed.username || parsed.password) {
    return {
      ok: false,
      value,
      reasonCode: 'address-components',
      message: '服务器地址不能包含账号或密码。',
      isLoopback: false,
    }
  }

  if (parsed.search || parsed.hash) {
    return {
      ok: false,
      value,
      reasonCode: 'address-components',
      message: '服务器地址不能包含查询参数或片段。',
      isLoopback: false,
    }
  }

  const loopback = isLoopbackHost(parsed.hostname)
  if (parsed.protocol === 'http:' && !loopback) {
    return {
      ok: false,
      value,
      reasonCode: 'remote-http-blocked',
      message: '远程服务器必须使用 HTTPS。此地址未保存，也未发送连接请求。',
      isLoopback: false,
    }
  }

  return { ok: true, value, reasonCode: null, message: '', isLoopback: loopback }
}

export function getApiBase(): string {
  const validation = validateApiBaseInput(getString(STORAGE_KEYS.apiBase) ?? '')
  return validation.ok ? validation.value : ''
}

export function getApiBaseHistory(): string[] {
  // 读取时也归一化并重新过传输门禁：旧版本可能存过远程 HTTP 地址
  const raw = getJSON<string[]>(STORAGE_KEYS.apiBaseHistory, [])
  const cleaned: string[] = []
  for (const item of raw) {
    const validation = validateApiBaseInput(item ?? '')
    if (!validation.ok || !validation.value) continue
    if (!cleaned.includes(validation.value)) cleaned.push(validation.value)
  }
  return cleaned
}

/**
 * host 通道（规格 §6.1）：基址恒为**文档 origin**，忽略 localStorage 的 `pim.apiBase`。
 * 嵌入宿主页面的地址由 Windows 宿主决定，页面里的服务器地址既不可信也不该被使用。
 */
function hostChannel(): boolean {
  return detectHostChannel() === 'host'
}

export function setApiBase(raw: string): void {
  // host 通道不允许保存服务器地址（规格 §6.1）：不写 storage，也不广播变更
  if (hostChannel()) return
  const validation = validateApiBaseInput(raw)
  if (!validation.ok) {
    throw new Error(validation.message)
  }
  const value = validation.value
  const changed = value !== getApiBase()
  setString(STORAGE_KEYS.apiBase, value)
  if (changed) markAuthSessionChanged()
  if (value) {
    const rest = getApiBaseHistory().filter((x) => x !== value)
    setJSON(STORAGE_KEYS.apiBaseHistory, [value, ...rest].slice(0, HISTORY_LIMIT))
  }
  // 广播给侧边栏服务器指示等订阅者
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('pim:api-base-changed'))
  }
}

/** 拼接 API 相对路径（path 以 / 开头，如 /api/v1/...） */
export function apiUrl(path: string): string {
  // host 通道：同源相对路径 = 文档 origin（规格 §6.1），不拼 pim.apiBase
  if (hostChannel()) return path
  const base = getApiBase()
  if (!base) return path
  return path.startsWith('/') ? base + path : `${base}/${path}`
}

/** 用候选地址（而非当前生效地址）拼 URL，供"测试连接"使用；同样归一化误带的后缀 */
export function candidateUrl(base: string, path: string): string {
  const b = normalizeApiBaseInput(base)
  if (!b) return path
  return path.startsWith('/') ? b + path : `${b}/${path}`
}

/** 是否运行在原生壳内（Capacitor / Tauri） */
export function isNativeShell(): boolean {
  const w = globalThis as {
    Capacitor?: { isNativePlatform?: () => boolean }
    __TAURI_INTERNALS__?: unknown
    __TAURI__?: unknown
  }
  return Boolean(w.Capacitor?.isNativePlatform?.()) || Boolean(w.__TAURI_INTERNALS__ ?? w.__TAURI__)
}

/** 壳内且未配置 API 地址 → 全路由重定向 /setup（host 通道用文档 origin，不需要向导） */
export function needsServerSetup(): boolean {
  if (hostChannel()) return false
  return isNativeShell() && !getApiBase()
}
