import { describe, expect, it } from 'vitest'
import { buildEventListSections, wallClockTime, dayLabels } from './event-list-model'
import type { EventResponse, LayerItem } from '../types'

/* 后端时间戳形态：7 位小数 + 偏移（与真实 API 一致） */
const ev = (over: Partial<EventResponse>): EventResponse =>
  ({
    id: 'e1',
    calendarId: 'cal-1',
    uid: 'u1',
    title: '课',
    description: null,
    location: null,
    dtStart: '2026-09-28T02:00:00.0000000+00:00',
    dtEnd: '2026-09-28T03:40:00.0000000+00:00',
    rrule: null,
    status: 'Confirmed',
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
    ...over,
  }) as EventResponse

const layer = (over: Partial<LayerItem>): LayerItem =>
  ({
    id: 'l1',
    layer: 'task-segments',
    objectType: 'task',
    objectId: 't1',
    title: '任务段',
    startsAt: '2026-09-28T06:00:00.0000000+00:00',
    endsAt: '2026-09-28T07:00:00.0000000+00:00',
    source: 'local',
    status: 'Planned',
    color: '#22C55E',
    requiresConfirmation: false,
    ...over,
  }) as LayerItem

describe('wallClockTime（+08:00 墙钟，不依赖浏览器时区）', () => {
  it('UTC 02:00 → 本地 10:00', () => {
    expect(wallClockTime('2026-09-28T02:00:00+00:00')).toBe('10:00')
  })
  it('跨日取模正确（UTC 17:30 → 次日 01:30 的 HH:mm）', () => {
    expect(wallClockTime('2026-09-28T17:30:00+00:00')).toBe('01:30')
  })
  it('空/坏值返回空串', () => {
    expect(wallClockTime(null)).toBe('')
    expect(wallClockTime('bad')).toBe('')
  })
})

describe('dayLabels（Intl UTC，中文星期与日期）', () => {
  it('2026-09-28 是星期一', () => {
    expect(dayLabels('2026-09-28')).toEqual({ weekdayLabel: '星期一', dateLabel: '2026年9月28日' })
  })
})

describe('buildEventListSections（分组 + 排序 + 过滤）', () => {
  it('按 +08:00 业务日分组：UTC 2026-09-27T17:30 落入 9/28', () => {
    const sections = buildEventListSections([ev({ dtStart: '2026-09-27T17:30:00+00:00' })], [])
    expect(sections).toHaveLength(1)
    expect(sections[0].day).toBe('2026-09-28')
  })

  it('组内按开始时间升序（后端不保证顺序）', () => {
    const sections = buildEventListSections(
      [
        ev({ id: 'b', dtStart: '2026-09-28T06:00:00+00:00' }),
        ev({ id: 'a', dtStart: '2026-09-28T02:00:00+00:00' }),
      ],
      [],
    )
    expect(sections[0].items.map((i) => i.key)).toEqual(['event:a:', 'event:b:'])
  })

  it('按开始日归组：本地 23:50 开始的事件归当天（不跨日拆段）', () => {
    const sections = buildEventListSections(
      [
        ev({ id: 'late', dtStart: '2026-09-28T15:50:00+00:00' }),
        ev({ id: 'next', dtStart: '2026-09-28T16:10:00+00:00' }),
      ],
      [],
    )
    expect(sections.map((s) => s.day)).toEqual(['2026-09-28', '2026-09-29'])
    expect(sections[0].items.map((i) => i.key)).toEqual(['event:late:'])
  })

  it('calendarId 过滤', () => {
    const sections = buildEventListSections(
      [ev({ id: 'a', calendarId: 'c1' }), ev({ id: 'b', calendarId: 'c2' })],
      [],
      { calendarId: 'c1' },
    )
    expect(sections[0].items.map((i) => i.key)).toEqual(['event:a:'])
  })

  it('图层按 toggles 过滤、与日程混排排序', () => {
    const sections = buildEventListSections(
      [ev({ dtStart: '2026-09-28T06:00:00+00:00' })],
      [layer({ layer: 'habits', startsAt: '2026-09-28T01:00:00+00:00' }), layer({ id: 'l2', layer: 'availability' })],
      { layerToggles: { habits: true, availability: false } },
    )
    expect(sections[0].items.map((i) => i.kind)).toEqual(['layer', 'event'])
    expect(sections[0].items[0].layer).toBe('habits')
  })

  it('坏时间戳跳过；空输入返回空数组', () => {
    expect(buildEventListSections([ev({ dtStart: 'bad' })], [layer({ startsAt: 'bad' })])).toEqual([])
    expect(buildEventListSections(undefined, undefined)).toEqual([])
  })

  it('重复系列标记 repeated；取消状态标记 cancelled', () => {
    const sections = buildEventListSections(
      [ev({ rrule: 'FREQ=WEEKLY', status: 'Cancelled' })],
      [],
    )
    const item = sections[0].items[0]
    expect(item.repeated).toBe(true)
    expect(item.cancelled).toBe(true)
  })
})
