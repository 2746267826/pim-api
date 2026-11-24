import { describe, expect, it } from 'vitest'
import {
  formatEventRange,
  fromEventTimeZoneInput,
  isIanaTimeZone,
  toEventTimeZoneInput,
} from './event-time'

describe('event-time', () => {
  it('renders the wall clock in the event IANA zone instead of the browser zone', () => {
    expect(toEventTimeZoneInput('2026-11-21T11:00:00Z', 'Asia/Shanghai'))
      .toBe('2026-11-21T19:00')
    expect(formatEventRange(
      '2026-11-21T11:00:00Z',
      '2026-11-21T12:00:00Z',
      'Asia/Shanghai',
    )).toBe('2026-11-21 19:00–20:00 · Asia/Shanghai')
  })

  it('fails closed without an IANA zone instead of silently using Shanghai or the browser zone', () => {
    expect(isIanaTimeZone('Asia/Shanghai')).toBe(true)
    expect(isIanaTimeZone('Mars/Olympus')).toBe(false)
    expect(toEventTimeZoneInput('2026-11-21T11:00:00Z', null)).toBe('')
    expect(toEventTimeZoneInput('2026-11-21T11:00:00Z', 'Mars/Olympus')).toBe('')
    expect(formatEventRange(
      '2026-11-21T11:00:00Z',
      '2026-11-21T12:00:00Z',
      null,
    )).toContain('时区未知')
    expect(() => fromEventTimeZoneInput('2026-11-21T19:00', null))
      .toThrow('请先填写有效的 IANA 时区')
    expect(() => fromEventTimeZoneInput('2026-11-21T19:00', 'Mars/Olympus'))
      .toThrow('时区必须是有效的 IANA 时区')
  })

  it('converts wall-clock input to UTC and round-trips the same event wall clock', () => {
    const utc = fromEventTimeZoneInput('2026-11-21T19:00', 'Asia/Shanghai')
    expect(utc).toBe('2026-11-21T11:00:00.000Z')
    expect(toEventTimeZoneInput(utc, 'Asia/Shanghai')).toBe('2026-11-21T19:00')
  })

  it('rejects both DST gaps and ambiguous wall-clock times', () => {
    expect(() => fromEventTimeZoneInput('2026-03-08T02:30', 'America/New_York'))
      .toThrow('不存在')
    expect(() => fromEventTimeZoneInput('2026-11-01T01:30', 'America/New_York'))
      .toThrow('重复')
  })
})
