import React from 'react';
import ErrorPage from './ErrorPage';

interface Props {
  children: React.ReactNode;
  fallback?: React.ReactNode;
  resetKeys?: unknown[];
  onReset?: () => void;
}

interface State {
  hasError: boolean;
  error: Error | null;
  retryCount: number;
}

export class ErrorBoundary extends React.Component<Props, State> {
  state: State = { hasError: false, error: null, retryCount: 0 };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    console.error('[ErrorBoundary]', error, info.componentStack);
  }

  componentDidUpdate(prevProps: Props) {
    if (!this.state.hasError) return;
    if (!this.props.resetKeys) return;
    if (!prevProps.resetKeys) {
      this.reset();
      return;
    }
    const changed = this.props.resetKeys.length !== prevProps.resetKeys.length
      || this.props.resetKeys.some((k, i) => k !== prevProps.resetKeys![i]);
    if (changed) this.reset();
  }

  private reset = () => {
    this.setState({ hasError: false, error: null, retryCount: 0 });
    this.props.onReset?.();
  };

  private handleRetry = () => {
    if (this.state.retryCount >= 3) return;
    this.setState((s) => ({ hasError: false, error: null, retryCount: s.retryCount + 1 }));
    this.props.onReset?.();
  };

  render() {
    if (this.state.hasError) {
      if (this.state.retryCount >= 3) {
        return <ErrorPage error={this.state.error} title="多次重试失败" emoji="😵" onRetry={undefined} />;
      }
      if (this.props.fallback) return this.props.fallback;
      return <ErrorPage error={this.state.error} onRetry={this.handleRetry} />;
    }
    return this.props.children;
  }
}

export default ErrorBoundary;
