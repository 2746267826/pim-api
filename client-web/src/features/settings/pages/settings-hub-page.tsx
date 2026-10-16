import { Link } from 'react-router'
import {
  Activity,
  BookOpen,
  Bot,
  CalendarCog,
  Cloud,
  Copy,
  Database,
  HardDrive,
  Images,
  KeyRound,
  Plug,
  Server,
  ShieldCheck,
  Table,
  Trash2,
  Users,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { useAuth } from '@/features/auth/auth-context'
import { useVersionInfo } from '@/api/version'
import { Card, CardSubtitle, CardTitle, Chip, PageHeader, StatusBadge } from '@/components/ui'
import { getApiBase } from '@/lib/apiBase'
import { notifySuccess } from '@/lib/notify'
import { APP_VERSION } from '@/api/version'

interface SettingCard {
  to: string
  title: string
  description: string
  icon: LucideIcon
  /** 仅当页面尚未实现时标记（当前仅展览馆） */
  badge?: '建设中'
  /** 该卡片对应页面的接口要求 Admin 角色（ai/admin 组 Roles="admin"），非管理员不展示 */
  adminOnly?: boolean
}

interface SettingSection {
  label: string
  cards: SettingCard[]
}

const SECTIONS: SettingSection[] = [
  {
    /*
     * 「账户」是对**全员可见**的分组（不设 adminOnly）。
     * 账号安全卡必须落在这里，不能放进管理员专属的「组织」组——
     * 否则普通用户看不到入口，Q2「用户可自行更换密码」直接失效（AC-14.1）。
     */
    label: '账户',
    cards: [
      {
        to: '/settings/account',
        title: '账号安全',
        description: '修改登录密码',
        icon: KeyRound,
      },
    ],
  },
  {
    label: '连接',
    cards: [
      {
        to: '/settings/server',
        title: '服务器',
        description: 'API 地址、连接测试与最近连接',
        icon: Server,
      },
      {
        to: '/settings/microsoft',
        title: 'Microsoft 账户',
        description: 'Outlook 日历同步与 OneDrive 文件绑定',
        icon: Cloud,
      },
      {
        to: '/settings/google',
        title: 'Google Drive',
        description: '连接 Google 网盘账号（浏览器跳转授权）',
        icon: HardDrive,
      },
    ],
  },
  {
    label: '智能与自动化',
    cards: [
      { to: '/settings/ai', title: 'AI 网关', description: '状态、用量与请求日志', icon: Bot, adminOnly: true },
      { to: '/settings/mcp', title: 'MCP 连接', description: '客户端、令牌与工具权限', icon: Plug },
    ],
  },
  {
    label: '数据',
    cards: [
      { to: '/settings/data-reliability', title: '数据可信度', description: '13 规则数据体检', icon: ShieldCheck },
      { to: '/settings/calendar-data', title: '日程数据管理', description: '事件批量管理与 ICS 导入导出', icon: CalendarCog },
      { to: '/settings/recycle-bin', title: '回收站', description: '日历域回收站与恢复预览', icon: Trash2 },
      { to: '/settings/pc-data', title: 'PC 明细查询', description: 'PC 原始记录 14 字段明细', icon: Table },
    ],
  },
]

export function SettingsHubPage() {
  const { user } = useAuth()
  const { data: version } = useVersionInfo()

  const sections = SECTIONS.map((section) => ({
    ...section,
    cards: section.cards.filter((card) => !card.adminOnly || user?.role === 'admin'),
  })).filter((section) => section.cards.length > 0)
  if (user?.role === 'admin') {
    sections.push({
      label: '组织',
      cards: [
        { to: '/settings/users', title: '用户管理', description: '用户与角色管理', icon: Users },
      ],
    })
  }
  sections.push({
    label: '板块入口',
    cards: [
      { to: '/exhibition', title: '展览馆', description: '图表组件陈列馆', icon: Images, badge: '建设中' },
      { to: '/status', title: '状态', description: '系统与组件状态', icon: Activity },
      { to: '/devices', title: '设备管理', description: '移动设备合并/改名/导出', icon: HardDrive },
      { to: '/app-knowledge-base', title: '应用知识库', description: '应用/域名知识与分类树', icon: BookOpen },
    ],
  })

  return (
    <div>
      <PageHeader title="设置" subtitle="系统配置与数据治理的枢纽" />

      <div className="max-w-[880px] space-y-6">
        {sections.map((section) => (
          <section key={section.label}>
            <h2 className="mb-2 text-xs font-medium text-text-3">{section.label}</h2>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {section.cards.map((card) => {
                const Icon = card.icon
                const inner = (
                  <>
                    <div className="flex items-center gap-2.5">
                      <span className="grid size-8 shrink-0 place-items-center rounded-ctl bg-surface-2 text-text-2">
                        <Icon className="size-4" strokeWidth={1.75} aria-hidden />
                      </span>
                      <CardTitle className="truncate">{card.title}</CardTitle>
                      {card.badge && (
                        <StatusBadge tone="neutral" dot={false} className="ml-auto shrink-0">
                          {card.badge}
                        </StatusBadge>
                      )}
                    </div>
                    <CardSubtitle className="mt-2">{card.description}</CardSubtitle>
                  </>
                )
                return card.badge ? (
                  <Card key={card.to} className="p-4 opacity-90">
                    {inner}
                  </Card>
                ) : (
                  <Link
                    key={card.to}
                    to={card.to}
                    className="rounded-card border border-border bg-bg shadow-card transition-colors outline-none hover:border-border-strong focus-visible:outline-2 focus-visible:outline-primary-ring"
                  >
                    <div className="p-4">{inner}</div>
                  </Link>
                )
              })}
            </div>
          </section>
        ))}

        {/* 关于 */}
        <section>
          <h2 className="mb-2 text-xs font-medium text-text-3">关于</h2>
          <Card className="p-4">
            <div className="flex flex-wrap items-center gap-x-6 gap-y-2 text-[13px] text-text-2">
              <span className="tnum">
                本地版本 <b className="font-semibold text-text-1">v{APP_VERSION}</b>
              </span>
              <span className="tnum">
                API 版本 <b className="font-semibold text-text-1">{version?.version ?? '—'}</b>
              </span>
              {version?.latestVersion && version.latestVersion !== version.version && (
                <StatusBadge tone="warn" dot={false}>有可用更新 {version.latestVersion}</StatusBadge>
              )}
              <span className="mono text-xs text-text-4">{getApiBase() || '本机同源'}</span>
              <Chip
                className="ml-auto"
                onClick={() => {
                  void navigator.clipboard
                    .writeText(`PIM v${APP_VERSION} / API ${version?.version ?? 'unknown'}`)
                    .then(() => notifySuccess('版本信息已复制'))
                }}
              >
                <Copy className="size-3" aria-hidden /> 复制
              </Chip>
            </div>
            {version?.capabilities && version.capabilities.length > 0 && (
              <div className="mt-3 flex flex-wrap gap-1.5">
                {version.capabilities.map((cap) => (
                  <Chip key={cap} className="mono h-6 cursor-default px-2.5 text-xs" active>
                    {cap}
                  </Chip>
                ))}
              </div>
            )}
          </Card>
        </section>

        <p className="text-xs text-text-4">
          数据中心（<Database className="mb-0.5 inline size-3" />）与提醒等治理页面见一级导航对应分组。
        </p>
      </div>
    </div>
  )
}
