import { useEffect, useState } from 'react'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'

/** 桌面侧边栏折叠状态（localStorage 持久化） */
export function useSidebarCollapsed() {
  const [collapsed, setCollapsed] = useState(() =>
    getJSON<boolean>(STORAGE_KEYS.sidebarCollapsed, false),
  )
  useEffect(() => {
    const sync = () => setCollapsed(getJSON<boolean>(STORAGE_KEYS.sidebarCollapsed, false))
    window.addEventListener('pim:sidebar-collapsed', sync)
    return () => window.removeEventListener('pim:sidebar-collapsed', sync)
  }, [])
  const toggle = () => {
    setJSON(STORAGE_KEYS.sidebarCollapsed, !collapsed)
    window.dispatchEvent(new CustomEvent('pim:sidebar-collapsed'))
  }
  return { collapsed, toggle }
}
