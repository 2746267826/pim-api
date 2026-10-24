// @vitest-environment jsdom
/*
 * 工单 04（E2）：日程编辑弹窗的「期间静音」三态字段。
 *
 * 契约依据（已确认语义，维护者 2026-10-14 定）：
 *   - CreateEventRequest / UpdateEventRequest 的 isMuted 为可空布尔：true 静音 / false 不静音
 *     / null（省略）未指定；PUT 是整体替换，省略即回到「未指定」。
 *   - EventResponse.isMuted 同形状回读；生效值 = isMuted ?? 所属日历本的 defaultMuted。
 *
 * 本文件钉住的产品行为（弹窗侧，不是自拟假设）：
 *   1. 三态都要能从界面选到，并且原样发出去：跟随默认 → 报文体显式 isMuted:null（不是缺键）、
 *      静音 → true、不静音 → false；
 *   2. 编辑既有日程时，静音值要回填成对应的选中态（不得把 true/false 折叠成同一个状态）；
 *   3. 选「跟随默认」时，界面必须说清当前会按哪个日历本的什么默认执行（不许静默留白）。
 *
 * A2 测试源码约束：不含 as any / as unknown as / @ts-ignore，不放宽 tsconfig。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'

import type { CalendarBook, EventResponse } from '../types'
import { EventEditorDialog } from './event-editor-dialog'

type SaveEventInput = {
  id?: string
  body: Record<string, unknown>
  scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string }
}

const { saveMock, removeMock } = vi.hoisted(() => ({
  saveMock: vi.fn<(input: SaveEventInput) => Promise<void>>(),
  removeMock: vi.fn<(input: { id: string }) => Promise<void>>(),
}))

vi.mock('../queries', () => ({
  useSaveEvent: () => ({ mutateAsync: saveMock, isPending: false }),
  useDeleteEvent: () => ({ mutateAsync: removeMock, isPending: false }),
}))

/** 该日历本默认静音 —— 「跟随默认」的生效值来源 */
const CALENDARS: CalendarBook[] = [
  {
    id: 'cal-1',
    name: '课程表',
    color: '#5b8def',
    kind: 'calendar',
    isDefault: true,
    eventCount: 0,
    source: 'local',
    outlookCalendarBindingId: null,
    canEdit: true,
    defaultMuted: true,
  },
]

const NEW_EVENT_SLOT = { start: new Date('2026-10-12T02:00:00Z'), end: new Date('2026-10-12T03:00:00Z') }

function baseEvent(overrides: Partial<EventResponse> = {}): EventResponse {
  return {
    id: 'evt-1',
    calendarId: 'cal-1',
    uid: 'uid-1',
    title: '周会',
    description: null,
    location: null,
    dtStart: '2026-10-12T02:00:00Z',
    dtEnd: '2026-10-12T03:00:00Z',
    rRule: null,
    status: 'confirmed',
    source: 'local',
    originalEventId: null,
    isAllDay: false,
    timeZoneId: null,
    sourceTimeZoneId: null,
    sourceUid: null,
    recurrenceId: null,
    descriptionFormat: null,
    showAs: null,
    importance: null,
    sensitivity: null,
    categories: null,
    isReminderOn: null,
    reminderMinutesBeforeStart: null,
    organizer: null,
    attendees: null,
    isOnlineMeeting: null,
    onlineMeetingProvider: null,
    onlineMeetingUrl: null,
    externalLink: null,
    isSeriesMaster: false,
    isException: false,
    seriesMasterId: null,
    isCancelled: false,
    outlookCalendarBindingId: null,
    outlookEventId: null,
    outlookEtag: null,
    outlookEventType: null,
    isMuted: null,
    ...overrides,
  }
}

let openChangeMock: ReturnType<typeof vi.fn<(open: boolean) => void>>

beforeEach(() => {
  saveMock.mockReset()
  saveMock.mockResolvedValue(undefined)
  removeMock.mockReset()
  openChangeMock = vi.fn<(open: boolean) => void>()
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ code: 0, message: 'ok', data: null }), { status: 200 })),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

/** 排空 submit 的异步链（zodResolver 校验是 Promise） */
async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0))
  await new Promise((r) => setTimeout(r, 0))
}

function openMoreSettings(): void {
  // 编辑态若有静音/提醒等更多设置，面板是自动展开的（按钮文案变成「收起更多设置」）
  const expand = screen.queryByRole('button', { name: /提醒、闲忙、会议、参会人/ })
  if (expand) fireEvent.click(expand)
}

function clickSave(): void {
  fireEvent.click(screen.getByRole('button', { name: '保存' }))
}

/** 三态分段控件：按 title 定位（文案会出现在提示里，按 title 更精确） */
function muteOption(title: string): HTMLElement {
  return screen.getByTitle(title)
}

/**
 * 分段控件的选中判定。
 *
 * Radix ToggleGroup（type=single）在选中项上给出 aria-checked="true"；本仓的 Segmented
 * 还把容器 role 改成 tablist，不同版本会在 aria-checked / aria-selected / data-state 之间取其一。
 * 三种信号都认，避免把「选中信号换了个属性」误判成「没选中」。
 */
function isMutedOptionSelected(title: string): boolean {
  const el = muteOption(title)
  return (
    el.getAttribute('aria-checked') === 'true' ||
    el.getAttribute('aria-selected') === 'true' ||
    el.getAttribute('data-state') === 'on'
  )
}

const MUTE_ON = '该日程期间静音'
const MUTE_OFF = '该日程期间不改变静音状态'
const MUTE_FOLLOW = '未指定：生效值取所属日历本的默认静音设定'

function renderNewDialog() {
  return render(
    <EventEditorDialog open onOpenChange={openChangeMock} calendars={CALENDARS} initial={NEW_EVENT_SLOT} event={null} />,
  )
}

function renderEditDialog(event: EventResponse) {
  return render(<EventEditorDialog open onOpenChange={openChangeMock} calendars={CALENDARS} event={event} />)
}

describe('日程编辑弹窗：期间静音三态', () => {
  it('新建时选「静音」：报文体带 isMuted: true', async () => {
    renderNewDialog()
    fireEvent.change(screen.getByLabelText(/标题/), { target: { value: '周会' } })
    openMoreSettings()
    fireEvent.click(muteOption(MUTE_ON))
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    expect(saveMock.mock.calls[0][0].body.isMuted).toBe(true)
  })

  it('新建时保持「跟随默认」：报文体显式发 isMuted: null（未指定，不是缺键）', async () => {
    renderNewDialog()
    fireEvent.change(screen.getByLabelText(/标题/), { target: { value: '周会' } })
    openMoreSettings()
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect('isMuted' in body).toBe(true)
    expect(body.isMuted).toBeNull()
  })

  it('编辑 isMuted=false 的日程：回填为「不静音」，保存后原样发 false', async () => {
    renderEditDialog(baseEvent({ isMuted: false }))
    openMoreSettings()
    // 断言选中态：分段控件的选中项带 aria-checked/aria-selected 之一
    expect(isMutedOptionSelected(MUTE_OFF)).toBe(true)
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    expect(saveMock.mock.calls[0][0].body.isMuted).toBe(false)
  })

  it('编辑 isMuted=true 的日程：回填为「静音」并原样发 true', async () => {
    renderEditDialog(baseEvent({ isMuted: true }))
    openMoreSettings()
    expect(isMutedOptionSelected(MUTE_ON)).toBe(true)
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    expect(saveMock.mock.calls[0][0].body.isMuted).toBe(true)
  })

  it('「跟随默认」时提示说明按哪个日历本的什么默认执行', () => {
    renderEditDialog(baseEvent({ isMuted: null }))
    openMoreSettings()

    expect(screen.getByTestId('event-mute-hint').textContent).toContain('课程表')
    expect(screen.getByTestId('event-mute-hint').textContent).toContain('静音')
    // 「跟随默认」是选中态：三态里未指定必须可辨识，而不是与某个具体值混同
    expect(isMutedOptionSelected(MUTE_FOLLOW)).toBe(true)
  })
})
