// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import type { CalendarBook, EventResponse } from '../types'
import { EventEditorDialog } from './event-editor-dialog'

// Radix Select 在 jsdom 里不会响应 fireEvent.change；这里只替换 Select 的呈现层，
// 让测试仍走组件真实的 fold 状态与提交逻辑。
vi.mock('@/components/ui/select', () => ({
  Select: ({
    id,
    value,
    onValueChange,
    options,
    placeholder,
  }: {
    id?: string
    value?: string
    onValueChange: (value: string) => void
    options: Array<{ value: string; label: string }>
    placeholder?: string
  }) => (
    <select id={id} value={value ?? ''} onChange={(event) => onValueChange(event.target.value)}>
      {value === '' && placeholder ? <option value="">{placeholder}</option> : null}
      {options.map((option) => (
        <option key={option.value} value={option.value}>{option.label}</option>
      ))}
    </select>
  ),
}))

type SaveEventInput = {
  id?: string
  body: Record<string, unknown>
}

const { saveMock, removeMock } = vi.hoisted(() => ({
  saveMock: vi.fn<(input: SaveEventInput) => Promise<void>>(),
  removeMock: vi.fn<(input: { id: string }) => Promise<void>>(),
}))

vi.mock('../queries', () => ({
  useSaveEvent: () => ({ mutateAsync: saveMock, isPending: false }),
  useDeleteEvent: () => ({ mutateAsync: removeMock, isPending: false }),
}))

const CALENDARS: CalendarBook[] = [{
  id: 'cal-1',
  name: '默认日历',
  color: '#5b8def',
  kind: 'calendar',
  isDefault: true,
  eventCount: 0,
  source: 'local',
  outlookCalendarBindingId: null,
  canEdit: true,
  defaultMuted: false,
}]

function existingEvent(overrides: Partial<EventResponse> = {}): EventResponse {
  return {
    id: 'event-1',
    calendarId: 'cal-1',
    uid: 'uid-1',
    title: '原標題',
    description: null,
    location: null,
    dtStart: '2026-11-01T05:30:00+00:00',
    dtEnd: '2026-11-01T07:30:00+00:00',
    rRule: null,
    status: 'CONFIRMED',
    source: 'local',
    originalEventId: null,
    isAllDay: false,
    timeZoneId: 'America/New_York',
    sourceTimeZoneId: 'Eastern Standard Time',
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
    timeZoneResolution: 'resolved',
    localStart: '2026-11-01T01:30:00',
    localEnd: '2026-11-01T02:30:00',
    displayTimeZoneId: 'Asia/Shanghai',
    displayLocalStart: '2026-11-01T13:30:00',
    displayLocalEnd: '2026-11-01T14:30:00',
    ...overrides,
  }
}

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0))
  await new Promise((resolve) => setTimeout(resolve, 0))
}

function input(label: string): HTMLInputElement {
  return screen.getByLabelText<HTMLInputElement>(label)
}

function save(): void {
  fireEvent.click(screen.getByRole('button', { name: '保存' }))
}

beforeEach(() => {
  saveMock.mockReset()
  removeMock.mockReset()
  saveMock.mockResolvedValue()
})

afterEach(() => cleanup())

describe('日程编辑器时区', () => {
  it('只改标题时保留原始绝对时刻，并省略未变更的时区字段', async () => {
    render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} event={existingEvent()} />)
    fireEvent.change(input('标题 *'), { target: { value: '更新标题' } })
    save()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.dtStart).toBe('2026-11-01T05:30:00+00:00')
    expect(body.dtEnd).toBe('2026-11-01T07:30:00+00:00')
    expect(body).not.toHaveProperty('timeZoneId')
  })

  it('切换事件时区只改变墙钟输入，不改变绝对时刻', async () => {
    const initial = {
      start: new Date('2026-11-16T01:00:00.000Z'),
      end: new Date('2026-11-16T02:00:00.000Z'),
    }
    render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} initial={initial} />)
    fireEvent.change(input('事件时区（IANA）'), { target: { value: 'America/New_York' } })
    expect(input('开始（America/New_York）*').value).toBe('2026-11-15T20:00')
    expect(input('结束（America/New_York）*').value).toBe('2026-11-15T21:00')
    fireEvent.change(input('标题 *'), { target: { value: '纽约会议' } })
    save()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    expect(saveMock.mock.calls[0][0].body.dtStart).toBe('2026-11-16T01:00:00.000Z')
    expect(saveMock.mock.calls[0][0].body.dtEnd).toBe('2026-11-16T02:00:00.000Z')
    expect(saveMock.mock.calls[0][0].body.timeZoneId).toBe('America/New_York')
  })

  it('拒绝夏令时缺口和未明确选择的回拨时刻', async () => {
    const gapSlot = {
      start: new Date('2026-03-08T06:00:00.000Z'),
      end: new Date('2026-03-08T08:00:00.000Z'),
    }
    const gapRender = render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} initial={gapSlot} />)
    fireEvent.change(input('事件时区（IANA）'), { target: { value: 'America/New_York' } })
    fireEvent.change(input('开始（America/New_York）*'), { target: { value: '2026-03-08T02:30' } })
    fireEvent.change(input('标题 *'), { target: { value: 'DST gap' } })
    save()
    await settle()
    expect(saveMock).not.toHaveBeenCalled()
    expect(screen.getByText('所选时间处于夏令时跳时缺口，请改选一个有效时刻。')).toBeTruthy()

    gapRender.unmount()
    const foldSlot = {
      start: new Date('2026-11-01T00:00:00.000Z'),
      end: new Date('2026-11-01T07:30:00.000Z'),
    }
    render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} initial={foldSlot} />)
    fireEvent.change(input('事件时区（IANA）'), { target: { value: 'America/New_York' } })
    fireEvent.change(input('开始（America/New_York）*'), { target: { value: '2026-11-01T01:30' } })
    fireEvent.change(input('标题 *'), { target: { value: 'DST fold' } })
    save()
    await settle()
    expect(saveMock).not.toHaveBeenCalled()
    expect(screen.getByText('请选择回拨时段中实际发生的开始和结束时刻。')).toBeTruthy()
  })

  it('回拨时段选择第二个 fold 时提交对应的绝对时刻', async () => {
    render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} event={existingEvent()} />)

    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('开始时刻'), {
      target: { value: '2026-11-01T06:30:00.000Z' },
    })
    save()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.dtStart).toBe('2026-11-01T06:30:00.000Z')
    expect(body.dtEnd).toBe('2026-11-01T07:30:00.000Z')
  })

  it('不从绝对时刻推断全天日期，并拒绝非法日期', async () => {
    render(<EventEditorDialog open onOpenChange={vi.fn()} calendars={CALENDARS} initial={{
      start: new Date('2026-11-16T01:00:00.000Z'),
      end: new Date('2026-11-16T02:00:00.000Z'),
    }} />)
    fireEvent.change(input('标题 *'), { target: { value: '全天' } })
    fireEvent.click(screen.getByRole('switch', { name: '全天' }))

    expect(input('开始日期 *').value).toBe('')
    expect(input('结束日期（含）*').value).toBe('')
    fireEvent.change(input('开始日期 *'), { target: { value: '2026-02-31' } })
    fireEvent.change(input('结束日期（含）*'), { target: { value: '2026-03-01' } })
    save()
    await settle()

    expect(saveMock).not.toHaveBeenCalled()
    expect(screen.getByText('请选择全天开始日期和结束日期。')).toBeTruthy()
  })

  it('UpdatingLegacyAllDayMetadata_PreservesAbsoluteBoundsWithoutInventingDates', async () => {
    const dtStart = '2026-07-12T00:00:00+00:00'
    const dtEnd = '2026-07-13T00:00:00+00:00'
    render(<EventEditorDialog
      open
      onOpenChange={vi.fn()}
      calendars={CALENDARS}
      event={existingEvent({
        title: '旧全天事件',
        isAllDay: true,
        allDayStartDate: null,
        allDayEndDateExclusive: null,
        dtStart,
        dtEnd,
        timeZoneId: 'Asia/Shanghai',
        sourceTimeZoneId: 'China Standard Time',
        localStart: null,
        localEnd: null,
      })}
    />)

    fireEvent.change(input('标题 *'), { target: { value: '只改标题' } })
    save()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.dtStart).toBe(dtStart)
    expect(body.dtEnd).toBe(dtEnd)
    expect(body.allDayStartDate).toBeNull()
    expect(body.allDayEndDateExclusive).toBeNull()
    expect(screen.queryByText('请选择全天开始日期和结束日期。')).toBeNull()
  })
})
