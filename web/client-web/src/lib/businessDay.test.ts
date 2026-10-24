import { describe, expect, it } from 'vitest'
import { businessDayOf, businessDayRange, businessDayShift } from './businessDay'

describe('businessDayOf（Asia/Shanghai 04:00 切分）', () => {
  it('04:00 整点起算新业务日', () => {
    expect(businessDayOf(new Date('2026-09-26T04:00:00+08:00'))).toBe('2026-09-26')
  })

  it('凌晨 0–4 点归前一天', () => {
    expect(businessDayOf(new Date('2026-09-26T03:59:59+08:00'))).toBe('2026-09-25')
    expect(businessDayOf(new Date('2026-09-26T00:00:00+08:00'))).toBe('2026-09-25')
  })

  it('UTC 输入换算正确（04:00 CST = 前一日 20:00Z）', () => {
    expect(businessDayOf(new Date('2026-09-25T20:00:00Z'))).toBe('2026-09-26')
    expect(businessDayOf(new Date('2026-09-25T19:59:59Z'))).toBe('2026-09-25')
  })
})

describe('businessDayRange', () => {
  it("'2026-09-26' → [09-25 20:00Z, 09-26 20:00Z)", () => {
    const r = businessDayRange('2026-09-26')
    expect(r.startUtc).toBe('2026-09-25T20:00:00.000Z')
    expect(r.endUtc).toBe('2026-09-26T20:00:00.000Z')
  })

  it('非法日期抛错', () => {
    expect(() => businessDayRange('2026/09/26')).toThrow()
  })
})

describe('businessDayShift', () => {
  it('向前/向后平移', () => {
    expect(businessDayShift('2026-09-26', -7)).toBe('2026-09-19')
    expect(businessDayShift('2026-09-01', -1)).toBe('2026-08-31')
    expect(businessDayShift('2026-09-30', 1)).toBe('2026-10-01')
  })
})
