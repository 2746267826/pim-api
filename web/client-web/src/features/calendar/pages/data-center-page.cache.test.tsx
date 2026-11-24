// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { getApiBase, setApiBase } from '@/lib/apiBase'
import { DataCenterPage } from './data-center-page'

const authState = vi.hoisted(() => ({ userId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' }))
const dataCenterApiMock = vi.hoisted(() => ({
  query: vi.fn(),
  auditExport: vi.fn(),
  restorePreview: vi.fn(),
}))
const auditApiMock = vi.hoisted(() => ({
  timeline: vi.fn(),
}))

vi.mock('../api', () => ({ dataCenterApi: dataCenterApiMock }))
vi.mock('@/features/operations/api', () => ({ auditApi: auditApiMock }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({
    user: authState.userId
      ? { id: authState.userId, username: authState.userId, displayName: authState.userId, role: 'user' }
      : null,
  }),
}))

let client: QueryClient

function resultForCurrentBinding() {
  const owner = authState.userId || 'logged-out'
  const server = getApiBase() || window.location.origin
  return {
    items: [{
      objectType: 'task',
      objectId: '11111111-1111-4111-8111-111111111111',
      title: `${owner}@${server}`,
      source: 'manual',
      status: 'Active',
      startsAt: '2026-11-01T10:00:00Z',
      endsAt: null,
      summary: 'owner/server scoped cache probe',
    }],
    page: 1,
    pageSize: 50,
    totalCount: 1,
  }
}

function renderPage() {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 30_000 },
      mutations: { retry: false },
    },
  })
  return render(<QueryClientProvider client={client}><DataCenterPage /></QueryClientProvider>)
}

beforeEach(() => {
  authState.userId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  dataCenterApiMock.query.mockReset()
  dataCenterApiMock.query.mockImplementation(async () => resultForCurrentBinding())
  auditApiMock.timeline.mockReset()
  auditApiMock.timeline.mockResolvedValue({ items: [] })
})

afterEach(() => {
  cleanup()
  client?.clear()
  setApiBase('')
  vi.restoreAllMocks()
})

describe('DataCenterPage cache isolation', () => {
  it('does not show the previous account data while the next account query is still pending', async () => {
    const firstOwner = authState.userId
    const firstTitle = `${firstOwner}@${window.location.origin}`
    const view = renderPage()
    expect(await screen.findByText(firstTitle)).toBeTruthy()

    let resolveSecond!: (value: ReturnType<typeof resultForCurrentBinding>) => void
    dataCenterApiMock.query.mockImplementationOnce(() => new Promise((resolve) => {
      resolveSecond = resolve
    }))
    authState.userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    const secondTitle = `${authState.userId}@${window.location.origin}`
    view.rerender(<QueryClientProvider client={client}><DataCenterPage /></QueryClientProvider>)

    await waitFor(() => expect(dataCenterApiMock.query).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText(firstTitle)).toBeNull())

    resolveSecond(resultForCurrentBinding())
    expect(await screen.findByText(secondTitle)).toBeTruthy()
  })

  it('drops the old server result when the API base changes', async () => {
    const owner = authState.userId
    const firstTitle = `${owner}@${window.location.origin}`
    renderPage()
    expect(await screen.findByText(firstTitle)).toBeTruthy()

    setApiBase('https://server-b.synthetic.invalid')
    const secondTitle = `${owner}@https://server-b.synthetic.invalid`

    await waitFor(() => expect(dataCenterApiMock.query).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText(firstTitle)).toBeNull())
    expect(await screen.findByText(secondTitle)).toBeTruthy()
  })

  it('does not expose the previous account cache after logout', async () => {
    const firstOwner = authState.userId
    const firstTitle = `${firstOwner}@${window.location.origin}`
    const view = renderPage()
    expect(await screen.findByText(firstTitle)).toBeTruthy()

    authState.userId = ''
    view.rerender(<QueryClientProvider client={client}><DataCenterPage /></QueryClientProvider>)

    await waitFor(() => expect(screen.queryByText(firstTitle)).toBeNull())
    expect(await screen.findByText('没有符合条件的对象')).toBeTruthy()
  })
})
