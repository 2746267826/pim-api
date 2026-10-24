import { Component } from 'react'
import type { ErrorInfo, ReactNode } from 'react'
import { AlertTriangle } from 'lucide-react'
import { Button } from './button'

interface ErrorBoundaryProps {
  /** page = 页面级错误边界（提示+重试+回首页）；card = 卡片级（模块内提示，不影响整页） */
  level?: 'page' | 'card'
  children: ReactNode
}

interface ErrorBoundaryState {
  error: Error | null
  stack: string | null
}

/**
 * 错误边界（01 §7）：页面级与卡片级共用，出错的模块不影响整页其它模块。
 * 数据层错误走 TanStack Query 重试与 toast；本组件只兜渲染异常。
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null, stack: null }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error, stack: error.stack?.split('\n').slice(1, 5).join('\n') ?? null }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[PIM] 渲染错误', error, info.componentStack)
  }

  private reset = () => this.setState({ error: null })

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    if (this.props.level === 'card') {
      return (
        <div className="rounded-card border border-crit-border bg-crit-soft px-4 py-3 text-[13px] text-crit">
          <div className="font-medium">模块加载失败</div>
          <div className="mt-0.5 break-all text-crit/80">{error.message}</div>
          <button
            type="button"
            onClick={this.reset}
            className="mt-1.5 underline underline-offset-2 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring"
          >
            重试
          </button>
        </div>
      )
    }

    return (
      <div className="flex min-h-[60vh] flex-col items-center justify-center gap-3 p-8 text-center">
        <AlertTriangle className="size-8 text-warn" strokeWidth={1.5} aria-hidden />
        <div className="text-base font-semibold text-text-1">页面出错了</div>
        <div className="max-w-[420px] text-[13px] break-all text-text-3">{error.message}</div>
        {this.state.stack && (
          <pre className="mono max-w-[720px] overflow-auto rounded-ctl bg-surface px-4 py-3 text-left text-xs text-text-3">
            {this.state.stack}
          </pre>
        )}
        <div className="mt-2 flex gap-2">
          <Button variant="primary" onClick={this.reset}>
            重试
          </Button>
          <Button variant="secondary" onClick={() => window.location.assign('/')}>
            回首页
          </Button>
        </div>
      </div>
    )
  }
}
