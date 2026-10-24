// @vitest-environment jsdom
/*
 * 任务详情弹窗内的计时区（pim-web#29）：
 *
 * - 正在计时时显示「计时中」与累计/本轮时长，开始按钮禁用、暂停/结束可用；
 * - 开始失败（如服务端 409）时在计时区内展示错误，不伪造时长；
 * - segments/timer 任一未读回或失败时显示「暂不可用」并禁用操作，
 *   不把 0/累计值当成可靠状态，也不会因缺段放行重复开始；
 * - 结束后关闭再打开（组件重挂载）仍能读到服务端累计值；
 * - 已结束/已取消的任务保留历史累计，移除计时操作。
 *
 * 只替换 query hooks：HTTP 形状由 calendar-api.test.ts 覆盖，计时口径由
 * task-timer.test.ts 覆盖；本文件断言详情弹窗对这些数据的呈现与操作。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'

import type { TaskExecutionSegment, TaskResponse, TaskTimerState } from '../types'
import { TaskDetailDialog } from './entity-detail-dialogs'

const { startMock, pauseMock, stopMock } = vi.hoisted(() => ({
  startMock: vi.fn<(taskId: string) => Promise<unknown>>(),
  pauseMock: vi.fn<(taskId: string) => Promise<unknown>>(),
  stopMock: vi.fn<(taskId: string) => Promise<unknown>>(),
}))

let segmentsFixture: TaskExecutionSegment[] | null = []
let timerFixture: TaskTimerState | null = null
let segmentsError: Error | null = null
let timerError: Error | null = null

vi.mock('../queries', () => ({
  useTaskSegments: () => ({ data: segmentsFixture, isPending: segmentsFixture == null && segmentsError == null, isError: segmentsError != null, error: segmentsError }),
  useTaskTimer: () => ({ data: timerFixture, isPending: timerFixture == null && timerError == null, isError: timerError != null, error: timerError }),
  useTaskTimerActions: () => ({
    start: { mutateAsync: startMock, isPending: false },
    pause: { mutateAsync: pauseMock, isPending: false },
    stop: { mutateAsync: stopMock, isPending: false },
  }),
}))

afterEach(() => {
  cleanup()
  segmentsFixture = []
  timerFixture = null
  segmentsError = null
  timerError = null
  vi.clearAllMocks()
})

function task(overrides: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id: 'task-1',
    calendarId: null,
    uid: 'uid-1',
    title: '写周报',
    description: null,
    priority: 0,
    estimatedDuration: null,
    minimumSegment: null,
    dtStart: null,
    due: null,
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

function segment(overrides: Partial<TaskExecutionSegment> = {}): TaskExecutionSegment {
  return {
    id: 'seg-1',
    taskId: 'task-1',
    taskTitle: '写周报',
    startsAt: '2026-10-24T01:00:00Z',
    endsAt: null,
    status: 'active',
    source: 'timer',
    planningReason: null,
    confirmationId: null,
    ...overrides,
  }
}

function timerState(overrides: Partial<TaskTimerState> = {}): TaskTimerState {
  return {
    taskId: 'task-1',
    taskStatus: 'IN-PROCESS',
    runningSegmentId: 'seg-1',
    runningSince: '2026-10-24T01:00:00Z',
    accumulatedSeconds: 120,
    segmentCount: 1,
    ...overrides,
  }
}

function renderDialog(current: TaskResponse) {
  render(
    <TaskDetailDialog
      task={current}
      onClose={() => {}}
      onEdit={() => {}}
    />,
  )
  return screen.queryByLabelText('任务计时') ?? document.body
}

async function userEventClick(button: HTMLElement) {
  fireEvent.click(button)
  await Promise.resolve()
}

describe('任务详情计时区（pim-web#29）', () => {
  it('正在计时时显示累计与本轮时长，暂停/结束可用，开始禁用', async () => {
    segmentsFixture = [segment()]
    timerFixture = timerState({ accumulatedSeconds: 120, runningSegmentId: 'seg-1' })
    const section = renderDialog(task({ status: 'IN-PROCESS' }))

    expect(section.textContent).toContain('00:02:00')
    expect(section.textContent).toContain('本轮 00:02:00')

    const start = screen.getByRole('button', { name: '继续计时' })
    const pause = screen.getByRole('button', { name: '暂停' })
    const stop = screen.getByRole('button', { name: '结束' })
    expect(start.hasAttribute('disabled')).toBe(true)
    expect(pause.hasAttribute('disabled')).toBe(false)
    expect(stop.hasAttribute('disabled')).toBe(false)
  })

  it('开始失败时错误留在计时区内，不发明新的执行段或时长', async () => {
    startMock.mockRejectedValueOnce(new Error('fixture: 无法开始计时'))
    timerFixture = timerState({ runningSegmentId: null, taskStatus: 'NEEDS-ACTION' })
    const section = renderDialog(task())

    await userEventClick(screen.getByRole('button', { name: '开始计时' }))
    expect((await screen.findByRole('alert')).textContent).toContain('fixture: 无法开始计时')
    expect(section.textContent).toContain('00:02:00')
    expect(startMock).toHaveBeenCalledTimes(1)
    expect(segmentsFixture).toHaveLength(0)
  })

  it('重开弹窗（重新挂载）仍从服务端读回累计值，不回退到 0', () => {
    segmentsFixture = [segment({ endsAt: '2026-10-24T01:02:00Z', status: 'paused' })]
    timerFixture = timerState({ runningSegmentId: null, taskStatus: 'IN-PROCESS' })
    const section = renderDialog(task({ status: 'IN-PROCESS' }))

    expect(section.textContent).toContain('00:02:00')
    expect(section.textContent).not.toContain('本轮')
    expect(section.contains(screen.getByRole('button', { name: '继续计时' }))).toBe(true)
  })

  it('已结束的任务仍显示历史累计，但不再提供计时操作', () => {
    segmentsFixture = [segment({ endsAt: '2026-10-24T01:02:00Z', status: 'completed' })]
    timerFixture = timerState({ runningSegmentId: null, taskStatus: 'COMPLETED' })
    const section = renderDialog(task({ status: 'COMPLETED' }))

    expect(section.textContent).toContain('00:02:00')
    expect(screen.queryByRole('button', { name: '继续计时' })).toBeNull()
    expect(screen.queryByRole('button', { name: '暂停' })).toBeNull()
    expect(screen.queryByRole('button', { name: '结束' })).toBeNull()
    expect(section.textContent).toContain('已结束的任务不可再计时')
  })

  it('计时状态未读回时不把 0 当真实值，且禁用开始/暂停/结束', () => {
    segmentsFixture = []
    timerFixture = null
    const section = renderDialog(task())

    expect(section.textContent).not.toContain('00:00:00')
    expect(screen.getByRole('button', { name: '开始计时' }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '暂停' }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '结束' }).hasAttribute('disabled')).toBe(true)
  })

  it('段列表确实为空时允许开始，累计仍以 timer 响应为准', () => {
    segmentsFixture = []
    timerFixture = timerState({ runningSegmentId: null, taskStatus: 'NEEDS-ACTION' })
    const section = renderDialog(task())

    expect(screen.getByRole('button', { name: '开始计时' }).hasAttribute('disabled')).toBe(false)
    expect(section.textContent).toContain('00:02:00')
  })

  it('segments 读取失败但 timer 有效且存在开放段时，不放行重复开始也不误显累计', () => {
    segmentsFixture = null
    segmentsError = new Error('fixture: 段列表读取失败')
    timerFixture = timerState({ runningSegmentId: 'seg-9', taskStatus: 'IN-PROCESS' })
    const section = renderDialog(task({ status: 'IN-PROCESS' }))

    expect(section.textContent).toContain('暂不可用')
    expect(screen.getByRole('alert').textContent).toContain('fixture: 段列表读取失败')
    expect(section.textContent).not.toContain('00:02:00')
    expect(section.textContent).not.toContain('00:00:00')
    expect(screen.getByRole('button', { name: /计时/ }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '暂停' }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '结束' }).hasAttribute('disabled')).toBe(true)
  })

  it('timer 读取失败时明确报错，不展示旧累计或放行操作', () => {
    segmentsFixture = [segment()]
    timerFixture = timerState({ accumulatedSeconds: 120 })
    timerError = new Error('fixture: 计时状态读取失败')
    const section = renderDialog(task({ status: 'IN-PROCESS' }))

    expect(screen.getByRole('alert').textContent).toContain('fixture: 计时状态读取失败')
    expect(section.textContent).toContain('暂不可用')
    expect(section.textContent).not.toContain('00:02:00')
    expect(screen.getByRole('button', { name: /计时/ }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '暂停' }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: '结束' }).hasAttribute('disabled')).toBe(true)
  })

  it('segments 加载中时同样显示暂不可用并禁用操作', () => {
    segmentsFixture = null
    timerFixture = timerState({ runningSegmentId: null, taskStatus: 'NEEDS-ACTION' })
    const section = renderDialog(task())

    expect(section.textContent).toContain('暂不可用')
    expect(screen.getByRole('button', { name: '开始计时' }).hasAttribute('disabled')).toBe(true)
  })
})
