/*
 * localStorage 安全封装：键名统一 pim.* 前缀（见 CONVENTIONS.md §6）。
 * 无 localStorage 的环境（node 测试 / 隐私模式）自动回退内存 Map。
 */

interface LsLike {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
  removeItem(key: string): void
}

const memory = new Map<string, string>()

function ls(): LsLike {
  try {
    const s = (globalThis as { localStorage?: LsLike }).localStorage
    if (!s) throw new Error('no localStorage')
    const probe = '__pim_probe__'
    s.setItem(probe, '1')
    s.removeItem(probe)
    return s
  } catch {
    return {
      getItem: (k) => memory.get(k) ?? null,
      setItem: (k, v) => void memory.set(k, v),
      removeItem: (k) => void memory.delete(k),
    }
  }
}

/** 全站 localStorage 键名登记表（新增键必须在此登记） */
export const STORAGE_KEYS = {
  accessToken: 'pim.accessToken',
  refreshToken: 'pim.refreshToken',
  apiBase: 'pim.apiBase',
  apiBaseHistory: 'pim.apiBaseHistory',
  calendarLayerVisibility: 'pim.calendarLayers',
  fileBrowserMemory: 'pim.fileBrowser',
  /** 文件页当前选中的网盘账号 id（多账号并存时记住选择） */
  fileAccountId: 'pim.fileAccountId',
  transferHistory: 'pim.transferHistory',
  labelingCustomCategories: 'pim.labelingCategories',
  quickNoteDialogPosition: 'pim.quickNoteDialog',
  exhibition: 'pim.exhibition',
  sidebarCollapsed: 'pim.sidebarCollapsed',
  calendarSkin: 'pim.calendarSkin',
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
