import React from 'react';

interface Props {
  children: React.ReactNode;
  cardTitle?: string;
  resetKeys?: unknown[];
  onRetry?: () => void;
}

interface State {
  hasError: boolean;
  error: Error | null;
  retryCount: number;
}

export class CardErrorBoundary extends React.Component<Props, State> {
  state: State = { hasError: false, error: null, retryCount: 0 };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    console.error('[CardErrorBoundary]', this.props.cardTitle, error, info.componentStack);
  }

  componentDidUpdate(prevProps: Props) {
    if (!this.state.hasError) return;
    const a = this.props.resetKeys, b = prevProps.resetKeys;
    if (!a && !b) return;
    if (!a || !b || a.length !== b.length || a.some((k, i) => k !== b![i])) {
      this.setState({ hasError: false, error: null, retryCount: 0 });
    }
  }

  private handleRetry = () => {
    if (this.state.retryCount >= 3) return;
    this.setState((s) => ({ hasError: false, error: null, retryCount: s.retryCount + 1 }));
    this.props.onRetry?.();
  };

  render() {
    if (this.state.hasError) {
      if (this.state.retryCount >= 3) {
        return (
          <div className="grid min-h-[168px] place-items-center rounded-md border border-red-200 bg-red-50 p-4 text-center">
            <div className="space-y-2">
              <div className="text-xs font-semibold text-red-600">多次重试失败</div>
              <div className="mx-auto max-w-[260px] truncate text-xs text-red-500">请刷新页面或稍后重试</div>
            </div>
          </div>
        );
      }
      return (
        <div className="grid min-h-[168px] place-items-center rounded-md border border-red-200 bg-red-50 p-4 text-center">
          <div className="space-y-2">
            <div className="text-xs font-semibold text-red-600">
              {this.props.cardTitle ? `“${this.props.cardTitle}” 加载失败` : '卡片加载失败'}
            </div>
            <div className="mx-auto max-w-[260px] truncate text-xs text-red-500">
              {this.state.error?.message ?? '组件渲染时发生错误'}
            </div>
            <button
              type="button"
              onClick={this.handleRetry}
              className="rounded-lg border border-red-200 bg-white px-3 py-1 text-xs font-semibold text-red-600 hover:bg-red-50"
            >
              重试
            </button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}

export default CardErrorBoundary;
