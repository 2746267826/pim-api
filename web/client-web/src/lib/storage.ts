/*
 * localStorage 安全封装：键名统一 pim.* 前缀（见 CONVENTIONS.md §6）。
 * 无 localStorage 的环境（node 测试 / 隐私模式）自动回退内存 Map。
 */

interface LsLike {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
  readonly length?: number
  key?(index: number): string | null
}

const memory = new Map<string, string>()

const memoryBackend: LsLike = {
  getItem: (key) => memory.get(key) ?? null,
  setItem: (key, value) => void memory.set(key, value),
  removeItem: (key) => void memory.delete(key),
  get length() {
    return memory.size
  },
  key: (index) => [...memory.keys()][index] ?? null,
}

function ls(): LsLike {
  try {
    const s = (globalThis as { localStorage?: LsLike }).localStorage
    if (!s) throw new Error('no localStorage')
    const probe = '__pim_probe__'
    s.setItem(probe, '1')
    s.removeItem(probe)
    return s
  } catch {
    return memoryBackend
  }
}

/** 全站 localStorage 键名登记表（新增键必须在此登记） */
export const STORAGE_KEYS = {
  accessToken: 'pim.accessToken',
  refreshToken: 'pim.refreshToken',
  apiBase: 'pim.apiBase',
  apiBaseHistory: 'pim.apiBaseHistory',
  calendarLayerVisibility: 'pim.calendarLayers',
  asrJobReceipt: 'pim.asrJobReceipt',
  fileBrowserMemory: 'pim.fileBrowser',
  /** 文件页当前选中的网盘账号 id（多账号并存时记住选择） */
  fileAccountId: 'pim.fileAccountId',
  transferHistory: 'pim.transferHistory',
  labelingCustomCategories: 'pim.labelingCategories',
  quickNoteDialogPosition: 'pim.quickNoteDialog',
  exhibition: 'pim.exhibition',
  sidebarCollapsed: 'pim.sidebarCollapsed',
  calendarSkin: 'pim.calendarSkin',
  /** 手写画笔上次选择的颜色与粗细（本机偏好） */
  inkBrushPreference: 'pim.inkBrushPreference',
} as const

export function getString(key: string): string | null {
  return ls().getItem(key)
}

export function setString(key: string, value: string): void {
  ls().setItem(key, value)
}

export function remove(key: string): void {
  ls().removeItem(key)
}

export function getJSON<T>(key: string, fallback: T): T {
  const raw = ls().getItem(key)
  if (raw == null) return fallback
  try {
    return JSON.parse(raw) as T
  } catch {
    return fallback
  }
}

export function setJSON(key: string, value: unknown): void {
  ls().setItem(key, JSON.stringify(value))
}

/** 枚举以指定前缀开头的键；用于发现本机草稿，不读取或修改未列入前缀的数据。 */
export function storageKeys(prefix = ''): string[] {
  const backend = ls()
  const keys: string[] = []
  const length = backend.length ?? 0
  if (typeof backend.key !== 'function') return keys
  for (let index = 0; index < length; index += 1) {
    const key = backend.key(index)
    if (key != null && key.startsWith(prefix) && !keys.includes(key)) keys.push(key)
  }
  return keys
}
