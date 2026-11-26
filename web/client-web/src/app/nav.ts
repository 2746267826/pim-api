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
  NotebookPen,
  Repeat,
  Search,
  Settings,
  ShieldCheck,
  Smartphone,
  Sunrise,
  Trash2,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

export interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  /** 右侧 1 字符徽标位（P1 起接入计数） */
  badgeChar?: string
  /** 管理/运维入口；普通用户不显示，路由层仍需独立守卫 */
  adminOnly?: boolean
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

/** 一级导航 17 项，按 v0.2 页面设计分 5 组 */
export const NAV_GROUPS: NavGroup[] = [
  {
    label: '工作',
    items: [
      { to: '/today', label: '今日', icon: Sunrise },
      { to: '/search', label: '搜索', icon: Search },
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
      { to: '/native-notes', label: '笔记', icon: NotebookPen },
      { to: '/files', label: '文件', icon: FolderClosed },
    ],
  },
  {
    label: '治理',
    items: [
      { to: '/data-center', label: '数据中心', icon: Database, adminOnly: true },
      { to: '/reminders', label: '提醒', icon: Bell },
      { to: '/reports', label: '报告', icon: FileText },
      { to: '/habits', label: '习惯', icon: Repeat },
    ],
  },
  {
    label: '系统',
    items: [
      { to: '/recycle-bin', label: '回收站', icon: Trash2 },
      { to: '/settings', label: '设置', icon: Settings },
    ],
  },
]

/** 按当前角色过滤导航；隐藏不等于授权，路由与 API 仍需各自校验。 */
export function navGroupsForRole(role: string | null | undefined): NavGroup[] {
  return NAV_GROUPS
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => !item.adminOnly || role === 'admin'),
    }))
    .filter((group) => group.items.length > 0)
}

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
