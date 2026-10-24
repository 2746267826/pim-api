import {
  Bell,
  Calendar,
  CheckSquare,
  Database,
  FileText,
  FolderClosed,
  Globe,
  LayoutDashboard,
  MapPinned,
  Monitor,
  Repeat,
  Settings,
  ShieldCheck,
  Smartphone,
  Sunrise,
  Zap,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

export interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  /** 右侧 1 字符徽标位（P1 起接入计数） */
  badgeChar?: string
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

/** 一级导航 16 项，按 v0.2 页面设计分 5 组 */
export const NAV_GROUPS: NavGroup[] = [
  {
    label: '工作',
    items: [
      { to: '/today', label: '今日', icon: Sunrise },
      { to: '/calendar', label: '日历', icon: Calendar },
      { to: '/workbench', label: '工作台', icon: LayoutDashboard },
      { to: '/tasks', label: '任务', icon: CheckSquare },
      { to: '/confirmations', label: '确认', icon: ShieldCheck },
    ],
  },
  {
    label: '洞察',
    items: [
      { to: '/pc-tracker', label: '电脑记录', icon: Monitor },
      { to: '/pc-tracker/browser', label: '浏览器使用', icon: Globe },
      { to: '/mobile-records', label: '手机记录', icon: Smartphone },
      { to: '/location-history', label: '历史位置', icon: MapPinned },
    ],
  },
  {
    label: '收集',
    items: [
      { to: '/quick-notes', label: '快速记录', icon: Zap },
      { to: '/files', label: '文件', icon: FolderClosed },
    ],
  },
  {
    label: '治理',
    items: [
      { to: '/data-center', label: '数据中心', icon: Database },
      { to: '/reminders', label: '提醒', icon: Bell },
      { to: '/reports', label: '报告', icon: FileText },
      { to: '/habits', label: '习惯', icon: Repeat },
    ],
  },
  {
    label: '系统',
    items: [{ to: '/settings', label: '设置', icon: Settings }],
  },
]

/** 当前路径命中的导航项（最长前缀匹配，避免 /pc-tracker 与其子页同时高亮） */
export function activeNavPath(pathname: string): string | null {
  let best: string | null = null
  for (const group of NAV_GROUPS) {
    for (const item of group.items) {
      const hit =
        pathname === item.to || pathname.startsWith(item.to + '/')
      if (hit && (best === null || item.to.length > best.length)) {
        best = item.to
      }
    }
  }
  return best
}
