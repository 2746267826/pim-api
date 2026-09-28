import { Link } from 'react-router-dom';
import PageHeader from '../ui/PageHeader';
import AboutPimCard from '../components/AboutPimCard';
import { useAuth } from '../auth/AuthContext';
import { SETTINGS_SECTION_ITEMS } from '../layout/navItems';

const settingsLinks = [
  {
    title: '数据可信度',
    description: '查看 13 项数据可信度体检结果、违规样例与历史欠账',
    label: '体检',
    to: '/settings/data-reliability',
  },
  {
    title: '管理日程数据',
    description: '查看、筛选、导入导出全部日程数据',
    label: '日程',
    to: '/settings/calendar-data',
  },
  {
    title: '回收站',
    description: '恢复已删除的日程、任务、日历本和任务本',
    label: '恢复',
    to: '/settings/recycle-bin',
  },
  {
    title: 'PC 记录详细数据',
    description: '查询、筛选、导出全部 PC 记录数据',
    label: 'PC',
    to: '/settings/pc-data',
  },
  {
    title: '同步设置',
    description: '配置微软日历连接、设备代码登录、同步批次与冲突策略',
    label: '同步',
    to: '/settings/sync',
  },
  {
    title: 'AI 设置',
    description: 'LiteLLM 状态、用量、请求日志与详情',
    label: 'AI',
    to: '/settings/ai',
  },
  {
    title: 'MCP 连接',
    description: '管理 AI Agent 客户端连接、Token 与工具级权限',
    label: 'MCP',
    to: '/settings/mcp',
  },
] as const;

const adminLink = {
  title: '用户管理',
  description: '管理账号角色与启用状态（仅管理员可见）',
  label: '用户',
  to: '/settings/users',
} as const;

export default function SettingsPage() {
  const { role } = useAuth();
  const links = role === 'admin' ? [...settingsLinks, adminLink] : settingsLinks;

  return (
    <div className="mx-auto max-w-2xl space-y-6 pb-20">
      <PageHeader title="设置" subtitle="管理数据入口与本地记录" />

      {links.map(link => (
        <Link
          key={link.to}
          to={link.to}
          className="pim-card flex min-h-[44px] w-full cursor-pointer items-center justify-between gap-4 rounded-lg border p-5 text-left transition-colors hover:border-blue-200 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-200"
        >
          <div className="flex min-w-0 items-center gap-4">
            <span className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl border border-blue-100 bg-blue-50 text-sm font-semibold text-blue-700">
              {link.label}
            </span>
            <span className="min-w-0">
              <span className="block truncate text-base font-semibold text-slate-950">{link.title}</span>
              <span className="mt-1 block break-words text-sm text-slate-500">{link.description}</span>
            </span>
          </div>
          <span className="shrink-0 text-xl text-slate-300" aria-hidden="true">
            →
          </span>
        </Link>
      ))}
      <div className="space-y-3">
        <h2 className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">板块入口</h2>
        {SETTINGS_SECTION_ITEMS.map(section => (
          <Link
            key={section.path}
            to={section.path}
            className="pim-card flex min-h-[44px] w-full cursor-pointer items-center justify-between gap-4 rounded-lg border p-5 text-left transition-colors hover:border-blue-200 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-200"
          >
            <div className="flex min-w-0 items-center gap-4">
              <span className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl border border-blue-100 bg-blue-50 text-sm font-semibold text-blue-700">
                {section.label}
              </span>
              <span className="min-w-0">
                <span className="block truncate text-base font-semibold text-slate-950">{section.title}</span>
                <span className="mt-1 block break-words text-sm text-slate-500">{section.description}</span>
              </span>
            </div>
            <span className="shrink-0 text-xl text-slate-300" aria-hidden="true">
              →
            </span>
          </Link>
        ))}
      </div>

      <AboutPimCard />
    </div>
  );
}
