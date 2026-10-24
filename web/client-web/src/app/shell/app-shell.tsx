import { useEffect, useState } from 'react'
import { Link, useLocation } from 'react-router'
import { LazyOutlet } from '../lazy-outlet'
import { Menu } from 'lucide-react'
import { APP_VERSION, useVersionInfo } from '@/api/version'
import { StatusBadge } from '@/components/ui'
import { ErrorBoundary } from '@/components/ui'
import { getApiBase } from '@/lib/apiBase'
import { installScrollTracking } from '@/lib/polling'
import { Drawer, DrawerContent } from '@/components/ui'
import { cn } from '@/lib/utils'
import { GlobalFab } from './global-fab'
import { SidebarContent } from './sidebar'
import { useSidebarCollapsed } from './use-sidebar-collapsed'
import { CalendarVisibilityProvider } from '@/features/calendar/calendar-visibility'
import { GlobalEditorsProvider } from './global-editors'

/** 页脚：本地版本 / API 版本 / "有可用更新"标记 / 当前服务器 */
function AppFooter() {
  const { data } = useVersionInfo()
  const hasUpdate =
    data?.latestVersion != null && data.latestVersion !== data.version

  return (
    <footer className="flex h-10 shrink-0 items-center gap-4 border-t border-border bg-bg px-4 text-xs text-text-4 md:px-6">
      <span className="tnum">本地 v{APP_VERSION}</span>
      <span className="tnum">API {data?.version ?? '—'}</span>
      {hasUpdate && <StatusBadge tone="warn" className="h-[18px]">有可用更新</StatusBadge>}
      <span className="mono ml-auto hidden truncate sm:inline">
        {getApiBase() || '本机同源'}
      </span>
    </footer>
  )
}

/**
 * 全局外壳（规格 01 §2）：
 * ≥1280 常驻侧边栏 232px；640–1279 图标栏 64px；<640 顶部条 + 抽屉。
 */
export function AppShell() {
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const { pathname } = useLocation()
  const { collapsed, toggle } = useSidebarCollapsed()

  // 全局滚动活动跟踪（延迟轮询依赖）
  useEffect(() => installScrollTracking(), [])

  // 路由变化时收起移动抽屉
  useEffect(() => {
    setMobileNavOpen(false)
  }, [pathname])

  return (
    <CalendarVisibilityProvider>
      <GlobalEditorsProvider>
      {/*
        外壳高度策略：整体锁在视口内（h-dvh + overflow-hidden），由 <main> 承担滚动。
        这样侧边栏与页脚常驻，内容不会把整个文档撑高——否则「文件」这类三栏布局
        会出现整页滚动，中间列表的内部滚动永远不生效。
      */}
      <div className="flex h-dvh flex-col overflow-hidden">
      {/* 移动顶条 */}
      <header className="sticky top-0 z-30 flex h-14 shrink-0 items-center gap-2 border-b border-border bg-bg px-3 md:hidden">
        <button
          type="button"
          aria-label="打开导航"
          onClick={() => setMobileNavOpen(true)}
          className="rounded-ctl p-2 text-text-2 transition-colors hover:bg-surface hover:text-text-1 outline-none"
        >
          <Menu className="size-5" aria-hidden />
        </button>
        <Link to="/today" className="flex items-center gap-2">
          <span className="size-2.5 rounded-full bg-primary" aria-hidden />
          <span className="text-base font-semibold text-text-1">PIM</span>
        </Link>
      </header>

      <div className="flex min-h-0 flex-1">
        {/* 平板图标栏 */}
        <aside className="hidden h-full shrink-0 border-r border-border bg-surface md:block lg:hidden">
          <SidebarContent compact />
        </aside>

        {/* 桌面侧边栏（可折叠为图标栏；折叠状态持久化） */}
        <aside
          className={cn(
            'hidden h-full shrink-0 border-r border-border bg-surface transition-[width] duration-200 lg:block',
            collapsed ? 'w-16' : 'w-[232px]',
          )}
        >
          <SidebarContent compact={collapsed} onToggle={toggle} />
        </aside>

        {/* 主内容区：外壳内唯一滚动容器；每页独立错误边界 */}
        <main className="min-w-0 flex-1 overflow-y-auto px-4 py-4 md:px-5 md:py-5 lg:px-6 lg:py-6">
          <ErrorBoundary level="page">
            <LazyOutlet />
          </ErrorBoundary>
        </main>
      </div>

      <AppFooter />
      <GlobalFab />

      {/* 移动导航抽屉 */}
      <Drawer open={mobileNavOpen} onOpenChange={setMobileNavOpen}>
        <DrawerContent side="left">
          <SidebarContent onNavigate={() => setMobileNavOpen(false)} />
        </DrawerContent>
      </Drawer>
      </div>
      </GlobalEditorsProvider>
    </CalendarVisibilityProvider>
  )
}
