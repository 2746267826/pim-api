import { describe, expect, it } from 'vitest'
import {
  conditionalInterval,
  deferredIntervalMs,
  fixedIntervalMs,
  markScrollActivity,
  standardIntervalMs,
} from './polling'

describe('standardIntervalMs（白天 5 分钟 / 夜间 30 分钟）', () => {
  it.each([
    ['2026-09-26T06:00:00', 5 * 60_000],
    ['2026-09-26T12:00:00', 5 * 60_000],
    ['2026-09-26T23:59:00', 5 * 60_000],
    ['2026-09-26T00:00:00', 30 * 60_000],
    ['2026-09-26T02:00:00', 30 * 60_000],
    ['2026-09-26T05:59:00', 30 * 60_000],
  ])('%s → %d', (iso, expected) => {
    expect(standardIntervalMs(new Date(iso))).toBe(expected)
  })
})

describe('deferredIntervalMs（滚动结束 1 秒内强制立即刷新）', () => {
  it('滚动刚结束（<1s）→ 1 秒间隔', () => {
    markScrollActivity(Date.now() - 500)
    expect(deferredIntervalMs()).toBe(1_000)
  })

  it('静止超 1 秒 → 回落标准间隔', () => {
    markScrollActivity(Date.now() - 5_000)
    expect(deferredIntervalMs()).toBe(standardIntervalMs())
  })
})

describe('工厂函数', () => {
  it('固定轮询返回常量', () => {
    expect(fixedIntervalMs(10_000)()).toBe(10_000)
  })

  it('条件轮询：谓词不成立时返回 false（停止轮询）', () => {
    let syncing = false
    const interval = conditionalInterval(2_000, () => syncing)
    expect(interval()).toBe(false)
    syncing = true
    expect(interval()).toBe(2_000)
  })
})
