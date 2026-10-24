import { describe, expect, it } from 'vitest'
import { DUE_SOON_MS, dueUrgency, dueUrgencyView, parseDueMs } from './due-urgency'

const NOW = Date.parse('2026-10-24T12:00:00.000Z')

describe('parseDueMs', () => {
  it('解析 UTC 与带偏移字符串为同一时刻；空值与损坏值返回 null', () => {
    expect(parseDueMs('2026-10-27T12:00:00.000Z')).toBe(Date.parse('2026-10-27T12:00:00.000Z'))
    expect(parseDueMs('2026-10-27T20:00:00+08:00')).toBe(Date.parse('2026-10-27T12:00:00.000Z'))
    expect(parseDueMs(null)).toBeNull()
    expect(parseDueMs('')).toBeNull()
    expect(parseDueMs('not-a-date')).toBeNull()
  })
})

describe('dueUrgency（api#491：三档 + 状态边界）', () => {
  it('过去与 due == now 都是已逾期', () => {
    expect(dueUrgency('2026-10-24T11:59:59.999Z', NOW)).toBe('overdue')
    expect(dueUrgency('2026-10-24T12:00:00.000Z', NOW)).toBe('overdue')
  })

  it('正好 3 天是即将到期，3 天 + 1ms 是远期', () => {
    expect(dueUrgency(new Date(NOW + DUE_SOON_MS).toISOString(), NOW)).toBe('dueSoon')
    expect(dueUrgency(new Date(NOW + DUE_SOON_MS + 1).toISOString(), NOW)).toBe('distant')
  })

  it('同一 UTC 时刻的不同写法（Z 与 +00:00）分档一致', () => {
    const due = new Date(NOW + 3 * 60 * 60 * 1000).toISOString()
    expect(dueUrgency(due, NOW)).toBe('dueSoon')
    expect(dueUrgency(due.replace('Z', '+00:00'), NOW)).toBe('dueSoon')
  })

  it('已完成/已取消一律不参与紧迫度；损坏 due 明确无效', () => {
    expect(dueUrgency('2000-01-01T00:00:00Z', NOW, true)).toBe('inactive')
    expect(dueUrgency('2099-01-01T00:00:00Z', NOW, true)).toBe('inactive')
    expect(dueUrgency('broken-due', NOW)).toBe('invalid')
    expect(dueUrgency(null, NOW)).toBe('invalid')
  })

  it('展示文案不只依赖颜色：逾期/临近/状态弱化可读且可测', () => {
    expect(dueUrgencyView('overdue')).toEqual({ className: 'text-crit', suffix: ' · 已逾期' })
    expect(dueUrgencyView('dueSoon')).toEqual({ className: 'text-warn', suffix: ' · 即将到期' })
    expect(dueUrgencyView('distant').className).toBe('text-text-4')
    expect(dueUrgencyView('inactive').className).toBe('text-text-4')
    expect(dueUrgencyView('invalid').className).toBe('text-crit')
  })
})
