import {
  MutationCache,
  QueryCache,
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { MotionConfig } from 'motion/react'
import { Toaster } from 'sonner'
import { ApiError } from '@/api/client'
import { notifyError } from '@/lib/notify'

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

/** 03 §1：重试最多 2 次、间隔 1 秒；4xx / 用户中止 / 登录过期不重试 */
function queryRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  if (isAbort(error)) return false
  return failureCount < 2
}

export function Providers({ children }: { children: ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            retry: queryRetry,
            retryDelay: 1_000,
            refetchIntervalInBackground: false,
            staleTime: 30_000,
          },
          mutations: { retry: false },
        },
        queryCache: new QueryCache({
          onError: (error, query) => {
            const meta = query.meta as { silent?: boolean } | undefined
            if (meta?.silent) return
            const apiError = error instanceof ApiError ? error : undefined
            notifyError(apiError?.message ?? '网络请求失败，请稍后重试', {
              key: `q:${JSON.stringify(query.queryKey)}`,
              status: apiError?.status,
              hasCache: query.state.data !== undefined,
            })
          },
        }),
        mutationCache: new MutationCache({
          onError: (error, _variables, _context, mutation) => {
            const meta = mutation.meta as { silent?: boolean } | undefined
            if (meta?.silent) return
            const apiError = error instanceof ApiError ? error : undefined
            notifyError(apiError?.message ?? '操作失败，请稍后重试', {
              key: `m:${String(mutation.mutationId)}`,
              status: apiError?.status,
            })
          },
        }),
      }),
  )

  return (
    <QueryClientProvider client={queryClient}>
      {/* reducedMotion="user"：系统开启「减少动效」时，所有 motion 动画自动退化为直出 */}
      <MotionConfig reducedMotion="user">
        {children}
        <Toaster
          position="bottom-right"
          toastOptions={{
            style: {
              background: 'var(--color-inverse)',
              color: '#ffffff',
              border: 'none',
              borderRadius: '10px',
              fontSize: '13px',
            },
          }}
        />
      </MotionConfig>
    </QueryClientProvider>
  )
}
