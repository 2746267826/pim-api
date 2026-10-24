import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider, QueryCache, MutationCache } from '@tanstack/react-query'
import { Toaster } from 'sonner'
import App from './App'
import './index.css'
import { installInteractionDeferral } from './lib/autoRefresh'
import { showApiError } from './components/error/ApiErrorToast'

function shouldRetry(failureCount: number, error: unknown): boolean {
  if (failureCount >= 2) return false;
  if (error instanceof DOMException && error.name === 'AbortError') return false;
  // 结构化 { status }：所有 4xx 不重试（覆盖 429/409 等）
  if (typeof error === 'object' && error !== null) {
    const maybe = error as { status?: number };
    if (typeof maybe.status === 'number' && maybe.status >= 400 && maybe.status < 500) return false;
  }
  const msg = error instanceof Error ? error.message ?? '' : String(error ?? '');
  if (/\b4\d{2}\b/.test(msg) || msg.includes('登录已过期') || msg.includes('Unauthorized')) return false;
  if (msg.includes('AbortError')) return false;
  return true;
}

const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => {
      // 展览馆 query 已在 hook 内静默回退到 fakeData，不需要全局 toast（避免 9 卡洪水）
      const key = query?.queryKey?.[0];
      if (key === 'exh') return;
      // 噪声抑制：5xx 在后台轮询场景下不做全局 toast，避免多页面频繁弹窗覆盖真实操作反馈
      // 仅在初始加载（无数据）或用户可见的非轮询查询时提示；轮询查询由组件内错误态自行展示
      const isServerToast = (() => {
        if (typeof error === 'object' && error !== null) {
          const maybe = error as { status?: number };
          if (typeof maybe.status === 'number' && maybe.status >= 500) return true;
        }
        const msg = error instanceof Error ? error.message ?? '' : String(error ?? '');
        return /\b5\d{2}\b/.test(msg) || msg.includes('服务器异常');
      })();
      if (isServerToast) {
        const hasData = (query as unknown as { state?: { data?: unknown } })?.state?.data !== undefined;
        const isBackgroundPolling = Boolean((query?.options as unknown as { refetchInterval?: unknown })?.refetchInterval);
        if (hasData || isBackgroundPolling) return;
      }
      // 按 queryKey 去重，避免不同页面的同文案 5xx 互相压制导致某一页面的错误被误判为已提示
      // 使用 JSON.stringify 稳定序列化，避免 [object Object] 归一化失效
      let keyPart = '';
      try {
        keyPart = query?.queryKey ? JSON.stringify(query.queryKey) : '';
      } catch {
        keyPart = query?.queryKey ? String(query.queryKey[0]) : '';
      }
      const dedupeKey = keyPart ? `q:${keyPart}` : undefined;
      // 同时在 dedupeKey 中保留 message 语义，避免不同错误类型互相覆盖
      const messageKey = dedupeKey ? `${dedupeKey}:${(error as Error)?.message?.slice(0, 20) ?? ''}` : undefined;
      showApiError(error, { dedupeKey: messageKey });
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      // mutation 失败不做整页 reload，仅提示；action 由具体 mutation 的 onError 另行提供可重试回调
      const key = mutation?.options?.mutationKey ? String(mutation.options.mutationKey) : undefined;
      showApiError(error, { dedupeKey: key ? `mut:${key}` : undefined });
    },
  }),
  defaultOptions: {
    queries: {
      retry: shouldRetry,
      retryDelay: 1000,
      refetchIntervalInBackground: false,
      structuralSharing: true,
    },
  },
})

installInteractionDeferral()

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
      <Toaster position="top-right" richColors closeButton toastOptions={{ className: 'pim-toast' }} />
    </QueryClientProvider>
  </React.StrictMode>
)
