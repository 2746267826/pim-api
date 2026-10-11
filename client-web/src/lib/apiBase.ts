/*
 * API 地址配置（v0.3 计划的结构性新增）。
 * - 存储 localStorage pim.apiBase（协议://主机:端口，不含 /api/v1；空 = 同源）
 * - 所有请求/瓦片/下载 URL 必须经 apiUrl() 拼接，禁止散落拼接
 * - 壳（Capacitor/Tauri）无同源概念：未配置地址时强制进入 /setup
 */
import { detectHostChannel } from './host-channel'
import { STORAGE_KEYS, getJSON, getString, setJSON, setString } from './storage'

const HISTORY_LIMIT = 5

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

export function getApiBase(): string {
  return normalizeApiBaseInput(getString(STORAGE_KEYS.apiBase) ?? '')
}

export function getApiBaseHistory(): string[] {
  // 读取时也归一化：修复归一化上线前存入的带 /api/v1 后缀的历史条目，并丢弃坏值
  const raw = getJSON<string[]>(STORAGE_KEYS.apiBaseHistory, [])
  const cleaned: string[] = []
  for (const item of raw) {
    const v = normalizeApiBaseInput(item ?? '')
    if (!v) continue
    try {
      new URL(v)
    } catch {
      continue
    }
    if (!cleaned.includes(v)) cleaned.push(v)
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
  const value = normalizeApiBaseInput(raw)
  if (value) {
    try {
      // 仅接受带协议的绝对地址（同源场景请留空）
      new URL(value)
    } catch {
      throw new Error('地址需包含协议，例如 http://192.168.1.10:5858')
    }
  }
  setString(STORAGE_KEYS.apiBase, value)
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
