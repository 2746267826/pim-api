import { Hammer } from 'lucide-react'
import { Card, EmptyState, PageHeader } from '@/components/ui'

/** 后续阶段的占位页：保持路由与导航可走通 */
export function UnderConstruction({
  title,
  subtitle,
}: {
  title: string
  subtitle?: string
}) {
  return (
    <div>
      <PageHeader title={title} subtitle={subtitle} />
      <Card>
        <EmptyState
          icon={Hammer}
          title="页面建设中"
          description="该页面属于后续阶段交付范围，当前阶段先打通导航与外壳。"
        />
      </Card>
    </div>
  )
}

export function NotFoundPage() {
  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4">
      <div className="flex flex-col items-center gap-3 text-center">
        <div className="tnum text-5xl font-semibold text-text-4">404</div>
        <div className="text-base font-semibold text-text-1">页面不存在</div>
        <p className="text-[13px] text-text-3">你访问的地址没有匹配到任何页面。</p>
        <a
          href="/"
          className="mt-2 rounded-ctl border border-border bg-bg px-4 py-2 text-sm text-text-1 shadow-card transition-colors hover:border-border-strong outline-none"
        >
          回首页
        </a>
      </div>
    </div>
  )
}
