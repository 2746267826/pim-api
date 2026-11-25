// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import type { GlobalSearchParams, GlobalSearchResult } from './types'
import { useGlobalSearch, useSearchIdentity } from './queries'

const authState = vi.hoisted(() => ({ userId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' }))
const searchApiMock = vi.hoisted(() => ({ query: vi.fn() }))

vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: authState.userId } }),
}))
vi.mock('./api', () => ({ searchApi: searchApiMock }))

const params: GlobalSearchParams = {
  q: '研究',
  scope: 'all',
  match: 'all',
  sort: 'relevance',
  page: 1,
  pageSize: 20,
}

function result(query: string): GlobalSearchResult {
  return {
    items: [],
    page: 1,
    pageSize: 20,
    totalCount: 0,
    totalPages: 0,
    query,
    scope: 'all',
    match: 'all',
    sort: 'relevance',
    groups: [],
    generatedAt: '2026-11-25T00:00:00Z',
  }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('useGlobalSearch identity fence', () => {
  it('uses a separate cache key per account so a switch cannot show the old account result', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 60_000 } } })
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    )
    let resolveSecond!: (value: GlobalSearchResult) => void
    searchApiMock.query
      .mockResolvedValueOnce(result('账号 A'))
      .mockImplementationOnce(() => new Promise((resolve) => {
        resolveSecond = resolve
      }))

    const hook = renderHook(() => {
      const identity = useSearchIdentity()
      return useGlobalSearch(params, identity)
    }, { wrapper })

    await waitFor(() => expect(hook.result.current.data?.query).toBe('账号 A'))
    authState.userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    hook.rerender()

    await waitFor(() => expect(searchApiMock.query).toHaveBeenCalledTimes(2))
    expect(hook.result.current.data).toBeUndefined()

    resolveSecond(result('账号 B'))
    await waitFor(() => expect(hook.result.current.data?.query).toBe('账号 B'))
    expect(client.getQueryCache().findAll({ queryKey: ['search', 'global'] }))
      .toHaveLength(2)
  })
})
