import { describe, expect, it } from 'vitest'
import {
  addDateDays,
  calendarAxisRangeForToday,
  calendarAxisToInstant,
  chooseWallTimeCandidate,
  formatInstantInZone,
  isValidTimeZoneId,
  resolveWallTime,
  toCalendarAxisValue,
} from './event-timezone'

describe('calendar event timezone rules', () => {
  it('keeps a fixed Beijing calendar axis independent of the browser timezone', () => {
    expect(toCalendarAxisValue('2026-11-16T01:00:00.000Z')).toBe('2026-11-16T09:00:00.000Z')
    expect(calendarAxisToInstant(new Date('2026-11-16T09:00:00.000Z')).toISOString()).toBe('2026-11-16T01:00:00.000Z')
    expect(calendarAxisRangeForToday(Date.parse('2026-11-16T01:00:00.000Z'))).toEqual({
      start: '2026-11-15T16:00:00.000Z',
      end: '2026-11-16T16:00:00.000Z',
    })
  })

  it('preserves event-local wall time across daylight-saving transitions', () => {
    expect(formatInstantInZone('2026-03-08T13:00:00.000Z', 'America/New_York')).toBe('2026-03-08T09:00')
    expect(resolveWallTime('2026-03-08T02:30', 'America/New_York').status).toBe('gap')
  })

  it('exposes both instants for an ambiguous fall-back time in chronological order', () => {
    const result = resolveWallTime('2026-11-01T01:30', 'America/New_York')
    expect(result.status).toBe('ambiguous')
    if (result.status !== 'ambiguous') return
    expect(result.candidates.map((candidate) => candidate.instant)).toEqual([
      '2026-11-01T05:30:00.000Z',
      '2026-11-01T06:30:00.000Z',
    ])
    expect(chooseWallTimeCandidate(result, null)).toBeNull()
    expect(chooseWallTimeCandidate(result, result.candidates[1].instant)).toEqual(result.candidates[1])
  })

  it('accepts IANA ids and rejects Windows ids as event timezones', () => {
    expect(isValidTimeZoneId('Asia/Shanghai')).toBe(true)
    expect(isValidTimeZoneId('America/New_York')).toBe(true)
    expect(isValidTimeZoneId('UTC')).toBe(true)
    expect(isValidTimeZoneId('Eastern Standard Time')).toBe(false)
    expect(isValidTimeZoneId('Mars/Olympus')).toBe(false)
  })

  it('uses independent inclusive and exclusive all-day dates', () => {
    expect(addDateDays('2026-11-01', 1)).toBe('2026-11-02')
    expect(addDateDays('2026-11-02', -1)).toBe('2026-11-01')
    expect(addDateDays('2026-02-31', 1)).toBe('')
  })
})
