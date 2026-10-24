// @vitest-environment jsdom
/*
 * api#465 前端配套：完成切换只发真正要改的字段。
 * 用旧快照点击完成/重开时，PUT body 必须只含 status/percentComplete，
 * 不把缓存外的 title/description/日期等覆盖回旧值。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'

const { updateMock } = vi.hoisted(() => ({ updateMock: vi.fn() }))

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  tasksApi: { update: updateMock },
}))

import { useToggleTaskComplete } from './queries'
import type { TaskResponse } from './types'

function task(overrides: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id: 'task-1',
    calendarId: null,
    uid: 'uid-1',
    title: '旧标题',
    description: '旧描述（别人已在别处改成新描述）',
    priority: 2,
    estimatedDuration: 'PT30M',
    minimumSegment: 'PT10M',
    dtStart: '2026-11-01T09:00:00Z',
    due: '2026-11-02T08:00:00Z',
    status: 'NEEDS-ACTION',
    isInbox: false,
    sortOrder: 0,
    subTasks: [],
    plannedEnd: null,
    taskBookId: null,
    percentComplete: 0,
    ...overrides,
  }
}

describe('useToggleTaskComplete 只发送 partial body', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  function renderMutation() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    )
    return renderHook(() => useToggleTaskComplete(), { wrapper })
  }

  it('点击完成时只发 status/percentComplete，任务 id 正确', async () => {
    updateMock.mockResolvedValue(task({ status: 'COMPLETED', percentComplete: 100 }))
    const stale = task()
    const { result } = renderMutation()

    await result.current.mutateAsync({ task: stale, completed: true })

    expect(updateMock).toHaveBeenCalledTimes(1)
    expect(updateMock).toHaveBeenCalledWith('task-1', { status: 'COMPLETED', percentComplete: 100 })
    const body = updateMock.mock.calls[0]![1] as Record<string, unknown>
    expect(Object.keys(body).sort()).toEqual(['percentComplete', 'status'])
    expect(body).not.toHaveProperty('title')
    expect(body).not.toHaveProperty('description')
    expect(body).not.toHaveProperty('due')
    expect(body).not.toHaveProperty('dtStart')
    expect(body).not.toHaveProperty('priority')
  })

  it('重开时 status/percentComplete 回到待办', async () => {
    updateMock.mockResolvedValue(task())
    const stale = task({ status: 'COMPLETED', percentComplete: 100 })
    const { result } = renderMutation()

    await result.current.mutateAsync({ task: stale, completed: false })

    expect(updateMock).toHaveBeenCalledWith('task-1', { status: 'NEEDS-ACTION', percentComplete: 0 })
    await waitFor(() => expect(result.current.isSuccess).toBe(true))
  })
})
