import { Component, type ReactNode } from 'react';

interface State {
  hasError: boolean;
  retryCount: number;
}

export default class AndroidEmbedLayout extends Component<{ children: ReactNode }, State> {
  state: State = { hasError: false, retryCount: 0 };

  static getDerivedStateFromError(): Partial<State> {
    return { hasError: true };
  }

  componentDidCatch() {
    // error captured; state already set by getDerivedStateFromError
  }

  render() {
    if (this.state.hasError) {
      if (this.state.retryCount >= 3) {
        return (
          <div role="alert" className="w-full overflow-y-auto bg-white flex flex-col items-center justify-center gap-4 p-4">
            <h2 className="text-lg font-semibold text-gray-800">多次重试失败</h2>
            <p className="text-sm text-gray-500">请稍后重试或联系支持</p>
          </div>
        );
      }
      return (
        <div
          role="alert"
          className="w-full overflow-y-auto bg-white flex flex-col items-center justify-center gap-4 p-4"
          style={{
            paddingTop: 'env(safe-area-inset-top, 0px)',
            paddingRight: 'env(safe-area-inset-right, 0px)',
            paddingBottom: 'env(safe-area-inset-bottom, 0px)',
            paddingLeft: 'env(safe-area-inset-left, 0px)',
          }}
        >
          <h2 className="text-lg font-semibold text-gray-800">页面暂时无法显示</h2>
          <button
            type="button"
            className="rounded bg-blue-600 px-4 py-2 text-white text-sm"
            onClick={() => this.setState((s) => ({ hasError: false, retryCount: s.retryCount + 1 }))}
          >
            重试
          </button>
        </div>
      );
    }

    return (
      <div
        className="w-full overflow-y-auto bg-white"
        style={{
          paddingTop: 'env(safe-area-inset-top, 0px)',
          paddingRight: 'env(safe-area-inset-right, 0px)',
          paddingBottom: 'env(safe-area-inset-bottom, 0px)',
          paddingLeft: 'env(safe-area-inset-left, 0px)',
        }}
      >
        {this.props.children}
      </div>
    );
  }
}
