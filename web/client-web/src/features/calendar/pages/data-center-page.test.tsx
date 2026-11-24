// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { NAV_GROUPS } from '@/app/nav'
import { ApiError } from '@/api/client'
import { DataCenterPage } from './data-center-page'

const dataCenterApiMock = vi.hoisted(() => ({
  query: vi.fn(),
  auditExport: vi.fn(),
  restorePreview: vi.fn(),
}))
const auditApiMock = vi.hoisted(() => ({
  timeline: vi.fn(),
}))

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
vi.mock('../api', () => ({ dataCenterApi: dataCenterApiMock }))
vi.mock('@/features/operations/api', () => ({ auditApi: auditApiMock }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' } }),
}))

beforeEach(() => {
  queryState.data = undefined
  queryState.isLoading = false
  queryState.isFetching = false
  queryState.isError = false
  queryState.error = undefined
  queryState.refetch.mockReset()
  dataCenterApiMock.restorePreview.mockReset()
  auditApiMock.timeline.mockReset()
  auditApiMock.timeline.mockResolvedValue({ items: [] })
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

  it('shows an audit timeline error and retries instead of treating it as empty history', async () => {
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
    auditApiMock.timeline
      .mockRejectedValueOnce(new ApiError('审计服务暂时不可用', 50301, 503))
      .mockResolvedValueOnce({
        items: [{
          id: '99999999-9999-4999-8999-999999999999',
          objectType: 'task',
          objectId: '11111111-1111-4111-8111-111111111111',
          confirmationId: null,
          source: 'manual',
          actor: '合成用户',
          beforeJson: '{}',
          afterJson: '{}',
          changedFieldsJson: '["title"]',
          createdAt: '2026-11-01T10:05:00Z',
        }],
      })
    render(<DataCenterPage />)

    await userEvent.click(await screen.findByRole('button', { name: '详情' }))
    expect(await screen.findByText('审计历史读取失败')).toBeTruthy()
    expect(screen.getByText(/审计服务暂时不可用/)).toBeTruthy()
    expect(screen.queryByText(/没有审计版本/)).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: '重新读取审计' }))
    await waitFor(() => expect(auditApiMock.timeline).toHaveBeenCalledTimes(2))
    expect(await screen.findByText('1 个版本')).toBeTruthy()
  })

  it('shows a restore-preview failure instead of an unhandled rejection', async () => {
    queryState.data = {
      items: [{
        objectType: 'audit-version',
        objectId: '88888888-8888-4888-8888-888888888888',
        title: '待恢复版本',
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
    dataCenterApiMock.restorePreview.mockRejectedValue(new ApiError('恢复预览服务不可用', 50302, 503))
    render(<DataCenterPage />)

    await userEvent.click(await screen.findByRole('button', { name: '详情' }))
    await userEvent.click(screen.getByRole('button', { name: '恢复预览' }))

    expect(await screen.findByText('恢复预览失败')).toBeTruthy()
    expect(screen.getByText(/恢复预览服务不可用/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '重新预览' })).toBeTruthy()
  })
})
