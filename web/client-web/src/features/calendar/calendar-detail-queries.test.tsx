// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { setApiBase } from '@/lib/apiBase'
import { useEventDetail, useTaskDetail } from './queries'

const api = vi.hoisted(() => ({
  getEvent: vi.fn(),
  getTask: vi.fn(),
}))

vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' } }),
}))
vi.mock('./api', () => ({
  calendarsApi: {},
  checklistApi: {},
  domainProjectsApi: {},
  eventsApi: { get: api.getEvent },
  layersApi: {},
  reportsApi: {},
  segmentsApi: {},
  taskBooksApi: {},
  taskTimerApi: {},
  tasksApi: { get: api.getTask },
}))

function wrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  api.getEvent.mockResolvedValue({ id: 'event-1', title: '日程' })
  api.getTask.mockResolvedValue({ id: 'task-1', title: '任务' })
})

afterEach(() => {
  cleanup()
  setApiBase('')
})

describe('calendar detail query identity', () => {
  it('does not reuse a detail response after the server address changes', async () => {
    setApiBase('https://192.0.2.10:5858')
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 60_000 } },
    })
    const event = renderHook(() => useEventDetail('event-1'), {
      wrapper: wrapper(client),
    })
    const task = renderHook(() => useTaskDetail('task-1'), {
      wrapper: wrapper(client),
    })

    await waitFor(() => expect(api.getEvent).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(api.getTask).toHaveBeenCalledTimes(1))

    setApiBase('https://198.51.100.20:5858')

    await waitFor(() => expect(api.getEvent).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(api.getTask).toHaveBeenCalledTimes(2))
    expect(client.getQueryCache().findAll({ queryKey: ['calendar', 'events', 'detail'] }))
      .toHaveLength(2)
    expect(client.getQueryCache().findAll({ queryKey: ['calendar', 'tasks', 'detail'] }))
      .toHaveLength(2)
    event.unmount()
    task.unmount()
  })
})
