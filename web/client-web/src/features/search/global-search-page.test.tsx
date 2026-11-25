// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { ApiError } from '@/api/client'
import { setApiBase } from '@/lib/apiBase'
import type {
  GlobalSearchResult,
  SearchGroup,
  SearchResult,
} from './types'
import { GlobalSearchPage } from './pages/global-search-page'

const searchApiMock = vi.hoisted(() => ({
  query: vi.fn(),
}))

vi.mock('./api', () => ({ searchApi: searchApiMock }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' } }),
}))

const NOTE_ID = '11111111-1111-4111-8111-111111111111'
const TASK_ID = '22222222-2222-4222-8222-222222222222'
const EVENT_ID = '33333333-3333-4333-8333-333333333333'

function result(
  domain: SearchResult['domain'],
  id: string,
  title: string,
  url: string,
): SearchResult {
  return {
    moduleName: domain === 'note' ? 'notes' : 'calendar',
    type: domain,
    id,
    title,
    snippet: `${title} 的摘要`,
    url,
    domain,
    score: 90,
    matchedFields: ['标题'],
    fieldLocations: [{ field: 'title', label: '标题' }],
    sourceState: 'server',
    updatedAt: '2026-11-25T08:00:00Z',
    createdAt: '2026-11-20T08:00:00Z',
    sortKey: `${domain}|${id}`,
  }
}

function group(
  domain: SearchGroup['domain'],
  label: string,
  items: SearchResult[],
  totalCount = items.length,
): SearchGroup {
  return { domain, label, totalCount, items }
}

function response(groups: SearchGroup[]): GlobalSearchResult {
  const items = groups.flatMap((item) => item.items)
  return {
    items,
    page: 1,
    pageSize: 20,
    totalCount: groups.reduce((sum, item) => sum + item.totalCount, 0),
    totalPages: 1,
    query: '研究',
    scope: 'all',
    match: 'all',
    sort: 'relevance',
    groups,
    generatedAt: '2026-11-25T09:00:00Z',
  }
}

function page(entry = '/search?q=%E7%A0%94%E7%A9%B6') {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/search" element={<GlobalSearchPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.resetAllMocks()
  setApiBase('http://192.0.2.10:5858')
})

afterEach(() => {
  cleanup()
  setApiBase('')
})

describe('GlobalSearchPage', () => {
  it('renders grouped task, event and note hits and links to the exact objects', async () => {
    searchApiMock.query.mockResolvedValue(response([
      group('note', '笔记', [result('note', NOTE_ID, '研究笔记', `/native-notes?note=${NOTE_ID}`)]),
      group('task', '任务', [result('task', TASK_ID, '研究任务', `/tasks?task=${TASK_ID}`)]),
      group('event', '日程', [result('event', EVENT_ID, '研究日程', `/calendar?event=${EVENT_ID}`)]),
    ]))

    page('/search?q=%E7%A0%94%E7%A9%B6&match=any&sort=recent_edited&page=2')

    expect(await screen.findByText('研究笔记')).toBeTruthy()
    expect(screen.getByText('研究任务')).toBeTruthy()
    expect(screen.getByText('研究日程')).toBeTruthy()
    expect(screen.getByRole('heading', { name: '笔记' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: '任务' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: '日程' })).toBeTruthy()

    expect(screen.getByRole('link', { name: /研究笔记/ }).getAttribute('href'))
      .toBe(`/native-notes?note=${NOTE_ID}`)
    expect(screen.getByRole('link', { name: /研究任务/ }).getAttribute('href'))
      .toBe(`/tasks?task=${TASK_ID}`)
    expect(screen.getByRole('link', { name: /研究日程/ }).getAttribute('href'))
      .toBe(`/calendar?event=${EVENT_ID}`)

    expect(searchApiMock.query).toHaveBeenCalledWith(
      {
        q: '研究',
        scope: 'all',
        match: 'any',
        sort: 'recent_edited',
        page: 2,
        pageSize: 20,
      },
      expect.anything(),
    )
  })

  it('keeps empty input from querying and distinguishes a true no-match result', async () => {
    page('/search')
    expect(screen.getByText('输入关键词开始搜索')).toBeTruthy()
    expect(searchApiMock.query).not.toHaveBeenCalled()

    cleanup()
    searchApiMock.query.mockResolvedValue(response([
      group('note', '笔记', []),
      group('task', '任务', []),
      group('event', '日程', []),
    ]))
    page()
    expect(await screen.findByText('没有找到匹配内容')).toBeTruthy()
  })

  it('keeps healthy domains visible when one domain fails and retries only that domain', async () => {
    const failed = response([
      group('note', '笔记', [result('note', NOTE_ID, '研究笔记', `/native-notes?note=${NOTE_ID}`)]),
      group('task', '任务', []),
      {
        domain: 'event',
        label: '日程',
        totalCount: 0,
        items: [],
        failure: {
          code: '50301',
          message: '日程服务暂时不可用',
          correlationId: 'corr-q08',
          retryable: true,
        },
      },
    ])
    const recovered = response([
      group('note', '笔记', []),
      group('task', '任务', []),
      group('event', '日程', [result('event', EVENT_ID, '研究组会', `/calendar?event=${EVENT_ID}`)]),
    ])
    searchApiMock.query
      .mockResolvedValueOnce(failed)
      .mockResolvedValueOnce(recovered)

    page()

    expect(await screen.findByText('研究笔记')).toBeTruthy()
    expect(screen.getByText('日程服务暂时不可用')).toBeTruthy()
    expect(screen.getByText(/corr-q08/)).toBeTruthy()
    await userEvent.click(screen.getByRole('button', { name: /重试该领域/ }))

    expect(await screen.findByText('研究组会')).toBeTruthy()
    expect(screen.getByText('研究笔记')).toBeTruthy()
    await waitFor(() => expect(searchApiMock.query).toHaveBeenCalledTimes(2))
    expect(searchApiMock.query.mock.calls[1]![0]).toMatchObject({
      q: '研究',
      scope: 'all',
      page: 1,
      pageSize: 20,
    })
  })

  it('pages through the aggregated result count and sends the URL page to the API', async () => {
    searchApiMock.query.mockResolvedValue({
      ...response([
        group('note', '笔记', [result('note', NOTE_ID, '研究笔记', `/native-notes?note=${NOTE_ID}`)], 45),
        group('task', '任务', []),
        group('event', '日程', []),
      ]),
      totalCount: 45,
      totalPages: 3,
    })

    page('/search?q=%E7%A0%94%E7%A9%B6')

    expect(await screen.findByText('研究笔记')).toBeTruthy()
    expect(screen.getByText('第 1 / 3 页')).toBeTruthy()
    await userEvent.click(screen.getByRole('button', { name: '下一页' }))

    await waitFor(() => expect(searchApiMock.query).toHaveBeenLastCalledWith(
      expect.objectContaining({ q: '研究', page: 2, pageSize: 20 }),
      expect.anything(),
    ))
  })

  it('shows the server correlation id when the aggregate request fails', async () => {
    searchApiMock.query.mockRejectedValue(
      new ApiError('搜索服务暂时不可用', 50001, 503, null, null, 'corr-global-q08'),
    )

    page()

    expect(await screen.findByText('搜索服务暂时不可用')).toBeTruthy()
    expect(screen.getByText(/错误码 50001/)).toBeTruthy()
    expect(screen.getByText(/corr-global-q08/)).toBeTruthy()
    expect(screen.getByRole('button', { name: /重试查询/ })).toBeTruthy()
  })

  it('hides the domain retry action when the failure is not retryable', async () => {
    searchApiMock.query.mockResolvedValue(response([
      group('note', '笔记', []),
      group('task', '任务', []),
      {
        domain: 'event',
        label: '日程',
        totalCount: 0,
        items: [],
        failure: {
          code: '40001',
          message: '日程搜索参数无效',
          correlationId: 'corr-no-retry',
          retryable: false,
        },
      },
    ]))

    page()

    expect(await screen.findByText('日程搜索参数无效')).toBeTruthy()
    expect(screen.queryByRole('button', { name: /重试该领域/ })).toBeNull()
  })

  it('aborts an in-flight domain retry when the server binding changes', async () => {
    let resolveRetry!: (value: GlobalSearchResult) => void
    let resolveNewSearch!: (value: GlobalSearchResult) => void
    const failed = response([
      group('note', '笔记', [result('note', NOTE_ID, '研究笔记', `/native-notes?note=${NOTE_ID}`)]),
      group('task', '任务', []),
      {
        domain: 'event',
        label: '日程',
        totalCount: 0,
        items: [],
        failure: {
          code: '50301',
          message: '日程服务暂时不可用',
          correlationId: 'corr-stale',
          retryable: true,
        },
      },
    ])
    const recovered = response([
      group('note', '笔记', []),
      group('task', '任务', []),
      group('event', '日程', [result('event', EVENT_ID, '研究组会', `/calendar?event=${EVENT_ID}`)]),
    ])
    const empty = response([
      group('note', '笔记', []),
      group('task', '任务', []),
      group('event', '日程', []),
    ])
    searchApiMock.query
      .mockResolvedValueOnce(failed)
      .mockImplementationOnce(() => new Promise((resolve) => {
        resolveRetry = resolve
      }))
      .mockImplementationOnce(() => new Promise((resolve) => {
        resolveNewSearch = resolve
      }))

    page()
    expect(await screen.findByText('研究笔记')).toBeTruthy()
    await userEvent.click(screen.getByRole('button', { name: /重试该领域/ }))
    await waitFor(() => expect(searchApiMock.query).toHaveBeenCalledTimes(2))
    const retrySignal = searchApiMock.query.mock.calls[1]![1]!.signal as AbortSignal
    expect(retrySignal.aborted).toBe(false)

    act(() => setApiBase('http://198.51.100.20:5858'))
    await waitFor(() => expect(searchApiMock.query).toHaveBeenCalledTimes(3))
    await waitFor(() => expect(retrySignal.aborted).toBe(true))

    await act(async () => resolveRetry(recovered))
    await act(async () => resolveNewSearch(empty))

    expect(await screen.findByText('没有找到匹配内容')).toBeTruthy()
    expect(screen.queryByText('研究组会')).toBeNull()
  })
})
