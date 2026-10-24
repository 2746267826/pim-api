import { useEffect, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { LogOut, PanelLeftClose, PanelLeftOpen, Server } from 'lucide-react'
import { useAuth } from '@/features/auth/auth-context'
import { useStatusSummary } from '@/api/version'
import { CalendarBooksManager } from '@/features/calendar/components/calendar-books-manager'
import { getApiBase } from '@/lib/apiBase'
import { HEALTH_LABEL, normalizeHealthStatus } from '@/lib/enums'
import { cn } from '@/lib/utils'
import { NAV_GROUPS, activeNavPath, type NavItem } from '../nav'

/** 当前生效的 API 地址（跟随 pim:api-base-changed 事件刷新） */
function useCurrentApiBase(): string {
  const [base, setBase] = useState(() => getApiBase())
  useEffect(() => {
    const update = () => setBase(getApiBase())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])
  return base
}

function NavItemButton({
  item,
  active,
  compact = false,
  onNavigate,
}: {
  item: NavItem
  active: boolean
  compact?: boolean
  onNavigate?: () => void
}) {
  const Icon = item.icon
  if (compact) {
    return (
      <Link
        to={item.to}
        viewTransition
        prefetch="intent"
        title={item.label}
        aria-label={item.label}
        aria-current={active ? 'page' : undefined}
        onClick={onNavigate}
        className={cn(
          'mx-auto flex h-9 w-9 items-center justify-center rounded-ctl transition-colors duration-150 outline-none',
          active
            ? 'bg-primary-soft text-primary-hover'
            : 'text-text-3 hover:bg-surface-2 hover:text-text-1',
        )}
      >
        <Icon className="size-5" strokeWidth={1.75} aria-hidden />
      </Link>
    )
  }
  return (
    <Link
      to={item.to}
      viewTransition
      prefetch="intent"
      aria-current={active ? 'page' : undefined}
      onClick={onNavigate}
      className={cn(
        'relative flex h-9 items-center gap-2.5 rounded-ctl px-2.5 text-[13px] font-medium transition-colors duration-150 outline-none',
        active
          ? 'bg-primary-soft text-primary-hover'
          : 'text-text-2 hover:bg-surface-2 hover:text-text-1',
      )}
    >
      {active && (
        <span
          className="absolute top-1.5 bottom-1.5 -left-2 w-[3px] rounded-full bg-primary"
          aria-hidden
        />
      )}
      <Icon className="size-5 shrink-0" strokeWidth={1.75} aria-hidden />
      <span className="truncate">{item.label}</span>
      {item.badgeChar && (
        <span className="ml-auto text-[10px] text-text-4">{item.badgeChar}</span>
      )}
    </Link>
  )
}

/*
 * 健康状态灯（唯一的系统状态指示）。
 * compact（折叠态）只渲染圆点 + title 提示：文字在 64px 栏里会折成两行，过于拥挤。
 * 侧边栏头部不再放装饰性蓝点——它与本状态灯同为「小圆点」，会被误读为两个状态灯。
 */
function SidebarStatusDot({ compact = false }: { compact?: boolean }) {
  const { data } = useStatusSummary()
  const status = normalizeHealthStatus(data?.status)
  const dotClass = {
    healthy: 'bg-ok',
    warning: 'bg-warn',
    critical: 'bg-crit status-dot-critical',
    unknown: 'bg-neutral',
  }[status]
  const label = data ? HEALTH_LABEL[status] : '状态未知'
  if (compact) {
    return (
      <Link
        to="/status"
        title={`系统状态：${label}`}
        aria-label={`系统状态：${label}`}
        className="grid size-8 place-items-center rounded-ctl transition-colors hover:bg-surface-2 outline-none"
      >
        <span className={cn('size-2.5 rounded-full', dotClass)} aria-hidden />
      </Link>
    )
  }
  return (
    <Link
      to="/status"
      className="flex items-center gap-1.5 rounded-ctl px-1 py-0.5 text-xs text-text-3 transition-colors hover:text-text-1 outline-none"
    >
      <span className={cn('size-2 rounded-full', dotClass)} aria-hidden />
      {label}
    </Link>
  )
}

function ApiBaseHint() {
  const base = useCurrentApiBase()
  return (
    <Link
      to="/settings/server"
      title={`当前服务器：${base || '本机同源'}（点击修改）`}
      className="mt-0.5 flex min-w-0 items-center gap-1 text-[11px] text-text-4 transition-colors hover:text-text-2 outline-none"
    >
      <Server className="size-3 shrink-0" aria-hidden />
      <span className="mono truncate">{base || '本机同源'}</span>
    </Link>
  )
}

function SidebarContent({
  compact = false,
  onNavigate,
  onToggle,
}: {
  compact?: boolean
  onNavigate?: () => void
  /** 桌面折叠切换；移动抽屉不传则不显示按钮 */
  onToggle?: () => void
}) {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const active = activeNavPath(pathname)

  return (
    <div className="flex h-full flex-col">
      {/* 头部：品牌名（不放装饰圆点，避免与底部状态灯混淆） */}
      <div className={cn('flex h-16 shrink-0 items-center gap-2', compact ? 'justify-center px-0' : 'px-4')}>
        {!compact && <span className="flex-1 text-base font-semibold tracking-wide text-text-1">PIM</span>}
        {onToggle && (
          <button
            type="button"
            title={compact ? '展开导航' : '折叠导航'}
            aria-label={compact ? '展开导航' : '折叠导航'}
            onClick={onToggle}
            className={cn(
              'rounded-ctl p-1.5 text-text-3 transition-colors hover:bg-surface-2 hover:text-text-1 outline-none',
              !compact && 'order-first',
            )}
          >
            {compact ? (
              <PanelLeftOpen className="size-4" aria-hidden />
            ) : (
              <PanelLeftClose className="size-4" aria-hidden />
            )}
          </button>
        )}
      </div>

      {/* 导航（overflow-x-hidden：折叠态下不产生横向滚动条） */}
      <nav className={cn('flex-1 overflow-y-auto overflow-x-hidden pb-2', compact ? 'px-2' : 'px-4')}>
        {NAV_GROUPS.map((group) => (
          <div key={group.label} className="mb-1">
            {!compact && (
              <div className="px-2.5 pt-3 pb-1 text-xs font-medium text-text-3">{group.label}</div>
            )}
            <div className={cn(!compact && 'space-y-0.5')}>
              {group.items.map((item) => (
                <NavItemButton
                  key={item.to}
                  item={item}
                  active={active === item.to}
                  compact={compact}
                  onNavigate={onNavigate}
                />
              ))}
            </div>
          </div>
        ))}

        {/* 日历本/任务本管理器：仅展开态显示（折叠时无处容纳，从图标栏隐藏） */}
        {!compact && <CalendarBooksManager onNavigate={onNavigate} />}
      </nav>

      {/* 底部：状态点 + 用户 + 服务器 */}
      <div className={cn('shrink-0 border-t border-divider', compact ? 'px-2 py-3' : 'px-4 py-3')}>
        {compact ? (
          <div className="flex flex-col items-center gap-2">
            <SidebarStatusDot compact />
            <button
              type="button"
              title={`退出登录（${(user?.displayName ?? user?.username) || ''}）`}
              onClick={() => {
                logout()
                navigate('/login')
              }}
              className="rounded-ctl p-1.5 text-text-3 transition-colors hover:bg-surface-2 hover:text-text-1 outline-none"
            >
              <LogOut className="size-4" aria-hidden />
            </button>
          </div>
        ) : (
          <>
            <SidebarStatusDot />
            <div className="mt-2 flex items-center justify-between gap-2">
              <div className="min-w-0">
                <div className="truncate text-[13px] font-medium text-text-1">
                  {user?.displayName || user?.username}
                </div>
                <ApiBaseHint />
              </div>
              <button
                type="button"
                title="退出登录"
                onClick={() => {
                  logout()
                  navigate('/login')
                }}
                className="rounded-ctl p-1.5 text-text-3 transition-colors hover:bg-surface-2 hover:text-text-1 outline-none"
              >
                <LogOut className="size-4" aria-hidden />
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

export { SidebarContent }
