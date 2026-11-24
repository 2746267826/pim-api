// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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
  dataCenterApiMock.auditExport.mockReset()
  dataCenterApiMock.restorePreview.mockReset()
  auditApiMock.timeline.mockReset()
  auditApiMock.timeline.mockResolvedValue({ items: [] })
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: vi.fn(() => 'blob:synthetic-audit') },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  })
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

  it('does not download a late audit export after the server changes', async () => {
    let resolveExport!: (value: { fileName: string; contentType: string; content: string }) => void
    dataCenterApiMock.auditExport.mockImplementationOnce(() => new Promise((resolve) => {
      resolveExport = resolve
    }))
    const createUrl = vi.mocked(URL.createObjectURL)
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    renderPage()
    expect(await screen.findByText(`${authState.userId}@${window.location.origin}`)).toBeTruthy()

    await userEvent.click(screen.getByRole('button', { name: /导出审计/ }))
    await waitFor(() => expect(dataCenterApiMock.auditExport).toHaveBeenCalledOnce())
    const signal = dataCenterApiMock.auditExport.mock.calls[0]?.[1]?.signal as AbortSignal

    setApiBase('https://server-b.synthetic.invalid')
    await waitFor(() => expect(signal.aborted).toBe(true))
    resolveExport({ fileName: 'old-audit.json', contentType: 'application/json', content: '{"owner":"old"}' })
    await Promise.resolve()
    await Promise.resolve()

    expect(createUrl).not.toHaveBeenCalled()
    expect(anchorClick).not.toHaveBeenCalled()
    expect(screen.queryByText('审计导出已下载')).toBeNull()
  })

  it('clears an open detail and ignores a late audit timeline response after the account changes', async () => {
    let resolveTimeline!: (value: { items: Array<Record<string, unknown>> }) => void
    auditApiMock.timeline.mockImplementationOnce(() => new Promise((resolve) => {
      resolveTimeline = resolve
    }))
    const view = renderPage()
    expect(await screen.findByText(`${authState.userId}@${window.location.origin}`)).toBeTruthy()

    await userEvent.click(screen.getByRole('button', { name: '详情' }))
    await waitFor(() => expect(auditApiMock.timeline).toHaveBeenCalledOnce())
    const signal = auditApiMock.timeline.mock.calls[0]?.[2]?.signal as AbortSignal
    expect(screen.getByText('对象详情')).toBeTruthy()

    authState.userId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    view.rerender(<QueryClientProvider client={client}><DataCenterPage /></QueryClientProvider>)
    await waitFor(() => expect(signal.aborted).toBe(true))
    resolveTimeline({
      items: [{
        id: '99999999-9999-4999-8999-999999999999',
        objectType: 'task',
        objectId: '11111111-1111-4111-8111-111111111111',
        confirmationId: null,
        source: 'manual',
        actor: '旧账号审计',
        beforeJson: '{}',
        afterJson: '{}',
        changedFieldsJson: '["title"]',
        createdAt: '2026-11-01T10:05:00Z',
      }],
    })
    await Promise.resolve()
    await Promise.resolve()

    expect(screen.queryByText('旧账号审计')).toBeNull()
    await waitFor(() => expect(screen.queryByText('对象详情')).toBeNull())
    expect(await screen.findByText(`${authState.userId}@${window.location.origin}`)).toBeTruthy()
  })

  it('does not show a late restore preview after the server changes', async () => {
    dataCenterApiMock.query.mockImplementation(async () => {
      const owner = authState.userId || 'logged-out'
      const server = getApiBase() || window.location.origin
      return {
        items: [{
          objectType: 'audit-version',
          objectId: '88888888-8888-4888-8888-888888888888',
          title: `待恢复版本@${owner}@${server}`,
          source: 'manual',
          status: 'Active',
          startsAt: '2026-11-01T10:00:00Z',
          endsAt: null,
          summary: '',
        }],
        page: 1,
        pageSize: 50,
        totalCount: 1,
      }
    })
    let resolvePreview!: (value: {
      summary: string
      changedFields: string[]
      beforeJson: string | null
      afterJson: string | null
    }) => void
    dataCenterApiMock.restorePreview.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePreview = resolve
    }))
    renderPage()
    expect(await screen.findByText(`待恢复版本@${authState.userId}@${window.location.origin}`)).toBeTruthy()

    await userEvent.click(screen.getByRole('button', { name: '详情' }))
    await userEvent.click(await screen.findByRole('button', { name: '恢复预览' }))
    await waitFor(() => expect(dataCenterApiMock.restorePreview).toHaveBeenCalledOnce())
    const signal = dataCenterApiMock.restorePreview.mock.calls[0]?.[1]?.signal as AbortSignal

    setApiBase('https://server-b.synthetic.invalid')
    await waitFor(() => expect(signal.aborted).toBe(true))
    resolvePreview({
      summary: '旧服务器恢复预览',
      changedFields: ['title'],
      beforeJson: '{}',
      afterJson: '{}',
    })
    await Promise.resolve()
    await Promise.resolve()

    expect(screen.queryByText('旧服务器恢复预览')).toBeNull()
    await waitFor(() => expect(screen.queryByText('对象详情')).toBeNull())
    expect(await screen.findByText(`待恢复版本@${authState.userId}@https://server-b.synthetic.invalid`)).toBeTruthy()
  })
})
