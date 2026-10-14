// @vitest-environment jsdom
/*
 * 工单 04（E2 后半）：日历本「静音设置」。
 *
 * 契约依据：`PUT /api/v1/calendar/calendars/{id}/mute-policy`，body `{isMuted:boolean}`，
 * 返回 `{calendarId, defaultMuted, affectedEventCount}`；`CalendarResponse.defaultMuted` 是该本当前默认。
 *
 * 本文件钉住的产品行为（AC-6 的界面侧）：
 *   1. 日历本设置里能**看到当前状态**（默认静音 or 不静音），不是只有一个开关；
 *   2. 能**批量设定**该本下日程统一为开或关，并按调用的返回值给出可见反馈（含受影响条数）；
 *   3. 只发一次批量请求，且 body 形状是 {isMuted:boolean}（不臆造字段）。
 *
 * A2 测试源码约束：不含 as any / as unknown as / @ts-ignore，不放宽 tsconfig。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'

import type { CalendarBook } from '../types'
import { CalendarVisibilityProvider } from '../calendar-visibility'
import { CalendarBooksManager } from './calendar-books-manager'

const BOOKS: CalendarBook[] = [
  {
    id: 'cal-1',
    name: '课程表',
    color: '#5b8def',
    kind: 'calendar',
    isDefault: true,
    eventCount: 3,
    source: 'outlook',
    outlookCalendarBindingId: null,
    canEdit: true,
    defaultMuted: true,
  },
]

const { setMutePolicyMock, notifySuccessMock, mutateAsyncMock } = vi.hoisted(() => ({
  setMutePolicyMock: vi.fn(),
  notifySuccessMock: vi.fn(),
  mutateAsyncMock: vi.fn(async () => ({ calendarId: 'cal-1', defaultMuted: false, affectedEventCount: 7 })),
}))

vi.mock('../queries', () => ({
  useCalendars: () => ({ data: [{ ...BOOKS[0] }] }),
  useCreateCalendar: () => ({ mutate: vi.fn(), isPending: false }),
  useRenameCalendar: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteCalendar: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useSetCalendarMutePolicy: () => {
    setMutePolicyMock()
    return { mutateAsync: mutateAsyncMock, isPending: false }
  },
}))

vi.mock('@/lib/notify', () => ({
  notifySuccess: notifySuccessMock,
  notifyError: vi.fn(),
}))

/**
 * Radix（DropdownMenu / Dialog）在 jsdom 里需要这组能力：
 * jsdom 没有 PointerEvent 的捕获 API，也没有 ResizeObserver —— 不补齐时菜单根本打不开，
 * 会被误判成「功能没接线」。
 */
beforeEach(() => {
  Element.prototype.hasPointerCapture = () => false
  Element.prototype.setPointerCapture = () => {}
  Element.prototype.releasePointerCapture = () => {}
  Element.prototype.scrollIntoView = () => {}
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  )
  setMutePolicyMock.mockClear()
  notifySuccessMock.mockClear()
  mutateAsyncMock.mockClear()
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ code: 0, message: 'ok', data: null }), { status: 200 })),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function renderManager() {
  return render(
    <MemoryRouter>
      <CalendarVisibilityProvider>
        <CalendarBooksManager />
      </CalendarVisibilityProvider>
    </MemoryRouter>,
  )
}

/** 排空批量设定后的异步链（成功后要弹提示，断言前必须让 promise 落地） */
async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0))
  await new Promise((r) => setTimeout(r, 0))
}

/** 打开某日历本的「静音设置…」弹窗（入口在日历本行的更多操作菜单里） */
async function openMuteDialog(): Promise<void> {
  const trigger = screen.getByRole('button', { name: '课程表 更多操作' })
  fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false })
  fireEvent.click(trigger)
  const item = await screen.findByRole('menuitem', { name: '静音设置…' })
  fireEvent.click(item)
}

describe('日历本静音设置（批量）', () => {
  it('弹窗显示当前默认状态，并把「统一设为不静音」发成一次 {isMuted:false} 调用', async () => {
    renderManager()
    await openMuteDialog()

    // 当前状态可见（该本 defaultMuted=true）
    expect(screen.getByTestId('calendar-mute-current').textContent).toContain('静音')

    fireEvent.click(screen.getByTestId('calendar-mute-off'))
    await settle()

    expect(mutateAsyncMock).toHaveBeenCalledTimes(1)
    expect(mutateAsyncMock).toHaveBeenCalledWith({ id: 'cal-1', isMuted: false })
    // 结果可见：新状态 + 受影响条数（affectedEventCount=7）
    expect(notifySuccessMock).toHaveBeenCalledTimes(1)
    const [title, description] = notifySuccessMock.mock.calls[0] as string[]
    expect(title).toContain('不静音')
    expect(description).toContain('7')
  })

  it('「统一设为静音」发 {isMuted:true}', async () => {
    renderManager()
    await openMuteDialog()

    fireEvent.click(screen.getByTestId('calendar-mute-on'))

    expect(mutateAsyncMock).toHaveBeenCalledWith({ id: 'cal-1', isMuted: true })
  })
})
