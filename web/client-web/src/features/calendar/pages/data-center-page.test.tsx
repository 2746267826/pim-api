// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { NAV_GROUPS } from '@/app/nav'
import { ApiError } from '@/api/client'
import { DataCenterPage } from './data-center-page'

const queryState = vi.hoisted(() => ({
  data: undefined as unknown,
  isLoading: false,
  isFetching: false,
  isError: false,
  error: undefined as unknown,
  refetch: vi.fn(),
}))

vi.mock('../queries', () => ({
  useDataCenterQuery: () => queryState,
}))

beforeEach(() => {
  queryState.data = undefined
  queryState.isLoading = false
  queryState.isFetching = false
  queryState.isError = false
  queryState.error = undefined
  queryState.refetch.mockReset()
})

afterEach(() => cleanup())

describe('DataCenterPage', () => {
  it('is exposed in the normal navigation and renders owner-scoped objects', async () => {
    const governance = NAV_GROUPS.find((group) => group.label === '治理')
    expect(governance?.items).toContainEqual(expect.objectContaining({ to: '/data-center', label: '数据中心' }))

    queryState.data = {
      items: [{
        objectType: 'task',
        objectId: '11111111-1111-4111-8111-111111111111',
        title: '学习率实验',
        source: 'manual',
        status: 'Active',
        startsAt: '2026-11-01T10:00:00Z',
        endsAt: null,
        summary: '补充验证集实验',
      }],
      page: 1,
      pageSize: 50,
      totalCount: 1,
    }
    render(<DataCenterPage />)

    expect(screen.getByRole('heading', { name: '数据中心' })).toBeTruthy()
    expect(await screen.findByText('学习率实验')).toBeTruthy()
    expect(screen.getByText('共 1 条')).toBeTruthy()
    expect(screen.queryByText('需要管理员权限')).toBeNull()
  })

  it('distinguishes an empty result from loading and from a failed read', async () => {
    queryState.data = { items: [], page: 1, pageSize: 50, totalCount: 0 }
    const { rerender } = render(<DataCenterPage />)
    expect(screen.getByText('没有符合条件的对象')).toBeTruthy()

    queryState.data = undefined
    queryState.isLoading = true
    rerender(<DataCenterPage />)
    expect(screen.queryByText('没有符合条件的对象')).toBeNull()

    queryState.isLoading = false
    queryState.isError = true
    queryState.error = new ApiError('当前账号无权读取数据中心', 40301, 403)
    rerender(<DataCenterPage />)
    expect(screen.getByText('数据中心读取失败')).toBeTruthy()
    expect(screen.getByText(/当前账号无权读取数据中心/)).toBeTruthy()

    await userEvent.click(screen.getByRole('button', { name: '重新读取' }))
    expect(queryState.refetch).toHaveBeenCalledOnce()
  })
})
